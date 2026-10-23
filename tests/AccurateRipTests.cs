using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Discphew.Disc;

namespace Discphew.Tests
{
    /// <summary>
    /// Checks the AccurateRip and offset-correction code against published test vectors.
    /// Run with tests\run-tests.ps1, which also fetches the sample files the checksum vectors refer to.
    /// </summary>
    static partial class TestProgram
    {
        static int failed, passed;
        static string dataDir = "";

        static int Main(string[] args)
        {
            dataDir = args.Length > 0 ? args[0] : "";
            Run("AccurateRip disc IDs", DiscIds);
            Run("dBAR response parsing", Parsing);
            Run("AccurateRip v1/v2 checksums (arver sample.wav)", ChecksumVectors);
            Run("v1 at every offset equals direct checksums", SlidingChecksums);
            Run("read offset correction", OffsetCorrection);
            Run("CD-Text packs", CdTextPacks);
            Run("CD-Text in a raw disc-at-once image (IMAPI)", CdTextRawImage);
            Run("cue sheet parsing", CueParsing);
            Run("cue tracks split out of one file", CueSplitting);
            Run("cover lookups: disc IDs for files, Cover Art Archive listings", CoverLookups);
            Run("booklet imposition", BookletImposition);
            Run("cover templates and PDF", CoverPdf);
            Console.WriteLine();
            Console.WriteLine(failed == 0 ? $"All {passed} checks passed." : $"{failed} of {passed + failed} checks FAILED.");
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            Console.WriteLine("· " + name);
            try { test(); }
            catch (Exception e) { Fail("threw " + e); }
        }

        static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else Fail(what);
        }

        static void Fail(string what)
        {
            failed++;
            Console.WriteLine("    FAIL " + what);
        }

        static void Equal<T>(T expected, T actual, string what) =>
            Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected {expected}, got {actual}");

        // ------------------------------------------------------------------ disc IDs

        /// <summary>A TOC the way arver's DiscInfo.from_track_lengths builds it (Enhanced CD when there's a data track).</summary>
        static DiscInfo Toc(int[] lengths, int pregap = 0, int data = 0)
        {
            var d = new DiscInfo { FirstTrack = 1 };
            int lba = pregap;
            for (int i = 0; i < lengths.Length; i++)
            {
                d.Tracks.Add(new TrackInfo { Number = i + 1, StartLba = lba, EndLba = lba + lengths[i], IsAudio = true });
                lba += lengths[i];
            }
            d.LeadOutLba = lba;
            if (data > 0)
            {
                int start = lba + 11400;    // lead-out, lead-in and pregap of the second session
                d.Tracks.Add(new TrackInfo { Number = lengths.Length + 1, StartLba = start, EndLba = start + data, Session = 2 });
                d.LeadOutLba = start + data;
            }
            d.LastTrack = d.Tracks.Count;
            return d;
        }

        static void DiscIds()
        {
            // whipper test_image_table.py, Ladyhawke (12 audio tracks + data track); checked against EAC's request.
            var ladyhawke = new DiscInfo { FirstTrack = 1, LastTrack = 13, LeadOutLba = 210385 };
            int[] starts = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
            for (int i = 0; i < starts.Length; i++)
                ladyhawke.Tracks.Add(new TrackInfo { Number = i + 1, StartLba = starts[i], EndLba = i + 1 < starts.Length ? starts[i + 1] : 210385, IsAudio = i < 12 });
            Equal("a/5/d/dBAR-012-0013bd5a-00b8d489-c60af50d.bin", AccurateRip.DatabasePath(ladyhawke), "Ladyhawke path");

            // arver tests/discinfo_test.py: audio, pregap, Enhanced CD and hidden-track discs.
            var cases = new (int[] tracks, int pregap, int data, string id)[]
            {
                (new[] { 75258, 54815, 205880 }, 0, 0, "003-00084264-001cc184-19117f03"),
                (new[] { 279037 }, 0, 0, "001-000441fd-000883fb-020e8801"),
                (new[] { 107450, 71470, 105737, 71600 }, 33, 0, "004-000e26d9-00380804-3e128e04"),
                (new[] { 143963 }, 32, 0, "001-0002329b-00046516-02077f01"),
                (new[] { 12617, 27720, 22738, 30185, 24705, 33750, 32475, 30920, 32195, 22880 }, 0, 52066, "010-00164419-00b9f6e2-9e11600b"),
                (new[] { 90778 }, 0, 164721, "001-00041293-00082527-100de602"),
                (new[] { 14765, 14932, 12508, 525, 20937, 6025, 19753, 35570, 17777, 15258, 23515, 18512, 26168, 13440 }, 12375, 0, "014-001ba337-01281b14-cf0c7b0e"),
            };
            foreach (var c in cases) Equal(c.id, AccurateRip.DiscId(Toc(c.tracks, c.pregap, c.data)), "disc ID");
        }

