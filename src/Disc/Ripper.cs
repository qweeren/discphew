using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Discphew.Audio;

namespace Discphew.Disc
{
    public sealed class RipFormat
    {
        public string Name = "", Extension = "", FfmpegArgs = "";
        public bool NeedsFfmpeg, Lossless, CoverArt = true;
        public override string ToString() => Name;

        public static readonly RipFormat[] All =
        {
            new RipFormat { Name = "FLAC — lossless", Extension = ".flac", Lossless = true },
            new RipFormat { Name = "WAV — uncompressed", Extension = ".wav", Lossless = true, CoverArt = false },
            new RipFormat { Name = "ALAC — lossless (.m4a)", Extension = ".m4a", NeedsFfmpeg = true, Lossless = true, FfmpegArgs = "-c:a alac" },
            new RipFormat { Name = "MP3 — V0 (~245 kbps)", Extension = ".mp3", NeedsFfmpeg = true, FfmpegArgs = "-c:a libmp3lame -q:a 0 -id3v2_version 3" },
            new RipFormat { Name = "MP3 — 320 kbps", Extension = ".mp3", NeedsFfmpeg = true, FfmpegArgs = "-c:a libmp3lame -b:a 320k -id3v2_version 3" },
            new RipFormat { Name = "AAC — 256 kbps (.m4a)", Extension = ".m4a", NeedsFfmpeg = true, FfmpegArgs = "-c:a aac -b:a 256k" },
            new RipFormat { Name = "Opus — 160 kbps", Extension = ".opus", NeedsFfmpeg = true, CoverArt = false, FfmpegArgs = "-c:a libopus -b:a 160k" },
            new RipFormat { Name = "Ogg Vorbis — q6", Extension = ".ogg", NeedsFfmpeg = true, CoverArt = false, FfmpegArgs = "-c:a libvorbis -q:a 6" },
        };
    }

    public sealed class RipOptions
    {
        public char Drive;
        public string Folder = "";
        public string Template = DefaultTemplate;
        public RipFormat Format = RipFormat.All[0];
        public bool Verify = true;
        public bool SaveCoverFile = true;
        public bool WriteCueAndLog = true;
        public bool AccurateRip = true;
        /// <summary>Drive read offset in samples; null when it isn't known (the rip then isn't corrected).</summary>
        public int? ReadOffset;
        public byte[] Cover;

        public const string DefaultTemplate = @"{albumartist}\{year} - {album}\{track} - {title}";
    }

    public sealed class TrackResult
    {
        public TrackInfo Track;
        public string Path = "";
        public uint Crc;
        public int ReadErrors;
        public int Unverified;
        public string Error = "";
        public bool Ok => Error.Length == 0;

        public uint ArV1, ArV2;
        public ArState Ar;
        /// <summary>Confidence of the matching AccurateRip entry, and the highest confidence of any entry for the track.</summary>
        public int ArConfidence, ArBest;
    }

    public sealed class OffsetMatch
    {
        public int Offset;
        public int TracksMatched, TracksChecked;
        public int Confidence;
    }

