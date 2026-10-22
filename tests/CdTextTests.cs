using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Discphew.Disc;

namespace Discphew.Tests
{
    /// <summary>CD-Text pack building, and the R-W subcode IMAPI's raw image creator puts in the lead-in.</summary>
    static partial class TestProgram
    {
        static void CdTextPacks()
        {
            var check = Encoding.ASCII.GetBytes("123456789");
            Equal(0x31C3, CdText.Crc16(check, 0, check.Length), "CRC-16/CCITT check value");

            var packs = CdText.Build("Some Album", "The Band", new[] { "First Song", "A Much Longer Second Title" }, new[] { "The Band", "" });
            Check(packs.Length % 18 == 0, "packs are 18 bytes each");
            int n = packs.Length / 18;
            for (int i = 0; i < n; i++)
            {
                int crc = ~CdText.Crc16(packs, i * 18, 16) & 0xFFFF;
                Check(packs[i * 18 + 16] == crc >> 8 && packs[i * 18 + 17] == (crc & 0xFF), $"pack {i} CRC");
                Equal(i, (int)packs[i * 18 + 2], $"pack {i} sequence number");
            }
            var size = Enumerable.Range(n - 3, 3).SelectMany(i => packs.Skip(i * 18 + 4).Take(12)).ToArray();
            Check(packs[(n - 3) * 18] == 0x8F && packs[(n - 1) * 18] == 0x8F, "block ends with three size packs");
            Equal(2, (int)size[2], "size info: last track");
            Equal(n - 1, (int)size[20], "size info: last sequence number");
            Equal(n, Enumerable.Range(4, 16).Sum(i => size[i]), "size info: pack counts add up");

            var text = CdText.Parse(packs, 0, n);
            Equal("Some Album", Get(text, 0x80, 0), "album title");
            Equal("First Song", Get(text, 0x80, 1), "track 1 title");
            Equal("A Much Longer Second Title", Get(text, 0x80, 2), "track 2 title");
            Equal("The Band", Get(text, 0x81, 0), "album performer");
            Equal("The Band", Get(text, 0x81, 1), "track 1 performer");

            // The second title's pack starts 2 characters into it ("Some Album\0First Song\0" is 22 bytes).
            int second = Enumerable.Range(0, n).First(i => packs[i * 18 + 1] == 2);
            Equal(1, (int)packs[(second - 1) * 18 + 1], "pack 1 belongs to track 1");
            Equal(2, packs[second * 18 + 3] & 0x0F, "character position");

            var fold = CdText.Build("Café — “Live”…", "", new[] { "Łódź" }, new[] { "" });
            var folded = CdText.Parse(fold, 0, fold.Length / 18);
            Equal("Café - \"Live\"...", Get(folded, 0x80, 0), "punctuation folded to Latin-1");
            Equal("?ódz", Get(folded, 0x80, 1), "letters folded to Latin-1");

            var many = Enumerable.Range(1, 99).Select(i => new string('x', 150) + i).ToList();
            var big = CdText.Build(new string('a', 200), new string('b', 200), many, many);
            Check(big.Length / 18 <= 256, $"long texts are shortened to fit one block ({big.Length / 18} packs)");
            Check(CdText.Parse(big, 0, big.Length / 18).Count(kv => kv.Key.Item1 == 0x80) == 100, "every long title survives");

            var sub = CdText.ToSubcode(packs);
            Equal(n * 24, sub.Length, "24 subcode symbols per pack");
            Check(sub.All(b => b < 64), "symbols leave P and Q clear");
            Check(FromSubcode(sub, 0, sub.Length).SequenceEqual(packs), "symbols decode back to the packs");
        }

