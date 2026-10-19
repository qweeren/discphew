using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace Discphew.Disc
{
    /// <summary>One submission set from the AccurateRip database: a checksum and its confidence for every track.</summary>
    public sealed class ArPressing
    {
        public uint Id1, Id2, CddbId;
        public int[] Confidence;
        public uint[] Crc;
        public uint[] Crc450;
    }

    public enum ArState { NotChecked, LookupFailed, NotInDatabase, Mismatch, Match }

    /// <summary>
    /// AccurateRip: disc IDs from the TOC, database lookup, and the v1/v2 track checksums
    /// (the same values EAC, dBpoweramp, CUETools and whipper compute).
    /// </summary>
    public static class AccurateRip
    {
        /// <summary>The first and last five sectors of the disc aren't checksummed (drives can't agree on them).</summary>
        public const int SkipSamples = 5 * 588;
        const string UserAgent = "discphew/0.1 (open-source CD/DVD utility)";

        public static (uint id1, uint id2, uint cddb) DiscIds(DiscInfo d)
        {
            uint id1 = 0, id2 = 0, n = 0;
            foreach (var t in d.AudioTracks)
            {
                id1 += (uint)t.StartLba;
                id2 += (uint)Math.Max(t.StartLba, 1) * ++n;
            }
            id1 += (uint)d.LeadOutLba;
            id2 += (uint)Math.Max(d.LeadOutLba, 1) * ++n;
            return (id1, id2, CddbId(d));
        }

        /// <summary>The freedb/CDDB disc ID (counts data tracks too).</summary>
        public static uint CddbId(DiscInfo d)
        {
            int sum = 0;
            foreach (var t in d.Tracks)
                for (int s = (t.StartLba + 150) / 75; s > 0; s /= 10) sum += s % 10;
            int seconds = (d.LeadOutLba + 150) / 75 - (d.Tracks[0].StartLba + 150) / 75;
            return (uint)(sum % 0xFF) << 24 | (uint)seconds << 8 | (uint)d.Tracks.Count;
        }

        /// <summary>"003-00084264-001cc184-19117f03": audio track count and the three disc IDs.</summary>
        public static string DiscId(DiscInfo d)
        {
            var (id1, id2, cddb) = DiscIds(d);
            return $"{d.AudioTracks.Count():000}-{id1:x8}-{id2:x8}-{cddb:x8}";
        }

        public static string DatabasePath(DiscInfo d)
        {
            uint id1 = DiscIds(d).id1;
            return $"{id1 & 0xF:x}/{id1 >> 4 & 0xF:x}/{id1 >> 8 & 0xF:x}/dBAR-{DiscId(d)}.bin";
        }

        /// <summary>Splits a dBAR response into its submission sets.</summary>
        public static List<ArPressing> Parse(byte[] b)
        {
            var list = new List<ArPressing>();
            int p = 0;
            while (p < b.Length)
            {
                int n = b[p];
                if (n == 0 || p + 13 + n * 9 > b.Length) throw new InvalidDataException("The AccurateRip response is malformed.");
                var r = new ArPressing
                {
                    Id1 = BitConverter.ToUInt32(b, p + 1),
                    Id2 = BitConverter.ToUInt32(b, p + 5),
                    CddbId = BitConverter.ToUInt32(b, p + 9),
                    Confidence = new int[n], Crc = new uint[n], Crc450 = new uint[n],
                };
                p += 13;
                for (int i = 0; i < n; i++, p += 9)
                {
                    r.Confidence[i] = b[p];
                    r.Crc[i] = BitConverter.ToUInt32(b, p + 1);
                    r.Crc450[i] = BitConverter.ToUInt32(b, p + 5);
                }
                list.Add(r);
            }
            return list;
        }

        /// <summary>Downloads the disc's entries. Returns an empty list when the disc isn't in the database.</summary>
        public static List<ArPressing> Fetch(DiscInfo d)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://www.accuraterip.com/accuraterip/" + DatabasePath(d));
            req.UserAgent = UserAgent;
            req.Timeout = req.ReadWriteTimeout = 15000;
            try
            {
                using var resp = req.GetResponse();
                using var s = resp.GetResponseStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                int count = d.AudioTracks.Count();
                return Parse(ms.ToArray()).Where(p => p.Crc.Length == count).ToList();
            }
            catch (WebException e) when ((e.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                return new List<ArPressing>();
            }
        }

        /// <summary>
        /// v1 and v2 checksums of one track (16-bit stereo PCM, little-endian). Every sample counts, weighted by its
        /// 1-based position; the disc's first track skips its first five sectors minus one sample, the last its last five.
        /// </summary>
        public static void Checksums(byte[] pcm, int length, bool firstTrack, bool lastTrack, out uint v1, out uint v2)
        {
            int samples = length / 4;
            int start = firstTrack ? SkipSamples - 1 : 0;
            int end = lastTrack ? samples - SkipSamples : samples;
            uint lo = 0, hi = 0;
            for (int i = start; i < end; i++)
            {
                ulong product = (ulong)BitConverter.ToUInt32(pcm, i * 4) * (uint)(i + 1);
                lo += (uint)product;
                hi += (uint)(product >> 32);
            }
            v1 = lo;
            v2 = lo + hi;
        }

        /// <summary>
        /// v1 checksums of a track at every read offset from -range to +range samples, in one pass.
        /// <paramref name="pcm"/> holds the track with <paramref name="range"/> extra samples on each side;
        /// element o + range of the result is the checksum of the track read with offset o.
        /// </summary>
        public static uint[] V1AtOffsets(byte[] pcm, int trackSamples, int range, bool firstTrack, bool lastTrack)
        {
            // S(o) = Σ k·x(k+o) and T(o) = Σ x(k+o) over the checked positions a..b; sliding the window by one sample
            // gives S(o+1) = S(o) − T(o) − (a−1)·x(a+o) + b·x(b+1+o), all modulo 2³².
            int a = firstTrack ? SkipSamples : 1;
            int b = lastTrack ? trackSamples - SkipSamples : trackSamples;
            uint X(int k, int o) => BitConverter.ToUInt32(pcm, (range + k - 1 + o) * 4);
            uint s = 0, t = 0;
            for (int k = a; k <= b; k++)
            {
                uint x = X(k, -range);
                s += (uint)k * x;
                t += x;
            }
            var result = new uint[2 * range + 1];
            for (int o = -range; ; o++)
            {
                result[o + range] = s;
                if (o == range) break;
                uint first = X(a, o), next = X(b + 1, o);
                s = s - t - (uint)(a - 1) * first + (uint)b * next;
                t = t - first + next;
            }
            return result;
        }

        /// <summary>
        /// Confidence of the database entry matching either checksum (0 if none does), and the highest confidence of
        /// any entry for that track.
        /// </summary>
        public static (int confidence, int best) Match(List<ArPressing> pressings, int index, uint v1, uint v2)
        {
            int confidence = 0, best = 0;
            foreach (var p in pressings)
            {
                if (index >= p.Crc.Length) continue;
                best = Math.Max(best, p.Confidence[index]);
                if (p.Crc[index] == v1 || p.Crc[index] == v2) confidence = Math.Max(confidence, p.Confidence[index]);
            }
            return (confidence, best);
        }
    }

    /// <summary>Per-drive read offsets in samples, remembered by the drive's vendor and product name.</summary>
    public static class DriveOffsets
    {
        /// <summary>Null while the drive's name isn't known yet.</summary>
        public static string Key(DiscInfo d, bool mediaLoading)
        {
            if (d == null) return null;
            if (d.Media != null && d.Media.RecorderName.Length > 0) return "offset." + d.Media.RecorderName;
            return mediaLoading ? null : $"offset.{d.Drive}:";
        }

        public static int? Get(string key) => key != null && int.TryParse(Settings.Get(key), out var v) ? v : (int?)null;

        public static void Set(string key, int? offset)
        {
            if (key != null) Settings.Set(key, offset.HasValue ? Format(offset.Value) : "");
        }

        public static string Format(int offset) => offset.ToString("+0;-0;0");
    }
}
