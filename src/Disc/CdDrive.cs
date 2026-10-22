using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Discphew.Native;
using Microsoft.Win32.SafeHandles;

namespace Discphew.Disc
{
    public sealed class DiscException : Exception
    {
        public DiscException(string message) : base(message) { }
        public DiscException(string message, Exception inner) : base(message, inner) { }

        /// <summary>The drive is still spinning up or settling after a disc change: worth trying again shortly.</summary>
        public bool NotReady { get; set; }
    }

    /// <summary>
    /// Direct access to an optical drive through the Windows CD-ROM class driver.
    /// Open it, do the work, dispose it: IMAPI (burning) needs exclusive access,
    /// so we never keep a handle around longer than an operation.
    /// </summary>
    public sealed class CdDrive : IDisposable
    {
        public const int RawSectorSize = 2352;
        public const int CookedSectorSize = 2048;
        /// <summary>26 × 2352 stays below the 64 KB transfer limit that many USB bridges enforce.</summary>
        public const int MaxRawSectorsPerRead = 26;
        const int MaxCookedSectorsPerRead = 32;

        readonly SafeFileHandle handle;
        IntPtr rawBuffer, cookedBuffer, ioBuffer;
        bool extendedDasd;

        public char Letter { get; }

        public static char[] Enumerate() =>
            DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => char.ToUpperInvariant(d.Name[0])).ToArray();

        /// <summary>The volume label Windows reports, or "". Blocks while the drive spins up, so keep it off the UI thread.</summary>
        public static string VolumeLabel(char letter)
        {
            try
            {
                var di = new DriveInfo(letter + ":\\");
                if (di.IsReady) return di.VolumeLabel ?? "";
            }
            catch { }
            return "";
        }

        public static bool IsNotReady(int win32Error) => win32Error == Win32.ERROR_NOT_READY || win32Error == Win32.ERROR_MEDIA_CHANGED;