        // ------------------------------------------------------------------ responses

        static void Parsing()
        {
            // Synthetic: two submission sets for a 2-track disc.
            var ms = new MemoryStream();
            void U32(uint v) => ms.Write(BitConverter.GetBytes(v), 0, 4);
            foreach (var (conf, crc) in new[] { (12, 0x284fc705u), (7, 0xdc77f9abu) })
            {
                ms.WriteByte(2); U32(0xf21c); U32(0x27ef8); U32(0x05021002);
                ms.WriteByte((byte)conf); U32(crc); U32(0x11111111);
                ms.WriteByte((byte)(conf + 1)); U32(crc + 1); U32(0x22222222);
            }
            var list = AccurateRip.Parse(ms.ToArray());
            Equal(2, list.Count, "sets");
            Equal(0x05021002u, list[0].CddbId, "cddb id");
            Equal(0xdc77f9abu, list[1].Crc[0], "crc");
            Equal(8, list[1].Confidence[1], "confidence");
            Equal((12, 12), AccurateRip.Match(list, 0, 0x284fc705, 0), "v1 match");
            Equal((7, 12), AccurateRip.Match(list, 0, 0, 0xdc77f9ab), "v2 match");
            Equal((0, 13), AccurateRip.Match(list, 1, 1, 2), "no match");
            bool threw = false;
            try { AccurateRip.Parse(ms.ToArray().Take(20).ToArray()); } catch (InvalidDataException) { threw = true; }
            Check(threw, "truncated response is rejected");

            // Real responses: whipper test_common_accurip.py and arver tests/data/responses.
            var whipper = Path.Combine(dataDir, "dBAR-002-0000f21c-00027ef8-05021002.bin");
            if (File.Exists(whipper))
            {
                var r = AccurateRip.Parse(File.ReadAllBytes(whipper));
                Equal(2, r.Count, "whipper sets");
                Equal(0x0000f21cu, r[0].Id1, "whipper id1");
                Equal(0x00027ef8u, r[0].Id2, "whipper id2");
                Equal("12,20", string.Join(",", r[0].Confidence), "whipper confidences 0");
                Equal("284fc705,9cc1f32e", string.Join(",", r[0].Crc.Select(x => x.ToString("x8"))), "whipper crcs 0");
                Equal("7,7", string.Join(",", r[1].Confidence), "whipper confidences 1");
                Equal("dc77f9ab,dd97d2c3", string.Join(",", r[1].Crc.Select(x => x.ToString("x8"))), "whipper crcs 1");
            }
            else Console.WriteLine("    skipped the whipper response (file not found)");

            var arver = Path.Combine(dataDir, "dBAR-013-00206791-01486a82-a710de0d.bin");
            if (File.Exists(arver))
            {
                var r = AccurateRip.Parse(File.ReadAllBytes(arver));
                Equal(1, r.Count, "arver sets");
                Equal(13, r[0].Crc.Length, "arver tracks");
                Equal(3840044706u, r[0].Crc[0], "arver track 1");
                Equal(2252988638u, r[0].Crc[12], "arver track 13");
                Check(r[0].Confidence.All(c => c == 2), "arver confidences");
            }
            else Console.WriteLine("    skipped the arver response (file not found)");
        }

        // ------------------------------------------------------------------ checksums

        static byte[] WavData(string path)
        {
            var b = File.ReadAllBytes(path);
            for (int p = 12; p + 8 <= b.Length;)
            {
                int size = BitConverter.ToInt32(b, p + 4);
                if (Encoding.ASCII.GetString(b, p, 4) == "data") return b.Skip(p + 8).Take(size).ToArray();
                p += 8 + size + (size & 1);
            }
            throw new InvalidDataException("no data chunk in " + path);
        }