    public static class Crc32
    {
        static readonly uint[] table = Enumerable.Range(0, 256).Select(i =>
        {
            uint c = (uint)i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Update(uint crc, byte[] b, int offset, int count)
        {
            crc = ~crc;
            for (int i = 0; i < count; i++) crc = table[(crc ^ b[offset + i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }

    /// <summary>Reads audio tracks and writes them as files, verifying each read with a second pass.</summary>
    public static class AudioRipper
    {
        public static string BuildPath(DiscInfo d, TrackInfo t, RipOptions o)
        {
            int total = d.AudioTracks.Count();
            string artist = t.Artist.Length > 0 ? t.Artist : d.AlbumArtist;
            string Tok(string name) => name switch
            {
                "albumartist" => d.AlbumArtist.Length > 0 ? d.AlbumArtist : artist,
                "artist" => artist,
                "album" => d.AlbumTitle.Length > 0 ? d.AlbumTitle : "Unknown album",
                "year" => d.Year.Length > 0 ? d.Year : "0000",
                "genre" => d.Genre,
                "track" => t.Number.ToString(total >= 100 ? "000" : "00"),
                "title" => t.Title.Length > 0 ? t.Title : $"Track {t.Number:00}",
                _ => "{" + name + "}",
            };
            var parts = o.Template.Replace('/', '\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var clean = parts.Select(part =>
            {
                var sb = new StringBuilder();
                for (int i = 0; i < part.Length; i++)
                {
                    if (part[i] == '{')
                    {
                        int end = part.IndexOf('}', i);
                        if (end > i)
                        {
                            sb.Append(Tok(part.Substring(i + 1, end - i - 1).ToLowerInvariant()).Replace('\\', '-').Replace('/', '-'));
                            i = end;
                            continue;
                        }
                    }
                    sb.Append(part[i]);
                }
                return Discphew.UI.Ui.SafeName(sb.ToString());
            }).ToList();
            return Path.Combine(o.Folder, Path.Combine(clean.ToArray()) + o.Format.Extension);
        }

        static List<KeyValuePair<string, string>> Tags(DiscInfo d, TrackInfo t)
        {
            int total = d.AudioTracks.Count();
            return new List<KeyValuePair<string, string>>
            {
                new("TITLE", t.Title),
                new("ARTIST", t.Artist.Length > 0 ? t.Artist : d.AlbumArtist),
                new("ALBUM", d.AlbumTitle),
                new("ALBUMARTIST", d.AlbumArtist),
                new("TRACKNUMBER", t.Number.ToString()),
                new("TRACKTOTAL", total.ToString()),
                new("DATE", d.Year),
                new("GENRE", d.Genre),
                new("ISRC", t.Isrc),
                new("MUSICBRAINZ_ALBUMID", d.MusicBrainzReleaseId),
                new("MUSICBRAINZ_DISCID", MusicBrainz.DiscId(d)),
            };
        }

        static List<KeyValuePair<string, string>> FfTags(DiscInfo d, TrackInfo t)
        {
            int total = d.AudioTracks.Count();
            return new List<KeyValuePair<string, string>>
            {
                new("title", t.Title), new("artist", t.Artist.Length > 0 ? t.Artist : d.AlbumArtist),
                new("album", d.AlbumTitle), new("album_artist", d.AlbumArtist),
                new("track", $"{t.Number}/{total}"), new("date", d.Year), new("genre", d.Genre),
            };
        }

        /// <param name="progress">(index into <paramref name="tracks"/>, fraction of that track, status text)</param>
        public static List<TrackResult> Rip(DiscInfo d, IList<TrackInfo> tracks, RipOptions o,
            Action<int, double, string> progress, Func<bool> cancelled)
        {
            var results = new List<TrackResult>();
            string coverTemp = null;
            if (o.Cover != null && o.Format.NeedsFfmpeg && o.Format.CoverArt)
            {
                coverTemp = Path.Combine(Path.GetTempPath(), "discphew-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
                File.WriteAllBytes(coverTemp, o.Cover);
            }

            var audio = d.AudioTracks.ToList();
            var readable = Readable(d);
            List<ArPressing> ar = null;
            string arError = null;
            if (o.AccurateRip && tracks.Count > 0)
            {
                progress(0, 0, "Looking up AccurateRip");
                try { ar = AccurateRip.Fetch(d); }
                catch (Exception e) { arError = e.Message; }
            }

            try
            {
                using var drive = new CdDrive(o.Drive);
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (cancelled()) break;
                    var t = tracks[i];
                    var r = new TrackResult { Track = t, Path = BuildPath(d, t, o) };
                    results.Add(r);
                    int idx = i;
                    try
                    {
                        var data = ReadTrack(Reader(drive), t, o.ReadOffset ?? 0, readable, o.Verify, r, (f, s) => progress(idx, f * 0.92, s), cancelled);
                        if (data == null) { r.Error = "Cancelled"; break; }
                        progress(i, 0.93, "Encoding");
                        r.Crc = Crc32.Update(0, data, 0, data.Length);
                        int n = audio.IndexOf(t);
                        AccurateRip.Checksums(data, data.Length, n == 0, n == audio.Count - 1, out r.ArV1, out r.ArV2);
                        r.Ar = ar == null ? (arError != null ? ArState.LookupFailed : ArState.NotChecked) : ar.Count == 0 ? ArState.NotInDatabase : ArState.Mismatch;
                        if (r.Ar == ArState.Mismatch)
                        {
                            (r.ArConfidence, r.ArBest) = AccurateRip.Match(ar, n, r.ArV1, r.ArV2);
                            if (r.ArConfidence > 0) r.Ar = ArState.Match;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(r.Path));
                        Encode(d, t, o, data, r.Path, coverTemp);
                        progress(i, 1, r.Ar == ArState.Match ? $"AccurateRip (conf {r.ArConfidence})" : r.ReadErrors > 0 ? $"{r.ReadErrors} unreadable sectors" : r.Unverified > 0 ? $"{r.Unverified} sectors unverified" : "Done");
                    }
                    catch (Exception e)
                    {
                        r.Error = e.Message;
                        progress(i, 1, "Failed: " + e.Message);
                    }
                }
            }
            finally
            {
                if (coverTemp != null) try { File.Delete(coverTemp); } catch { }
            }

            var okResults = results.Where(x => x.Ok).ToList();
            if (okResults.Count > 0)
            {
                string folder = Path.GetDirectoryName(okResults[0].Path);
                if (o.SaveCoverFile && o.Cover != null)
                    try { File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), o.Cover); } catch { }
                if (o.WriteCueAndLog)
                {
                    try { WriteCue(d, okResults, folder); } catch { }
                    try { WriteLog(d, results, o, folder, ar, arError); } catch { }
                }
            }
            return results;
        }

        /// <summary>
        /// The sectors audio can be read from: the audio tracks, plus the lead-in pregap when the disc starts with audio.
        /// Lead-in, lead-out, data tracks and an Enhanced CD's session gap aren't readable as audio.
        /// </summary>
        internal static (int min, int max) Readable(DiscInfo d)
        {
            var audio = d.AudioTracks.ToList();
            if (audio.Count == 0) return (0, 0);
            return (d.Tracks[0].IsAudio ? 0 : audio[0].StartLba, audio[audio.Count - 1].EndLba);
        }

        /// <summary>Reads <paramref name="count"/> CD-DA sectors; returns how many couldn't be read (those are silenced).</summary>
        internal delegate int SectorReader(int lba, int count, byte[] dest, int destOffset);

        static SectorReader Reader(CdDrive drive) => (lba, count, dest, destOffset) => drive.ReadAudioRobust(lba, count, dest, destOffset);

        /// <summary>Reads sectors, filling the parts outside <paramref name="readable"/> with silence. Returns the unreadable count.</summary>
        internal static int ReadRange(SectorReader read, int lba, int count, byte[] dest, int destOffset, (int min, int max) readable)
        {
            const int size = CdDrive.RawSectorSize;
            int from = Math.Max(lba, readable.min), to = Math.Min(lba + count, readable.max);
            if (to <= from) { Array.Clear(dest, destOffset, count * size); return 0; }
            if (from > lba) Array.Clear(dest, destOffset, (from - lba) * size);
            if (to < lba + count) Array.Clear(dest, destOffset + (to - lba) * size, (lba + count - to) * size);
            return read(from, to - from, dest, destOffset + (from - lba) * size);
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <param name="offset">
        /// Drive read offset in samples. The drive delivers sample k of the track at position k + offset, so we read
        /// the whole sectors around that window (one extra when it isn't sector-aligned) and cut the track out of it.
        /// </param>
        internal static byte[] ReadTrack(SectorReader read, TrackInfo t, int offset, (int min, int max) readable, bool verify, TrackResult r,
            Action<double, string> progress, Func<bool> cancelled)
        {
            int firstLba = t.StartLba + FloorDiv(offset, 588);
            int skip = offset - FloorDiv(offset, 588) * 588;    // samples to drop from the first sector
            int sectors = t.Sectors + (skip > 0 ? 1 : 0);
            var data = new byte[(long)sectors * CdDrive.RawSectorSize];
            const int chunk = CdDrive.MaxRawSectorsPerRead;
            var started = DateTime.Now;
            double share = verify ? 0.5 : 1.0;

            for (int done = 0; done < sectors; done += chunk)
            {
                if (cancelled()) return null;
                int n = Math.Min(chunk, sectors - done);
                r.ReadErrors += ReadRange(read, firstLba + done, n, data, done * CdDrive.RawSectorSize, readable);
                if (done % (chunk * 20) == 0) progress(share * done / sectors, $"Reading · {Speed(done, started)}");
            }
            if (verify && !Verify(read, firstLba, sectors, readable, data, r, progress, cancelled)) return null;

            if (skip > 0)
            {
                int bytes = t.Sectors * CdDrive.RawSectorSize;
                Array.Copy(data, skip * 4, data, 0, bytes);
                Array.Resize(ref data, bytes);
            }
            return data;
        }

        static bool Verify(SectorReader read, int firstLba, int sectors, (int min, int max) readable, byte[] data, TrackResult r,
            Action<double, string> progress, Func<bool> cancelled)
        {
            const int chunk = CdDrive.MaxRawSectorsPerRead;

            // Second pass. A full track is far bigger than any drive cache, so this really re-reads the disc.
            var check = new byte[chunk * CdDrive.RawSectorSize];
            var retry = new byte[chunk * CdDrive.RawSectorSize];
            var started = DateTime.Now;
            for (int done = 0; done < sectors; done += chunk)
            {
                if (cancelled()) return false;
                int n = Math.Min(chunk, sectors - done);
                int bytes = n * CdDrive.RawSectorSize;
                long offset = (long)done * CdDrive.RawSectorSize;
                int lba = firstLba + done;
                ReadRange(read, lba, n, check, 0, readable);
                if (!Same(data, offset, check, bytes))
                {
                    bool agreed = false;
                    for (int attempt = 0; attempt < 8 && !agreed; attempt++)
                    {
                        // Seek away first so the drive can't answer from its cache.
                        int far = lba > 20000 ? lba - 15000 : lba + 15000;
                        read(Math.Max(readable.min, Math.Min(readable.max - 1, far)), 1, retry, 0);
                        ReadRange(read, lba, n, retry, 0, readable);
                        if (Same(retry, 0, check, bytes) || Same(retry, 0, data, offset, bytes))
                        {
                            Array.Copy(retry, 0, data, offset, bytes);
                            agreed = true;
                        }
                        else Array.Copy(retry, check, bytes);
                    }
                    if (!agreed) r.Unverified += n;
                }
                if (done % (chunk * 20) == 0) progress(0.5 + 0.5 * done / sectors, $"Verifying · {Speed(done, started)}");
            }
            return true;
        }

        /// <summary>
        /// Finds the drive's read offset with a disc that's in AccurateRip (a "key disc"): reads a couple of tracks with
        /// a margin on both sides and looks for the shift at which their checksums match the database.
        /// </summary>
        public static OffsetMatch DetectOffset(DiscInfo d, Action<double, string> progress, Func<bool> cancelled)
        {
            progress(0, "Looking up AccurateRip");
            var ar = AccurateRip.Fetch(d);
            if (ar.Count == 0) throw new DiscException("This disc isn't in the AccurateRip database. Try a popular, commercially pressed CD.");
            var audio = d.AudioTracks.ToList();
            const int margin = 10;                  // sectors on each side: offsets up to ±5880 samples
            const int range = margin * 588;
            // Middle tracks with many submissions; the shorter the quicker.
            var picks = Enumerable.Range(0, audio.Count)
                .Where(i => audio[i].Sectors > 2 * margin + 10)
                .OrderBy(i => i == 0 || i == audio.Count - 1 ? 1 : 0)
                .ThenByDescending(i => ar.Max(p => p.Confidence[i]))
                .ThenBy(i => audio[i].Sectors)
                .Take(2).ToList();
            if (picks.Count == 0) throw new DiscException("The tracks on this disc are too short to find the offset with.");

            var readable = Readable(d);
            var matches = new Dictionary<int, (int tracks, int confidence)>();
            long total = picks.Sum(i => (long)audio[i].Sectors + 2 * margin), before = 0;
            const int chunk = CdDrive.MaxRawSectorsPerRead;
            using var drive = new CdDrive(d.Drive);
            var read = Reader(drive);
            foreach (int i in picks)
            {
                var t = audio[i];
                int sectors = t.Sectors + 2 * margin;
                var pcm = new byte[(long)sectors * CdDrive.RawSectorSize];
                for (int done = 0; done < sectors; done += chunk)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    int n = Math.Min(chunk, sectors - done);
                    ReadRange(read, t.StartLba - margin + done, n, pcm, done * CdDrive.RawSectorSize, readable);
                    if (done % (chunk * 20) == 0) progress((before + done) / (double)total, $"Reading track {t.Number}");
                }
                before += sectors;

                var known = new Dictionary<uint, int>();
                foreach (var p in ar)
                    if (p.Crc[i] != 0) known[p.Crc[i]] = Math.Max(p.Confidence[i], known.TryGetValue(p.Crc[i], out var c) ? c : 0);
                var sums = AccurateRip.V1AtOffsets(pcm, t.Sectors * 588, range, i == 0, i == audio.Count - 1);
                for (int o = -range; o <= range; o++)
                    if (known.TryGetValue(sums[o + range], out int conf))
                    {
                        matches.TryGetValue(o, out var m);
                        matches[o] = (m.tracks + 1, m.confidence + conf);
                    }
            }
            progress(1, "Done");
            if (matches.Count == 0)
                throw new DiscException($"No read offset within ±{range} samples matched AccurateRip. This may be a pressing the database doesn't know; try another disc.");
            var best = matches.OrderByDescending(kv => kv.Value.tracks).ThenByDescending(kv => kv.Value.confidence).First();
            return new OffsetMatch { Offset = best.Key, TracksMatched = best.Value.tracks, TracksChecked = picks.Count, Confidence = best.Value.confidence };
        }

        static bool Same(byte[] a, long aOffset, byte[] b, int count) => Same(b, 0, a, aOffset, count);

        static bool Same(byte[] a, long aOffset, byte[] b, long bOffset, int count)
        {
            for (int i = 0; i < count; i++)
                if (a[aOffset + i] != b[bOffset + i]) return false;
            return true;
        }

        static string Speed(int sectors, DateTime since)
        {
            double secs = (DateTime.Now - since).TotalSeconds;
            return secs < 0.5 ? "starting" : $"{sectors / 75.0 / secs:0.0}×";
        }

        static void Encode(DiscInfo d, TrackInfo t, RipOptions o, byte[] data, string path, string coverPath)
        {
            const int block = 2352 * 64;
            switch (o.Format.Extension)
            {
                case ".flac":
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                    using (var enc = new FlacEncoder(fs, 44100, 2, 16, Tags(d, t), o.Cover))
                        for (int p = 0; p < data.Length; p += block) enc.Write16(data, p, Math.Min(block, data.Length - p));
                    break;
                case ".wav" when !o.Format.NeedsFfmpeg:
                    using (var w = new WavWriter(path))
                        w.Write(data, 0, data.Length);
                    break;
                default:
                    using (var enc = new FfmpegEncoder(path, o.Format.FfmpegArgs, FfTags(d, t), coverPath))
                    {
                        for (int p = 0; p < data.Length; p += block) enc.Write(data, p, Math.Min(block, data.Length - p));
                        enc.Finish();
                    }
                    break;
            }
        }

        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";

        static void WriteCue(DiscInfo d, List<TrackResult> results, string folder)
        {
            var sb = new StringBuilder();
            if (d.Genre.Length > 0) sb.AppendLine("REM GENRE " + Q(d.Genre));
            if (d.Year.Length > 0) sb.AppendLine("REM DATE " + d.Year);
            sb.AppendLine("REM COMMENT \"discphew\"");
            sb.AppendLine("PERFORMER " + Q(d.AlbumArtist));
            sb.AppendLine("TITLE " + Q(d.AlbumTitle));
            foreach (var r in results)
            {
                sb.AppendLine($"FILE {Q(Path.GetFileName(r.Path))} WAVE");
                sb.AppendLine($"  TRACK {r.Track.Number:00} AUDIO");
                sb.AppendLine("    TITLE " + Q(r.Track.Title));
                sb.AppendLine("    PERFORMER " + Q(r.Track.Artist.Length > 0 ? r.Track.Artist : d.AlbumArtist));
                if (r.Track.Isrc.Length > 0) sb.AppendLine("    ISRC " + r.Track.Isrc);
                if (r.Track.PreEmphasis) sb.AppendLine("    FLAGS PRE");
                sb.AppendLine("    INDEX 01 00:00:00");
            }
            File.WriteAllText(Path.Combine(folder, Discphew.UI.Ui.SafeName(d.AlbumTitle, "album") + ".cue"), sb.ToString(), new UTF8Encoding(true));
        }

        static string ArText(TrackResult r) => r.Ar switch
        {
            ArState.Match => $"accurate (confidence {r.ArConfidence}{(r.ArBest > r.ArConfidence ? $" of {r.ArBest}" : "")})",
            ArState.Mismatch => $"NOT accurate (no matching entry; best confidence {r.ArBest})",
            _ => "",
        };

        static void WriteLog(DiscInfo d, List<TrackResult> results, RipOptions o, string folder, List<ArPressing> ar, string arError)
        {
            var sb = new StringBuilder();
            sb.AppendLine("discphew rip log");
            sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine($"Album       {d.AlbumArtist} - {d.AlbumTitle}");
            sb.AppendLine($"Drive       {o.Drive}: {d.Media?.RecorderName}");
            sb.AppendLine($"Disc ID     {MusicBrainz.DiscId(d)} (MusicBrainz)");
            sb.AppendLine($"            {AccurateRip.DiscId(d)} (AccurateRip)");
            sb.AppendLine($"Format      {o.Format.Name}");
            sb.AppendLine($"Verify      {(o.Verify ? "two passes, mismatches re-read" : "off")}");
            sb.AppendLine(o.ReadOffset.HasValue
                ? $"Offset      {DriveOffsets.Format(o.ReadOffset.Value)} samples (read offset corrected)"
                : "Offset      not corrected (this drive's read offset isn't set)");
            sb.AppendLine("AccurateRip " + (!o.AccurateRip ? "not checked" : arError != null ? "lookup failed: " + arError : ar == null ? "not checked"
                : ar.Count == 0 ? "disc not in the database" : $"disc found, {ar.Count} submission set{(ar.Count == 1 ? "" : "s")}"));
            sb.AppendLine();
            sb.AppendLine("Track  Start      Length     CRC32     AR v1     AR v2     Status");
            foreach (var r in results)
            {
                string status = !r.Ok ? r.Error : r.ReadErrors > 0 ? $"{r.ReadErrors} unreadable sectors (silenced)" : r.Unverified > 0 ? $"{r.Unverified} sectors read inconsistently" : "OK";
                string arText = r.Ok ? ArText(r) : "";
                if (arText.Length > 0) status += ", AccurateRip " + arText;
                string sums = r.Ok ? $"{r.Crc:X8}  {r.ArV1:X8}  {r.ArV2:X8}" : new string(' ', 28);
                sb.AppendLine($"{r.Track.Number,5}  {Fmt.Msf(r.Track.StartLba),-9}  {Fmt.Time(r.Track.Duration),-9}  {sums}  {status}");
            }
            sb.AppendLine();
            bool clean = results.All(r => r.Ok && r.ReadErrors == 0 && r.Unverified == 0);
            sb.AppendLine(clean ? "All tracks read consistently." : "Some tracks had problems; see above.");
            if (ar != null && ar.Count > 0)
            {
                int accurate = results.Count(r => r.Ar == ArState.Match);
                sb.AppendLine(accurate == results.Count ? "All tracks accurately ripped (AccurateRip)."
                    : $"{accurate} of {results.Count} tracks accurately ripped (AccurateRip)."
                      + (accurate == 0 && !o.ReadOffset.HasValue ? " Set the drive's read offset and rip again." : ""));
            }
            File.WriteAllText(Path.Combine(folder, "rip.log"), sb.ToString(), new UTF8Encoding(true));
        }
    }

    public static class DataRipper
    {
        /// <summary>Copies the data track sector by sector into an .iso. Returns the number of unreadable sectors.</summary>
        public static int SaveImage(DiscInfo d, string path, Action<double, string> progress, Func<bool> cancelled)
        {
            var track = d.Tracks.FirstOrDefault(t => !t.IsAudio) ?? throw new DiscException("This disc has no data track.");
            int start = track.StartLba;
            int count = track.Sectors;
            if (d.FileSystem != null && d.FileSystem.VolumeSectors > 0 && d.SessionCount == 1)
                count = Math.Min(count, d.FileSystem.VolumeSectors);
            const int chunk = 32;
            var buf = new byte[chunk * 2048];
            int bad = 0;
            var started = DateTime.Now;
            using var drive = new CdDrive(d.Drive);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            for (int done = 0; done < count; done += chunk)
            {
                if (cancelled()) throw new OperationCanceledException();
                int n = Math.Min(chunk, count - done);
                if (!drive.ReadData(start + done, n, buf))
                {
                    for (int i = 0; i < n; i++)
                    {
                        bool ok = false;
                        for (int a = 0; a < 3 && !ok; a++) ok = drive.ReadData(start + done + i, 1, buf, i * 2048);
                        if (!ok) { Array.Clear(buf, i * 2048, 2048); bad++; }
                    }
                }
                fs.Write(buf, 0, n * 2048);
                if (done % (chunk * 16) == 0)
                {
                    double secs = Math.Max(0.1, (DateTime.Now - started).TotalSeconds);
                    progress(done / (double)count, $"{Fmt.Bytes((long)done * 2048)} of {Fmt.Bytes((long)count * 2048)} · {Fmt.Bytes((long)(done * 2048 / secs))}/s");
                }
            }
            progress(1, bad == 0 ? "Image saved" : $"Image saved with {bad} unreadable sectors");
            return bad;
        }

        static readonly string[] VideoExtensions = { ".vob", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".avi", ".m2v", ".dat", ".mov", ".mp4", ".mkv", ".wmv" };

        /// <summary>Copies every file from the disc. Optionally converts video files with ffmpeg.</summary>
        public static List<string> CopyFiles(char letter, string dest, string videoContainer, Action<double, string> progress, Func<bool> cancelled)
        {
            var errors = new List<string>();
            string root = letter + ":\\";
            var files = new List<FileInfo>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var sub in dir.GetDirectories()) stack.Push(sub);
                    files.AddRange(dir.GetFiles());
                }
                catch (Exception e) { errors.Add(dir.FullName + ": " + e.Message); }
            }

            bool convert = !string.IsNullOrEmpty(videoContainer) && Ffmpeg.Available;
            var toConvert = convert ? files.Where(f => VideoExtensions.Contains(f.Extension.ToLowerInvariant())).ToList() : new List<FileInfo>();
            // DVD-Video: join each title set's VOBs (VTS_01_1.VOB, VTS_01_2.VOB, ...) into one movie; skip menus (_0).
            var dvdTitles = toConvert.Where(f => f.Name.StartsWith("VTS_", StringComparison.OrdinalIgnoreCase) && f.Extension.Equals(".vob", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Name.Substring(0, 6).ToUpperInvariant()).ToList();
            var dvdParts = new HashSet<string>(dvdTitles.SelectMany(g => g).Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
            var singles = toConvert.Where(f => !dvdParts.Contains(f.FullName)).ToList();

            long total = files.Where(f => !convert || !toConvert.Contains(f)).Sum(f => f.Length) + toConvert.Sum(f => f.Length);
            long done = 0;
            var buf = new byte[1 << 20];
            var started = DateTime.Now;
            void Report(string name)
            {
                double secs = Math.Max(0.1, (DateTime.Now - started).TotalSeconds);
                progress(total > 0 ? done / (double)total : 1, $"{name} · {Fmt.Bytes(done)} of {Fmt.Bytes(total)} · {Fmt.Bytes((long)(done / secs))}/s");
            }

            foreach (var f in files)
            {
                if (cancelled()) throw new OperationCanceledException();
                if (convert && toConvert.Contains(f)) continue;
                string target = Path.Combine(dest, f.FullName.Substring(root.Length));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        dst.Write(buf, 0, n);
                        done += n;
                        Report(f.Name);
                    }
                    dst.Close();
                    File.SetLastWriteTime(target, f.LastWriteTime);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { errors.Add(f.FullName + ": " + e.Message); done += f.Length; }
            }

            string ext = "." + videoContainer.ToLowerInvariant();
            string codec = ext == ".mkv" ? "-c copy" : "-c:v libx264 -crf 18 -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k" + (ext == ".mp4" ? " -movflags +faststart" : "");
            var jobs = new List<(string input, string output, long bytes, string name)>();
            foreach (var g in dvdTitles)
            {
                var parts = g.Where(f => !f.Name.EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Name).ToList();
                if (parts.Count == 0) { done += g.Sum(f => f.Length); continue; }
                done += g.Where(f => !parts.Contains(f)).Sum(f => f.Length);
                jobs.Add(("concat:" + string.Join("|", parts.Select(p => p.FullName)), Path.Combine(dest, "Title " + g.Key.Substring(4, 2) + ext), parts.Sum(p => p.Length), "Title " + g.Key.Substring(4, 2)));
            }
            foreach (var f in singles)
                jobs.Add((f.FullName, Path.Combine(dest, Path.ChangeExtension(f.FullName.Substring(root.Length), ext)), f.Length, f.Name));

            foreach (var job in jobs)
            {
                if (cancelled()) throw new OperationCanceledException();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.output));
                    Report("Converting " + job.name);
                    using var p = Ffmpeg.Start($"-hide_banner -v error -y -fflags +genpts -i {Ffmpeg.Quote(job.input)} -map 0:v? -map 0:a? {codec} {Ffmpeg.Quote(job.output)}", false, false);
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    while (!p.WaitForExit(500))
                        if (cancelled()) { try { p.Kill(); } catch { } throw new OperationCanceledException(); }
                    if (p.ExitCode != 0) errors.Add(job.name + ": ffmpeg failed " + err.ToString().Trim());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { errors.Add(job.name + ": " + e.Message); }
                done += job.bytes;
                Report(job.name);
            }
            progress(1, errors.Count == 0 ? "All files copied" : $"Copied with {errors.Count} problems");
            return errors;
        }
    }
}