        public CdDrive(char letter)
        {
            Letter = char.ToUpperInvariant(letter);
            handle = Win32.CreateFile($@"\\.\{Letter}:", Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new DiscException($"Couldn't open drive {Letter}: ({new Win32Exception().Message})");
            rawBuffer = Marshal.AllocHGlobal(MaxRawSectorsPerRead * RawSectorSize);
            cookedBuffer = Marshal.AllocHGlobal(MaxCookedSectorsPerRead * CookedSectorSize);
            ioBuffer = Marshal.AllocHGlobal(64 * 1024);
        }

        public void Dispose()
        {
            handle.Dispose();
            if (rawBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(rawBuffer); rawBuffer = IntPtr.Zero; }
            if (cookedBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(cookedBuffer); cookedBuffer = IntPtr.Zero; }
            if (ioBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(ioBuffer); ioBuffer = IntPtr.Zero; }
        }

        bool Ioctl(uint code, int inSize, int outSize, out int returned) =>
            Win32.DeviceIoControl(handle, code, inSize > 0 ? ioBuffer : IntPtr.Zero, inSize, outSize > 0 ? ioBuffer : IntPtr.Zero, outSize, out returned, IntPtr.Zero);

        /// <summary>
        /// The first check after a disc goes in reports "media changed" rather than success; that
        /// still means a disc is there, so ask again to clear it.
        /// </summary>
        public bool MediaPresent()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Ioctl(Win32.IOCTL_STORAGE_CHECK_VERIFY, 0, 0, out _)) return true;
                if (Marshal.GetLastWin32Error() != Win32.ERROR_MEDIA_CHANGED) return false;
            }
            return true;
        }

        public static void Eject(char letter)
        {
            using var d = new CdDrive(letter);
            if (!d.Ioctl(Win32.IOCTL_STORAGE_EJECT_MEDIA, 0, 0, out _))
                throw new DiscException("The drive refused to eject (is another program using it?)");
        }

        public static void CloseTray(char letter)
        {
            using var d = new CdDrive(letter);
            d.Ioctl(Win32.IOCTL_STORAGE_LOAD_MEDIA, 0, 0, out _);
        }

        static int MsfToLba(byte m, byte s, byte f) => (m * 60 + s) * 75 + f - 150;

        /// <summary>Reads the TOC and turns it into a DiscInfo with correct track boundaries.</summary>
        public DiscInfo ReadDisc()
        {
            if (!MediaPresent()) throw new DiscException("No disc in the drive.");
            const int tocSize = 4 + 100 * 8;
            if (!Ioctl(Win32.IOCTL_CDROM_READ_TOC, 0, tocSize, out _))
            {
                int error = Marshal.GetLastWin32Error();
                throw new DiscException("Couldn't read the disc's table of contents. " + new Win32Exception(error).Message) { NotReady = IsNotReady(error) };
            }

            var toc = new byte[tocSize];
            Marshal.Copy(ioBuffer, toc, 0, tocSize);
            var info = new DiscInfo { Drive = Letter, FirstTrack = toc[2], LastTrack = toc[3] };
            int count = info.LastTrack - info.FirstTrack + 1;
            if (count <= 0 || count > 99) throw new DiscException("The disc's table of contents looks empty.");

            var starts = new List<(int number, int lba, byte control)>();
            for (int i = 0; i <= count; i++)
            {
                int o = 4 + i * 8;
                byte control = (byte)(toc[o + 1] & 0x0F);
                int number = toc[o + 2];
                int lba = MsfToLba(toc[o + 5], toc[o + 6], toc[o + 7]);
                starts.Add((number, lba, control));
            }
            info.LeadOutLba = starts[count].lba;

            int lastSessionStart = -1;
            if (Ioctl(Win32.IOCTL_CDROM_GET_LAST_SESSION, 0, 12, out _))
            {
                var s = new byte[12];
                Marshal.Copy(ioBuffer, s, 0, 12);
                info.SessionCount = Math.Max(1, (int)s[3]);
                if (info.SessionCount > 1) lastSessionStart = MsfToLba(s[9], s[10], s[11]);
            }

            for (int i = 0; i < count; i++)
            {
                var (number, lba, control) = starts[i];
                var t = new TrackInfo
                {
                    Number = number,
                    StartLba = lba,
                    EndLba = starts[i + 1].lba,
                    IsAudio = (control & 0x04) == 0,
                    PreEmphasis = (control & 0x01) != 0 && (control & 0x04) == 0,
                    CopyPermitted = (control & 0x02) != 0,
                };
                if (lastSessionStart >= 0 && lba >= lastSessionStart) t.Session = info.SessionCount;
                info.Tracks.Add(t);
            }

            // Enhanced CD: the last audio track is followed by a session gap (lead-out 6750 + lead-in 4500
            // + pregap 150 = 11400 sectors) that the TOC folds into the audio track. Trim it.
            for (int i = 0; i < info.Tracks.Count - 1; i++)
            {
                var a = info.Tracks[i];
                var b = info.Tracks[i + 1];
                if (a.IsAudio && !b.IsAudio && b.Session != a.Session)
                    a.EndLba = Math.Max(a.StartLba, a.EndLba - 11400);
            }

            TryReadCdText(info);
            return info;
        }

        public int LastSessionDataStart(DiscInfo info)
        {
            var data = info.Tracks.Where(t => !t.IsAudio).ToList();
            return data.Count > 0 ? data[data.Count - 1].StartLba : 0;
        }

        void TryReadCdText(DiscInfo info)
        {
            const int size = 4 + 18 * 8 * 255;
            Marshal.WriteInt32(ioBuffer, 0, 0x05);    // CDROM_READ_TOC_EX_FORMAT_CDTEXT
            if (!Ioctl(Win32.IOCTL_CDROM_READ_TOC_EX, 4, size, out int returned) || returned < 4 + 18) return;
            var buf = new byte[returned];
            Marshal.Copy(ioBuffer, buf, 0, returned);
            int length = (buf[0] << 8) | buf[1];
            int packs = Math.Min((length - 2) / 18, (returned - 4) / 18);
            var parsed = CdText.Parse(buf, 4, packs);
            if (parsed == null) return;

            info.HasCdText = true;
            if (parsed.TryGetValue((0x80, 0), out var album)) info.AlbumTitle = album;
            if (parsed.TryGetValue((0x81, 0), out var albumArtist)) info.AlbumArtist = albumArtist;
            if (parsed.TryGetValue((0x87, 0), out var genre)) info.Genre = genre;
            foreach (var t in info.Tracks)
            {
                if (parsed.TryGetValue((0x80, t.Number), out var title)) t.Title = title;
                if (parsed.TryGetValue((0x81, t.Number), out var artist)) t.Artist = artist;
                if (parsed.TryGetValue((0x8E, t.Number), out var isrc)) t.Isrc = isrc;
            }
        }

        /// <summary>Reads CD-DA sectors (2352 bytes each). Returns false on a read error.</summary>
        public bool ReadAudio(int lba, int count, byte[] dest, int destOffset)
        {
            if (count < 1 || count > MaxRawSectorsPerRead) throw new ArgumentOutOfRangeException(nameof(count));
            var info = new byte[16];
            BitConverter.GetBytes((long)lba * CookedSectorSize).CopyTo(info, 0);
            BitConverter.GetBytes(count).CopyTo(info, 8);
            BitConverter.GetBytes(2).CopyTo(info, 12);   // TRACK_MODE_TYPE.CDDA
            Marshal.Copy(info, 0, ioBuffer, 16);
            int bytes = count * RawSectorSize;
            if (!Win32.DeviceIoControl(handle, Win32.IOCTL_CDROM_RAW_READ, ioBuffer, 16, rawBuffer, bytes, out int got, IntPtr.Zero))
                return false;
            if (got < bytes) return false;
            Marshal.Copy(rawBuffer, dest, destOffset, bytes);
            return true;
        }

        /// <summary>
        /// Reads audio with retries. Falls back to single sectors on failure and fills unreadable
        /// sectors with silence. Returns how many sectors could not be read.
        /// </summary>
        public int ReadAudioRobust(int lba, int count, byte[] dest, int destOffset, int retries = 4)
        {
            for (int attempt = 0; attempt <= retries; attempt++)
                if (ReadAudio(lba, count, dest, destOffset)) return 0;

            int bad = 0;
            for (int i = 0; i < count; i++)
            {
                bool ok = false;
                for (int attempt = 0; attempt <= retries && !ok; attempt++)
                    ok = ReadAudio(lba + i, 1, dest, destOffset + i * RawSectorSize);
                if (!ok)
                {
                    Array.Clear(dest, destOffset + i * RawSectorSize, RawSectorSize);
                    bad++;
                }
            }
            return bad;
        }

        /// <summary>Reads user data (2048-byte "cooked") sectors through the volume.</summary>
        public bool ReadData(int lba, int count, byte[] dest, int destOffset = 0)
        {
            if (!extendedDasd)
            {
                Ioctl(Win32.FSCTL_ALLOW_EXTENDED_DASD_IO, 0, 0, out _);
                extendedDasd = true;
            }
            while (count > 0)
            {
                int n = Math.Min(count, MaxCookedSectorsPerRead);
                if (!Win32.SetFilePointerEx(handle, (long)lba * CookedSectorSize, out _, 0)) return false;
                if (!Win32.ReadFile(handle, cookedBuffer, n * CookedSectorSize, out int got, IntPtr.Zero) || got < n * CookedSectorSize)
                    return false;
                Marshal.Copy(cookedBuffer, dest, destOffset, n * CookedSectorSize);
                lba += n; count -= n; destOffset += n * CookedSectorSize;
            }
            return true;
        }

        public byte[] ReadData(int lba, int count)
        {
            var b = new byte[count * CookedSectorSize];
            return ReadData(lba, count, b) ? b : null;
        }
    }

    /// <summary>CD-Text pack parser (block 0, ISO-8859-1 text).</summary>
    public static class CdText
    {
        public static Dictionary<(int type, int track), string> Parse(byte[] buf, int offset, int packCount)
        {
            if (packCount <= 0) return null;
            var byType = new Dictionary<int, List<(int seq, int track, byte[] text)>>();
            for (int i = 0; i < packCount; i++)
            {
                int o = offset + i * 18;
                int type = buf[o];
                if (type < 0x80 || type > 0x8F) continue;
                int track = buf[o + 1] & 0x7F;
                int seq = buf[o + 2];
                int block = (buf[o + 3] >> 4) & 0x07;
                bool doubleByte = (buf[o + 3] & 0x80) != 0;
                if (block != 0 || doubleByte) continue;
                var text = new byte[12];
                Array.Copy(buf, o + 4, text, 0, 12);
                if (!byType.TryGetValue(type, out var list)) byType[type] = list = new List<(int, int, byte[])>();
                list.Add((seq, track, text));
            }
            if (byType.Count == 0) return null;

            var result = new Dictionary<(int, int), string>();
            var latin1 = Encoding.GetEncoding(28591);
            foreach (var kv in byType)
            {
                var packs = kv.Value.OrderBy(p => p.seq).ToList();
                var all = packs.SelectMany(p => p.text).ToArray();
                int track = packs[0].track;
                string previous = "";
                int start = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != 0) continue;
                    string raw = latin1.GetString(all, start, i - start);
                    string s = raw == "\t" ? previous : raw.Trim();
                    if (s.Length > 0) result[(kv.Key, track)] = s;
                    previous = s;
                    track++;
                    start = i + 1;
                }
            }
            return result;
        }

        /// <summary>
        /// Builds block 0 (ISO-8859-1, English) as 18-byte packs: titles, performers when there are any,
        /// and the three size-information packs that close the block. Index 0 of each list is the album.
        /// </summary>
        public static byte[] Build(string albumTitle, string albumPerformer, IList<string> titles, IList<string> performers)
        {
            bool anyPerformer = !string.IsNullOrWhiteSpace(albumPerformer) || performers.Any(p => !string.IsNullOrWhiteSpace(p));
            // A block holds at most 256 packs, so very long texts get shortened until everything fits.
            for (int max = 160; ; max = max * 2 / 3)
            {
                var packs = new List<byte[]>();
                var counts = new int[16];
                AddText(packs, counts, 0x80, new[] { albumTitle }.Concat(titles), max);
                if (anyPerformer) AddText(packs, counts, 0x81, new[] { albumPerformer }.Concat(performers), max);
                if (packs.Count + 3 > 256 && max > 8) continue;

                counts[0x0F] = 3;
                var info = new byte[36];
                info[1] = 1;                                // first track
                info[2] = (byte)titles.Count;               // last track
                for (int i = 0; i < 16; i++) info[4 + i] = (byte)counts[i];
                info[20] = (byte)(packs.Count + 3 - 1);     // last sequence number in block 0
                info[28] = 0x09;                            // English
                for (int i = 0; i < 3; i++)
                {
                    var p = new byte[18];
                    p[0] = 0x8F;
                    p[1] = (byte)i;
                    Array.Copy(info, i * 12, p, 4, 12);
                    packs.Add(p);
                }

                var result = new byte[packs.Count * 18];
                for (int i = 0; i < packs.Count; i++)
                {
                    var p = packs[i];
                    p[2] = (byte)i;
                    int crc = ~Crc16(p, 0, 16);
                    p[16] = (byte)(crc >> 8);
                    p[17] = (byte)crc;
                    Array.Copy(p, 0, result, i * 18, 18);
                }
                return result;
            }
        }

        static void AddText(List<byte[]> packs, int[] counts, int type, IEnumerable<string> texts, int max)
        {
            var bytes = new List<byte>();
            var owner = new List<(int track, int pos)>();
            int track = 0;
            foreach (var t in texts)
            {
                var b = Latin1(t);
                int n = Math.Min(b.Length, max);
                for (int i = 0; i <= n; i++) { bytes.Add(i < n ? b[i] : (byte)0); owner.Add((track, i)); }
                track++;
            }
            for (int o = 0; o < bytes.Count; o += 12)
            {
                var p = new byte[18];
                p[0] = (byte)type;
                p[1] = (byte)owner[o].track;                    // the string this pack's first character belongs to
                p[3] = (byte)Math.Min(15, owner[o].pos);        // characters of that string in earlier packs
                for (int j = 0; j < 12 && o + j < bytes.Count; j++) p[4 + j] = bytes[o + j];
                packs.Add(p);
                counts[type & 0x0F]++;
            }
        }

        /// <summary>Folds text into ISO-8859-1: typographic punctuation to ASCII, other letters to their base letter.</summary>
        static byte[] Latin1(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Trim().Normalize(NormalizationForm.FormC))
            {
                if (c < 0x20 || (c >= 0x7F && c < 0xA0) || char.IsLowSurrogate(c)) continue;   // a lone tab would mean "same as before"
                if (c <= 0xFF) { sb.Append(c); continue; }
                switch (c)
                {
                    case '‘': case '’': case '′': sb.Append('\''); break;
                    case '“': case '”': case '″': sb.Append('"'); break;
                    case '‐': case '–': case '—': sb.Append('-'); break;
                    case '…': sb.Append("..."); break;
                    default:
                        char b = c.ToString().Normalize(NormalizationForm.FormD)[0];
                        sb.Append(b <= 0xFF ? b : '?');
                        break;
                }
            }
            return Encoding.GetEncoding(28591).GetBytes(sb.ToString());
        }

        /// <summary>CRC-16/CCITT (x¹⁶ + x¹² + x⁵ + 1, initial 0); CD-Text stores it inverted.</summary>
        public static int Crc16(byte[] b, int offset, int count)
        {
            int crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc ^= b[offset + i] << 8;
                for (int k = 0; k < 8; k++) crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1) & 0xFFFF;
            }
            return crc;
        }

        /// <summary>
        /// The packs as R-W subcode symbols, 6 bits per byte with P and Q left clear: 24 per pack,
        /// so each 96-byte sector of lead-in subcode carries four whole packs.
        /// </summary>
        public static byte[] ToSubcode(byte[] packs)
        {
            var s = new byte[packs.Length / 3 * 4];
            for (int i = 0, o = 0; i + 2 < packs.Length; i += 3, o += 4)
            {
                s[o] = (byte)(packs[i] >> 2);
                s[o + 1] = (byte)(((packs[i] & 0x03) << 4) | (packs[i + 1] >> 4));
                s[o + 2] = (byte)(((packs[i + 1] & 0x0F) << 2) | (packs[i + 2] >> 6));
                s[o + 3] = (byte)(packs[i + 2] & 0x3F);
            }
            return s;
        }
    }
}