        static void ChecksumVectors()
        {
            // arver tests/checksums_test.py: the same 1-second sample as the only, first, a middle and the last track.
            var path = Path.Combine(dataDir, "sample.wav");
            if (!File.Exists(path)) { Fail("sample.wav not found in " + dataDir); return; }
            var pcm = WavData(path);
            var vectors = new (bool first, bool last, uint v1, uint v2, string name)[]
            {
                (true, true, 0xf43f9174, 0x0ae7c6f9, "only track"),
                (true, false, 0x9d6c90ec, 0xb775893e, "first track"),
                (false, false, 0x3c8dd1d2, 0x56bba272, "middle track"),
                (false, true, 0x9360d25a, 0xaa2de02d, "last track"),
            };
            foreach (var v in vectors)
            {
                AccurateRip.Checksums(pcm, pcm.Length, v.first, v.last, out uint v1, out uint v2);
                Equal(v.v1.ToString("x8"), v1.ToString("x8"), v.name + " v1");
                Equal(v.v2.ToString("x8"), v2.ToString("x8"), v.name + " v2");
            }
            var silence = new byte[pcm.Length];
            AccurateRip.Checksums(silence, silence.Length, true, false, out uint s1, out uint s2);
            Check(s1 == 0 && s2 == 0, "silence sums to zero");
        }

        static void SlidingChecksums()
        {
            const int samples = 40 * 588, range = 3 * 588;
            var pcm = new byte[(samples + 2 * range) * 4];
            new Random(7).NextBytes(pcm);
            var slice = new byte[samples * 4];
            foreach (var (first, last) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var all = AccurateRip.V1AtOffsets(pcm, samples, range, first, last);
                int wrong = 0;
                for (int o = -range; o <= range; o++)
                {
                    Buffer.BlockCopy(pcm, (range + o) * 4, slice, 0, slice.Length);
                    AccurateRip.Checksums(slice, slice.Length, first, last, out uint v1, out _);
                    if (all[o + range] != v1) wrong++;
                }
                Equal(0, wrong, $"offsets with a wrong v1 (first={first}, last={last})");
            }
        }

        // ------------------------------------------------------------------ offset correction

        static uint SampleAt(long position) => (uint)(position * 2654435761L + 0x9E3779B9L) | 1;

        static void OffsetCorrection()
        {
            // A fake disc whose every sample encodes its own position; audio is readable from LBA 0 to 3000.
            var readable = (min: 0, max: 3000);
            int outOfRange = 0;
            AudioRipper.SectorReader read = (lba, count, dest, destOffset) =>
            {
                if (lba < readable.min || lba + count > readable.max) outOfRange++;
                for (int i = 0; i < count * 588; i++)
                    BitConverter.GetBytes(SampleAt((long)lba * 588 + i)).CopyTo(dest, destOffset + i * 4);
                return 0;
            };

            var tracks = new[]
            {
                new TrackInfo { Number = 1, StartLba = 0, EndLba = 1000, IsAudio = true },        // starts at the lead-in
                new TrackInfo { Number = 2, StartLba = 1000, EndLba = 2000, IsAudio = true },
                new TrackInfo { Number = 3, StartLba = 2000, EndLba = 3000, IsAudio = true },     // runs into the lead-out
            };
            foreach (var t in tracks)
                foreach (int offset in new[] { 0, 6, -6, 48, 102, 587, 588, 589, 667, 1776, -472, -588, -1164, -3000 })
                {
                    var r = new TrackResult { Track = t };
                    var data = AudioRipper.ReadTrack(read, t, offset, readable, true, r, (f, s) => { }, () => false);
                    if (data == null || data.Length != t.Sectors * 2352) { Fail($"track {t.Number} offset {offset}: wrong length"); continue; }
                    int wrong = 0;
                    for (int k = 0; k < t.Sectors * 588; k++)
                    {
                        long position = (long)t.StartLba * 588 + k + offset;
                        uint expected = position >= readable.min * 588L && position < readable.max * 588L ? SampleAt(position) : 0;
                        if (BitConverter.ToUInt32(data, k * 4) != expected) wrong++;
                    }
                    Equal(0, wrong, $"track {t.Number} offset {offset}: wrong samples");
                    Equal(0, r.Unverified + r.ReadErrors, $"track {t.Number} offset {offset}: read problems");
                }
            Equal(0, outOfRange, "reads outside the audio area");
        }
    }
}