        static void CdTextRawImage()
        {
            if (Type.GetTypeFromProgID("IMAPI2.MsftRawCDImageCreator", false) == null) { Console.WriteLine("    skipped: no IMAPI2 raw image creator"); return; }
            string dir = Path.Combine(Path.GetTempPath(), "discphew-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var streams = new List<IStream>();
            object creator = null, image = null;
            IStream subcode = null;
            try
            {
                var files = new[] { Path.Combine(dir, "01.pcm"), Path.Combine(dir, "02.pcm") };
                File.WriteAllBytes(files[0], Enumerable.Repeat((byte)0x11, 300 * 2352).ToArray());
                File.WriteAllBytes(files[1], Enumerable.Repeat((byte)0x22, 320 * 2352).ToArray());
                var packs = CdText.Build("Raw Album", "Raw Artist", new[] { "Opening", "Closing" }, new[] { "", "" });

                subcode = Imapi.CdTextSubcode(files, packs, streams);
                creator = Imapi.CreateRawImage(Imapi.SubcodeRaw, files, subcode, streams, out image);
                var img = ReadAll((IStream)image);
                int sectors = img.Length / 2448;
                int Lba(int lba) => (lba - Imapi.RawImageStartLba) * 2448;

                Equal(0x11, (int)img[Lba(0)], "track 1 audio at LBA 0");
                Equal(0x11, (int)img[Lba(299) + 2351], "track 1 runs to LBA 299");
                Equal(0x22, (int)img[Lba(300)], "track 2 follows with no gap");

                // A drive's lead-in starts somewhere around LBA -11000; read a few sectors of R-W from there.
                var symbols = new List<byte>();
                for (int s = 0; s < 4; s++) for (int i = 0; i < 96; i++) symbols.Add((byte)(img[Lba(-11000 + s) + 2352 + i] & 0x3F));
                var raw = FromSubcode(symbols.ToArray(), 0, symbols.Count);
                var found = new Dictionary<int, byte[]>();
                for (int o = 0; o + 18 <= raw.Length; o += 18)
                {
                    int crc = ~CdText.Crc16(raw, o, 16) & 0xFFFF;
                    if (raw[o + 16] == crc >> 8 && raw[o + 17] == (crc & 0xFF)) found[raw[o + 2]] = raw.Skip(o).Take(18).ToArray();
                }
                Equal(packs.Length / 18, found.Count, "every pack is in the lead-in with a good CRC");
                var text = CdText.Parse(found.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).ToArray(), 0, found.Count);
                Equal("Raw Album", Get(text, 0x80, 0), "album title read back from the lead-in");
                Equal("Closing", Get(text, 0x80, 2), "track 2 title read back from the lead-in");
                Equal("Raw Artist", Get(text, 0x81, 0), "performer read back from the lead-in");

                int dirty = 0;
                for (int s = Lba(-150) / 2448; s < sectors; s++)
                    for (int i = 0; i < 96; i++) if ((img[s * 2448 + 2352 + i] & 0x3F) != 0) { dirty++; break; }
                Equal(0, dirty, "sectors from the pregap on with R-W subcode");
            }
            finally
            {
                Com.Release(image); Com.Release(creator); Com.Release(subcode);
                foreach (var s in streams) Com.Release(s);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static string Get(Dictionary<(int, int), string> text, int type, int track) =>
            text != null && text.TryGetValue((type, track), out var s) ? s : null;

        static byte[] FromSubcode(byte[] s, int offset, int count)
        {
            var b = new byte[count / 4 * 3];
            for (int i = 0, o = 0; i + 3 < count; i += 4, o += 3)
            {
                b[o] = (byte)((s[offset + i] << 2) | (s[offset + i + 1] >> 4));
                b[o + 1] = (byte)((s[offset + i + 1] << 4) | (s[offset + i + 2] >> 2));
                b[o + 2] = (byte)((s[offset + i + 2] << 6) | s[offset + i + 3]);
            }
            return b;
        }

        static byte[] ReadAll(IStream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[1 << 20];
            var got = Marshal.AllocHGlobal(4);
            try
            {
                while (true)
                {
                    s.Read(buf, buf.Length, got);
                    int n = Marshal.ReadInt32(got);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
            }
            finally { Marshal.FreeHGlobal(got); }
            return ms.ToArray();
        }
    }
}
