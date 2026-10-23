using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Discphew.Disc
{
    /// <summary>One piece of artwork for a release: a Cover Art Archive scan, or a front cover from iTunes.</summary>
    public sealed class ArtImage
    {
        public string Id = "";                  // unique across sources; names the cached files
        public List<string> Types = new List<string>();     // Front, Back, Tray, Booklet, Medium, Spine, Obi, ...
        public string Comment = "";
        public string Source = "Cover Art Archive";
        public string Original = "";
        public readonly SortedDictionary<int, string> Sizes = new SortedDictionary<int, string>();      // thumbnails by pixel size

        public bool Is(string type) => Types.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));

        /// <summary>The smallest copy at least <paramref name="size"/> pixels across, or the original (0: always the original).</summary>
        public string Url(int size)
        {
            if (size > 0)
                foreach (var kv in Sizes)
                    if (kv.Key >= size) return kv.Value;
            return Original.Length > 0 ? Original : Sizes.Values.LastOrDefault() ?? "";
        }

        public override string ToString()
        {
            string kind = Types.Count > 0 ? string.Join(" + ", Types) : "Image";
            return Comment.Length > 0 ? $"{kind} · {Comment}" : kind;
        }
    }

    /// <summary>
    /// Every image the Cover Art Archive has for a release (front, back, tray, booklet pages, the disc itself),
    /// and big front covers from the iTunes Search API. Downloads are cached in %APPDATA%\discphew\art.
    /// </summary>
    public static class CoverArt
    {
        static string Dir => Path.Combine(Settings.Folder, "art");

        /// <summary>
        /// The release's own images, or if it has none, those of the release group (any pressing of the album).
        /// Empty if neither has any; throws <see cref="WebException"/> if the archive can't be reached.
        /// </summary>
        public static List<ArtImage> List(ReleaseMatch r)
        {
            foreach (var (kind, id) in new[] { ("release", r.Id), ("release-group", r.ReleaseGroupId) })
            {
                if (id.Length == 0) continue;
                string json = Json(kind, id);
                if (json == null) continue;
                var images = Parse(json);
                if (images.Count > 0) return images;
            }
            return new List<ArtImage>();
        }

        /// <summary>The archive's index of one release or release group, from the cache for a day, then from the web. Null if it has none.</summary>
        static string Json(string kind, string id)
        {
            string cached = Path.Combine(Dir, Safe(kind + "-" + id) + ".json");
            try { if (File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < TimeSpan.FromDays(1)) return File.ReadAllText(cached, Encoding.UTF8); }
            catch (IOException) { }
            string json;
            try
            {
                using var wc = MusicBrainz.Client();
                wc.Headers[HttpRequestHeader.Accept] = "application/json";
                json = wc.DownloadString($"https://coverartarchive.org/{kind}/{id}");
            }
            catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            try { Directory.CreateDirectory(Dir); File.WriteAllText(cached, json, Encoding.UTF8); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return json;
        }

        internal static List<ArtImage> Parse(string json)
        {
            var list = new List<ArtImage>();
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("images", out var v) || !(v is object[] images)) return list;
            foreach (var o in images.OfType<Dictionary<string, object>>())
            {
                var img = new ArtImage
                {
                    Id = "caa-" + Str(o, "id"),
                    Comment = Str(o, "comment"),
                    Original = Https(Str(o, "image")),
                };
                if (o.TryGetValue("types", out var types) && types is object[] t) img.Types.AddRange(t.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
                if (img.Types.Count == 0 && Str(o, "front") == "True") img.Types.Add("Front");
                if (o.TryGetValue("thumbnails", out var th) && th is Dictionary<string, object> thumbs)
                    foreach (var kv in thumbs)
                    {
                        // Numbered sizes, plus the older "small" (250) and "large" (500) names for the same files.
                        int size = int.TryParse(kv.Key, out int n) ? n : kv.Key == "small" ? 250 : kv.Key == "large" ? 500 : 0;
                        if (size > 0 && kv.Value != null && !img.Sizes.ContainsKey(size)) img.Sizes[size] = Https(kv.Value.ToString());
                    }
                if (img.Original.Length > 0 || img.Sizes.Count > 0) list.Add(img);
            }
            // Front first, then the order a jewel case is put together in; the archive's own order within a type (booklet pages).
            string[] order = { "Front", "Back", "Tray", "Medium", "Booklet", "Spine" };
            int Rank(ArtImage i) { int r = Array.FindIndex(order, i.Is); return r < 0 ? order.Length : r; }
            return list.Select((img, i) => (img, i)).OrderBy(x => Rank(x.img)).ThenBy(x => x.i).Select(x => x.img).ToList();
        }

        /// <summary>
        /// The best front cover iTunes has for an album (usually 3000 px), or null. Matches on the album and artist
        /// names, ignoring case, punctuation and "(Deluxe Edition)"-style suffixes.
        /// </summary>
        public static ArtImage Itunes(string artist, string album)
        {
            if (string.IsNullOrWhiteSpace(album)) return null;
            string json;
            try
            {
                using var wc = MusicBrainz.Client();
                json = wc.DownloadString("https://itunes.apple.com/search?media=music&entity=album&limit=15&term=" + Uri.EscapeDataString((artist + " " + album).Trim()));
            }
            catch (WebException) { return null; }
            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("results", out var v) || !(v is object[] results)) return null;
            string wantAlbum = Plain(album), wantArtist = Plain(artist ?? "");
            var hit = results.OfType<Dictionary<string, object>>()
                .Where(r => Str(r, "artworkUrl100").Contains("100x100"))
                .Select(r => (r, album: Plain(Str(r, "collectionName")), artist: Plain(Str(r, "artistName"))))
                .Where(x => x.album.Length > 0 && (x.album == wantAlbum || x.album.StartsWith(wantAlbum) || wantAlbum.StartsWith(x.album)))
                .OrderBy(x => x.album == wantAlbum ? 0 : 1)
                .ThenBy(x => wantArtist.Length == 0 || x.artist == wantArtist ? 0 : x.artist.Contains(wantArtist) || wantArtist.Contains(x.artist) ? 1 : 2)
                .Select(x => x.r).FirstOrDefault();
            if (hit == null) return null;
            string url = Str(hit, "artworkUrl100");
            var img = new ArtImage { Id = "itunes-" + Str(hit, "collectionId"), Source = "iTunes", Original = url.Replace("100x100", "3000x3000") };
            img.Types.Add("Front");
            img.Sizes[600] = url.Replace("100x100", "600x600");
            img.Sizes[1200] = url.Replace("100x100", "1200x1200");
            return img;
        }

        /// <summary>
        /// The image at the size <see cref="ArtImage.Url"/> picks (0: the original), from the cache or downloaded into it.
        /// Archive scans redirect to archive.org; WebClient follows that. Null if it can't be had.
        /// </summary>
        public static byte[] Fetch(ArtImage img, int size)
        {
            string url = img.Url(size);
            if (url.Length == 0) return null;
            string path = Path.Combine(Dir, Safe(img.Id) + "-" + (url == img.Original ? "original" : size.ToString()) + ".img");
            try { if (File.Exists(path)) return File.ReadAllBytes(path); }
            catch (IOException) { }
            byte[] data;
            try
            {
                using var wc = MusicBrainz.Client();
                data = wc.DownloadData(url);
            }
            catch (WebException) { return null; }
            if (data.Length < 1000) return null;            // an error page, not a picture
            try { Directory.CreateDirectory(Dir); File.WriteAllBytes(path, data); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return data;
        }

        static string Str(Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v != null ? v.ToString() : "";

        // The archive lists its URLs as http://; the same paths work (and redirect) over https.
        static string Https(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url.Substring(7) : url;

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

        /// <summary>A name reduced to lower-case letters and digits, without a trailing "(…)" or "[…]" (editions, remasters).</summary>
        internal static string Plain(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s*[\(\[][^\)\]]*[\)\]]\s*$", "");
            return string.Concat(s.Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        }
    }
}
