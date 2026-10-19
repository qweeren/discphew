using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Discphew.Disc
{
    /// <summary>
    /// Reads the file layout of a data track. ISO 9660 (and its Joliet extension) records the
    /// starting sector of every file, which is exactly what the visualizer needs. For discs that
    /// only carry UDF we fall back to listing files through Windows, without positions.
    /// </summary>
    public static class FileSystemReader
    {
        const int MaxEntries = 250_000;

        public static FileSystemInfo Read(CdDrive drive, DiscInfo disc)
        {
            var data = disc.Tracks.Where(t => !t.IsAudio).ToList();
            if (data.Count == 0) return null;

            var starts = new List<int> { data[data.Count - 1].StartLba, data[0].StartLba, 0 };
            foreach (int start in starts.Distinct())
            {
                try
                {
                    var fs = TryIso(drive, start);
                    if (fs != null) return fs;
                }
                catch (Exception) { }
            }
            return EnumerateMounted(disc.Drive);
        }

        static FileSystemInfo TryIso(CdDrive drive, int sessionStart)
        {
            byte[] pvd = null, joliet = null;
            bool udf = false;
            for (int i = 16; i < 48; i++)
            {
                var d = drive.ReadData(sessionStart + i, 1);
                if (d == null) break;
                string id = Encoding.ASCII.GetString(d, 1, 5);
                if (id == "BEA01" || id == "NSR02" || id == "NSR03") { udf |= id.StartsWith("NSR"); continue; }
                if (id == "TEA01") break;
                if (id != "CD001") { if (pvd != null) break; continue; }
                if (d[0] == 1 && pvd == null) pvd = d;
                else if (d[0] == 2 && d[88] == 0x25 && d[89] == 0x2F && (d[90] == 0x40 || d[90] == 0x43 || d[90] == 0x45)) joliet = d;
                else if (d[0] == 255) continue;
            }
            if (pvd == null) return null;

            bool useJoliet = joliet != null;
            var desc = useJoliet ? joliet : pvd;
            var fs = new FileSystemInfo
            {
                Kind = "ISO 9660" + (useJoliet ? " + Joliet" : "") + (udf ? " + UDF" : ""),
                VolumeSectors = BitConverter.ToInt32(pvd, 80),
                HasPositions = true,
            };
            fs.VolumeLabel = useJoliet
                ? Encoding.BigEndianUnicode.GetString(desc, 40, 32).Trim().TrimEnd('\0')
                : Encoding.ASCII.GetString(desc, 40, 32).Trim();

            int rootExtent = BitConverter.ToInt32(desc, 156 + 2);
            int rootSize = BitConverter.ToInt32(desc, 156 + 10);
            var visited = new HashSet<int>();
            Walk(drive, fs, rootExtent, rootSize, "", useJoliet, 0, visited);
            return fs;
        }

        static void Walk(CdDrive drive, FileSystemInfo fs, int extent, int size, string path, bool joliet, int depth, HashSet<int> visited)
        {
            if (depth > 64 || !visited.Add(extent) || size <= 0 || fs.Entries.Count > MaxEntries) return;
            int sectors = (size + 2047) / 2048;
            var data = drive.ReadData(extent, sectors);
            if (data == null) return;

            FsEntry multiExtent = null;
            int pos = 0;
            while (pos < size && pos < data.Length)
            {
                int len = data[pos];
                if (len == 0)
                {
                    pos = (pos / 2048 + 1) * 2048;
                    continue;
                }
                if (pos + len > data.Length || len < 34) break;

                int lba = BitConverter.ToInt32(data, pos + 2);
                int length = BitConverter.ToInt32(data, pos + 10);
                byte flags = data[pos + 25];
                int nameLen = data[pos + 32];
                bool isDir = (flags & 0x02) != 0;

                if (nameLen == 1 && (data[pos + 33] == 0 || data[pos + 33] == 1))
                {
                    pos += len;
                    continue;
                }

                string name = joliet
                    ? Encoding.BigEndianUnicode.GetString(data, pos + 33, nameLen & ~1)
                    : Encoding.ASCII.GetString(data, pos + 33, nameLen);
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name.Substring(0, semi);
                if (!joliet && !isDir && name.EndsWith(".")) name = name.Substring(0, name.Length - 1);

                string full = path.Length == 0 ? name : path + "\\" + name;
                DateTime date = ParseDate(data, pos + 18);

                if (multiExtent != null && multiExtent.Path == full)
                {
                    multiExtent.Size += (uint)length;
                }
                else
                {
                    var e = new FsEntry { Path = full, Size = (uint)length, Lba = lba, IsDirectory = isDir, Modified = date };
                    fs.Entries.Add(e);
                    if (isDir) Walk(drive, fs, lba, length, full, joliet, depth + 1, visited);
                    multiExtent = (flags & 0x80) != 0 ? e : null;
                }
                if ((flags & 0x80) == 0) multiExtent = null;
                pos += len;
            }
        }

        static DateTime ParseDate(byte[] d, int o)
        {
            try
            {
                if (d[o + 1] == 0) return DateTime.MinValue;
                return new DateTime(1900 + d[o], d[o + 1], d[o + 2], d[o + 3], d[o + 4], d[o + 5]);
            }
            catch { return DateTime.MinValue; }
        }

        static FileSystemInfo EnumerateMounted(char letter)
        {
            var root = letter + ":\\";
            var di = new DriveInfo(root);
            if (!di.IsReady) return null;
            var fs = new FileSystemInfo { Kind = di.DriveFormat, VolumeLabel = di.VolumeLabel, HasPositions = false };
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0 && fs.Entries.Count < MaxEntries)
            {
                var dir = stack.Pop();
                IEnumerable<System.IO.FileSystemInfo> items;
                try { items = dir.EnumerateFileSystemInfos().ToList(); }
                catch { continue; }
                foreach (var item in items)
                {
                    string rel = item.FullName.Substring(root.Length);
                    if (item is DirectoryInfo sub)
                    {
                        fs.Entries.Add(new FsEntry { Path = rel, IsDirectory = true, Modified = item.LastWriteTime });
                        stack.Push(sub);
                    }
                    else if (item is FileInfo f)
                    {
                        fs.Entries.Add(new FsEntry { Path = rel, Size = f.Length, Modified = f.LastWriteTime });
                    }
                }
            }
            fs.VolumeSectors = (int)Math.Min(int.MaxValue, (di.TotalSize + 2047) / 2048);
            return fs;
        }
    }
}
