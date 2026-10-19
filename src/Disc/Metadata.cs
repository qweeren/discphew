using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Discphew.Disc
{
    public sealed class ReleaseMatch
    {
        public string Id = "", Title = "", Artist = "", Date = "", Country = "", ReleaseGroupId = "", Barcode = "";
        public int MediumPosition = 1, MediumCount = 1;
        public List<(string title, string artist)> Tracks = new List<(string, string)>();

        public string Year => Date.Length >= 4 ? Date.Substring(0, 4) : Date;

        public override string ToString()
        {
            var bits = new List<string> { $"{Artist} – {Title}" };
            if (Date.Length > 0) bits.Add(Date);
            if (Country.Length > 0) bits.Add(Country);
            if (MediumCount > 1) bits.Add($"disc {MediumPosition}/{MediumCount}");
            return string.Join("  ·  ", bits);
        }
    }

    /// <summary>MusicBrainz disc IDs, release lookup and Cover Art Archive images.</summary>
    public static class MusicBrainz
    {
        const string UserAgent = "discphew/0.1 (open-source CD/DVD utility)";

        static List<TrackInfo> IdTracks(DiscInfo d, out int leadOut)
        {
            // Enhanced CDs: the data session isn't part of the audio disc ID.
            var tracks = d.Tracks.Where(t => t.IsAudio || t.Session == 1).ToList();
            while (tracks.Count > 0 && !tracks[tracks.Count - 1].IsAudio && tracks[tracks.Count - 1].Session > 1) tracks.RemoveAt(tracks.Count - 1);
            leadOut = tracks.Count < d.Tracks.Count ? tracks.Last().EndLba : d.LeadOutLba;
            return tracks;
        }

        public static string DiscId(DiscInfo d)
        {
            var tracks = IdTracks(d, out int leadOut);
            if (tracks.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append(tracks.First().Number.ToString("X2"));
            sb.Append(tracks.Last().Number.ToString("X2"));
            sb.Append((leadOut + 150).ToString("X8"));
            for (int i = 1; i < 100; i++)
            {
                var t = tracks.FirstOrDefault(x => x.Number == i);
                sb.Append((t == null ? 0 : t.StartLba + 150).ToString("X8"));
            }
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }

        static string TocParam(DiscInfo d)
        {
            var tracks = IdTracks(d, out int leadOut);
            var parts = new List<int> { tracks.First().Number, tracks.Last().Number, leadOut + 150 };
            parts.AddRange(tracks.Select(t => t.StartLba + 150));
            return string.Join("+", parts);
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            return wc;
        }

        public static List<ReleaseMatch> Lookup(DiscInfo d)
        {
            string id = DiscId(d);
            if (id.Length == 0) return new List<ReleaseMatch>();
            string url = $"https://musicbrainz.org/ws/2/discid/{id}?toc={TocParam(d)}&cdstubs=no&inc=recordings+artist-credits+release-groups&fmt=json";
            string json = null;
            for (int attempt = 0; json == null; attempt++)
            {
                try
                {
                    using var wc = Client();
                    wc.Headers[HttpRequestHeader.Accept] = "application/json";
                    json = wc.DownloadString(url);
                }
                catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                {
                    return new List<ReleaseMatch>();
                }
                catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 3)
                {
                    System.Threading.Thread.Sleep(1500 * (attempt + 1));    // MusicBrainz rate limit: back off and retry
                }
            }
            return Parse(json, id, d.AudioTracks.Count());
        }

        static string S(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v != null ? v.ToString() : "";
        static Dictionary<string, object> O(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        static IEnumerable<Dictionary<string, object>> A(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is object[] arr ? arr.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();

        static string Credit(Dictionary<string, object> o) =>
            string.Concat(A(o, "artist-credit").Select(c => S(c, "name") + S(c, "joinphrase")));

        static List<ReleaseMatch> Parse(string json, string discId, int audioTracks)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            var list = new List<ReleaseMatch>();
            foreach (var rel in A(root, "releases"))
            {
                var media = A(rel, "media").ToList();
                var medium = media.FirstOrDefault(m => A(m, "discs").Any(x => S(x, "id") == discId))
                             ?? media.FirstOrDefault(m => A(m, "tracks").Count() == audioTracks)
                             ?? media.FirstOrDefault();
                if (medium == null) continue;
                var m = new ReleaseMatch
                {
                    Id = S(rel, "id"),
                    Title = S(rel, "title"),
                    Artist = Credit(rel),
                    Date = S(rel, "date"),
                    Country = S(rel, "country"),
                    Barcode = S(rel, "barcode"),
                    ReleaseGroupId = S(O(rel, "release-group"), "id"),
                    MediumCount = media.Count,
                    MediumPosition = int.TryParse(S(medium, "position"), out var pos) ? pos : 1,
                };
                foreach (var t in A(medium, "tracks"))
                {
                    var credit = Credit(t);
                    m.Tracks.Add((S(t, "title"), credit.Length > 0 ? credit : m.Artist));
                }
                list.Add(m);
            }
            return list;
        }

        public static byte[] FetchCover(ReleaseMatch r)
        {
            foreach (var url in new[] { $"https://coverartarchive.org/release/{r.Id}/front-500", $"https://coverartarchive.org/release-group/{r.ReleaseGroupId}/front-500" })
            {
                if (url.Contains("//front") || url.EndsWith("//front-500")) continue;
                try
                {
                    using var wc = Client();
                    var bytes = wc.DownloadData(url);
                    if (bytes.Length > 1000) return bytes;
                }
                catch (WebException) { }
            }
            return null;
        }

        public static void Apply(DiscInfo d, ReleaseMatch r)
        {
            d.AlbumTitle = r.Title;
            d.AlbumArtist = r.Artist;
            d.Year = r.Year;
            d.MusicBrainzReleaseId = r.Id;
            var audio = d.AudioTracks.ToList();
            for (int i = 0; i < audio.Count && i < r.Tracks.Count; i++)
            {
                audio[i].Title = r.Tracks[i].title;
                audio[i].Artist = r.Tracks[i].artist;
            }
        }
    }

    /// <summary>Remembers album metadata and cover art per disc (by MusicBrainz disc ID).</summary>
    public static class MetadataCache
    {
        static string Dir => Path.Combine(Settings.Folder, "discs");

        static string SafeId(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

        public static string CoverPathFor(DiscInfo d)
        {
            string id = MusicBrainz.DiscId(d);
            return id.Length == 0 ? "" : Path.Combine(Dir, SafeId(id) + ".jpg");
        }

        public static void SaveCover(DiscInfo d, byte[] jpeg)
        {
            var path = CoverPathFor(d);
            if (path.Length == 0 || jpeg == null) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(path, jpeg);
            d.CoverPath = path;
        }

        public static void Save(DiscInfo d)
        {
            string id = MusicBrainz.DiscId(d);
            if (id.Length == 0) return;
            var lines = new List<string>
            {
                "album=" + d.AlbumTitle, "artist=" + d.AlbumArtist, "year=" + d.Year, "genre=" + d.Genre, "mbid=" + d.MusicBrainzReleaseId,
            };
            foreach (var t in d.AudioTracks)
            {
                lines.Add($"t{t.Number}.title={t.Title}");
                lines.Add($"t{t.Number}.artist={t.Artist}");
            }
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(Path.Combine(Dir, SafeId(id) + ".txt"), lines.Select(l => l.Replace("\r", " ").Replace("\n", " ")), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Fills in remembered metadata. Returns true if anything was found.</summary>
        public static bool Load(DiscInfo d)
        {
            string id = MusicBrainz.DiscId(d);
            if (id.Length == 0) return false;
            var cover = CoverPathFor(d);
            if (File.Exists(cover)) d.CoverPath = cover;
            var file = Path.Combine(Dir, SafeId(id) + ".txt");
            if (!File.Exists(file)) return false;
            var kv = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) kv[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            string G(string k) => kv.TryGetValue(k, out var v) ? v : "";
            d.AlbumTitle = G("album"); d.AlbumArtist = G("artist"); d.Year = G("year"); d.Genre = G("genre"); d.MusicBrainzReleaseId = G("mbid");
            foreach (var t in d.AudioTracks)
            {
                var title = G($"t{t.Number}.title");
                var artist = G($"t{t.Number}.artist");
                if (title.Length > 0) t.Title = title;
                if (artist.Length > 0) t.Artist = artist;
            }
            return true;
        }
    }
}
