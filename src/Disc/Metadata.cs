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
        public string Format = "";          // only set for search results, where it tells a CD from a vinyl or digital release
        public int MediumPosition = 1, MediumCount = 1, TrackCount;
        public List<(string title, string artist, TimeSpan length)> Tracks = new List<(string, string, TimeSpan)>();

        public string Year => Date.Length >= 4 ? Date.Substring(0, 4) : Date;

        public override string ToString()
        {
            var bits = new List<string> { $"{Artist} – {Title}" };
            if (Date.Length > 0) bits.Add(Date);
            if (Country.Length > 0) bits.Add(Country);
            if (MediumCount > 1) bits.Add($"disc {MediumPosition}/{MediumCount}");
            if (Format.Length > 0) bits.Add(TrackCount > 0 ? $"{Format}, {TrackCount} tracks" : Format);
            return string.Join("  ·  ", bits);
        }
    }

    /// <summary>MusicBrainz disc IDs, release lookup and Cover Art Archive images.</summary>
    public static class MusicBrainz
    {
        // MusicBrainz blocks clients that don't say who they are, so this names the app and where to find it.
        const string UserAgent = "discphew/0.1 ( https://github.com/qweeren/discphew )";

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

        internal static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
            return wc;
        }

        static readonly object pace = new object();
        static DateTime lastRequest = DateTime.MinValue;

        /// <summary>
        /// A MusicBrainz web service call: at most one a second (their limit, shared by every caller in the app),
        /// backing off and retrying when it says we're going too fast. Null if MusicBrainz has no such thing.
        /// </summary>
        static string Get(string url)
        {
            for (int attempt = 0; ; attempt++)
            {
                lock (pace)
                {
                    var wait = lastRequest.AddSeconds(1.1) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) System.Threading.Thread.Sleep(wait);
                    lastRequest = DateTime.UtcNow;
                }
                try
                {
                    using var wc = Client();
                    wc.Headers[HttpRequestHeader.Accept] = "application/json";
                    return wc.DownloadString(url);
                }
                catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode is HttpStatusCode code && (code == HttpStatusCode.ServiceUnavailable || (int)code == 429) && attempt < 3)
                {
                    System.Threading.Thread.Sleep(1500 * (attempt + 1));    // rate limited: back off and retry
                }
            }
        }

        /// <summary>
        /// The disc that tracks of these lengths (in sectors) make when they're burned back to back from LBA 0,
        /// the way the Burner lays them out, so files can be looked up by disc ID like the CD they were ripped from.
        /// </summary>
        public static DiscInfo LayOut(IEnumerable<long> sectors)
        {
            var d = new DiscInfo();
            int lba = 0;
            foreach (var n in sectors)
            {
                int length = (int)Math.Max(n, 4 * 75);      // the Burner pads tracks to at least 4 seconds
                d.Tracks.Add(new TrackInfo { Number = d.Tracks.Count + 1, StartLba = lba, EndLba = lba + length, IsAudio = true });
                lba += length;
            }
            d.FirstTrack = 1;
            d.LastTrack = d.Tracks.Count;
            d.LeadOutLba = lba;
            return d;
        }

        /// <summary>
        /// One release by its MusicBrainz ID (as Picard tags files: MUSICBRAINZ_ALBUMID), with the tracks of the
        /// medium numbered <paramref name="disc"/>, or else the one with <paramref name="tracks"/> tracks. Null if there's no such release.
        /// </summary>
        public static ReleaseMatch Release(string id, int disc = 0, int tracks = 0)
        {
            if (!Guid.TryParse(id?.Trim(), out var guid)) return null;
            string json = Get($"https://musicbrainz.org/ws/2/release/{guid}?inc=recordings+artist-credits+release-groups&fmt=json");
            if (json == null) return null;
            var rel = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            var media = A(rel, "media").ToList();
            var medium = media.FirstOrDefault(m => disc > 0 && S(m, "position") == disc.ToString())
                         ?? media.FirstOrDefault(m => tracks > 0 && A(m, "tracks").Count() == tracks)
                         ?? media.FirstOrDefault();
            return Match(rel, medium, media.Count);
        }

        /// <summary>
        /// Releases whose title and artist match at least <paramref name="minScore"/> (of 100), best first, for the user
        /// to pick from. Search results carry no track lists; <see cref="Release"/> fetches one.
        /// </summary>
        public static List<ReleaseMatch> Search(string artist, string album, int tracks = 0, int minScore = 60)
        {
            var list = new List<ReleaseMatch>();
            if (string.IsNullOrWhiteSpace(album)) return list;
            static string Phrase(string s) => "\"" + s.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string query = "release:" + Phrase(album) + (string.IsNullOrWhiteSpace(artist) || artist == "Various Artists" ? "" : " AND artist:" + Phrase(artist));
            string json = Get("https://musicbrainz.org/ws/2/release/?limit=25&fmt=json&query=" + Uri.EscapeDataString(query));
            if (json == null) return list;
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            foreach (var rel in A(root, "releases"))
            {
                if (!int.TryParse(S(rel, "score"), out int score) || score < minScore) continue;
                var media = A(rel, "media").ToList();
                var medium = media.FirstOrDefault(m => tracks > 0 && S(m, "track-count") == tracks.ToString()) ?? media.FirstOrDefault();
                var m = Match(rel, medium, media.Count);
                m.Format = S(medium, "format");
                m.TrackCount = int.TryParse(S(medium, "track-count"), out int n) ? n : 0;
                list.Add(m);
            }
            return list;
        }

        public static List<ReleaseMatch> Lookup(DiscInfo d)
        {
            string id = DiscId(d);
            if (id.Length == 0) return new List<ReleaseMatch>();
            string json = Get($"https://musicbrainz.org/ws/2/discid/{id}?toc={TocParam(d)}&cdstubs=no&inc=recordings+artist-credits+release-groups&fmt=json");
            if (json == null) return new List<ReleaseMatch>();
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
                list.Add(Match(rel, medium, media.Count));
            }
            return list;
        }

        /// <summary>A release as MusicBrainz describes it, with the tracks of <paramref name="medium"/>.</summary>
        static ReleaseMatch Match(Dictionary<string, object> rel, Dictionary<string, object> medium, int mediumCount)
        {
            var m = new ReleaseMatch
            {
                Id = S(rel, "id"),
                Title = S(rel, "title"),
                Artist = Credit(rel),
                Date = S(rel, "date"),
                Country = S(rel, "country"),
                Barcode = S(rel, "barcode"),
                ReleaseGroupId = S(O(rel, "release-group"), "id"),
                MediumCount = Math.Max(1, mediumCount),
                MediumPosition = int.TryParse(S(medium, "position"), out var pos) ? pos : 1,
            };
            foreach (var t in A(medium, "tracks"))
            {
                var credit = Credit(t);
                var length = long.TryParse(S(t, "length"), out long ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
                m.Tracks.Add((S(t, "title"), credit.Length > 0 ? credit : m.Artist, length));
            }
            m.TrackCount = m.Tracks.Count;
            return m;
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

        /// <summary>
        /// Front cover for an album known only by name (CD-Text, tags): searches MusicBrainz releases and takes
        /// the first close match the Cover Art Archive has art for. Null if there's none or the lookup fails.
        /// </summary>
        public static byte[] SearchCover(string artist, string album)
        {
            try
            {
                // Releases of one album share a release group; try each group once, best-scoring first.
                var tried = new HashSet<string>();
                foreach (var m in Search(artist, album, minScore: 90).Take(5))
                {
                    if (!tried.Add(m.ReleaseGroupId.Length > 0 ? m.ReleaseGroupId : m.Id)) continue;
                    var cover = FetchCover(m);
                    if (cover != null) return cover;
                }
            }
            catch (WebException) { }
            catch (ArgumentException) { }       // malformed JSON
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
