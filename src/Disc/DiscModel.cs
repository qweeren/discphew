using System;
using System.Collections.Generic;
using System.Linq;

namespace Discphew.Disc
{
    public sealed class TrackInfo
    {
        public int Number;
        public int StartLba;
        /// <summary>Exclusive end LBA (first sector that no longer belongs to the track).</summary>
        public int EndLba;
        public bool IsAudio;
        public bool PreEmphasis;
        public bool CopyPermitted;
        public int Session = 1;

        public string Title = "";
        public string Artist = "";
        public string Isrc = "";

        public int Sectors => Math.Max(0, EndLba - StartLba);
        public TimeSpan Duration => TimeSpan.FromSeconds(Sectors / 75.0);
        public long Bytes => IsAudio ? (long)Sectors * 2352 : (long)Sectors * 2048;
    }

    public sealed class FsEntry
    {
        public string Path = "";
        public long Size;
        public int Lba = -1;
        public bool IsDirectory;
        public DateTime Modified;

        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Path).ToLowerInvariant();
        public int Sectors => (int)((Size + 2047) / 2048);
    }

    public sealed class FileSystemInfo
    {
        public string Kind = "";          // "ISO 9660", "ISO 9660 + Joliet", "UDF", ...
        public string VolumeLabel = "";
        public int VolumeSectors;
        public bool HasPositions;          // true when we know where each file sits on the disc
        public List<FsEntry> Entries = new List<FsEntry>();
        public long TotalFileBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        public int FileCount => Entries.Count(e => !e.IsDirectory);
    }

    public enum MediaKind { Unknown, CdRom, CdR, CdRw, DvdRom, DvdRam, DvdPlusR, DvdPlusRw, DvdPlusRDl, DvdDashR, DvdDashRw, DvdDashRDl, DvdPlusRwDl, BdRom, BdR, BdRe, Other }

    public sealed class MediaInfo
    {
        public MediaKind Kind = MediaKind.Unknown;
        public bool Writable;
        public bool Blank;
        public bool Rewritable;
        public bool Appendable;
        public long TotalSectors;
        public long FreeSectors;
        public string RecorderName = "";
        public string RecorderId = "";
        public bool CanBurnCd, CanBurnDvd;

        public bool IsDvd => Kind >= MediaKind.DvdRom && Kind <= MediaKind.DvdPlusRwDl;
        public bool IsBluRay => Kind >= MediaKind.BdRom && Kind <= MediaKind.BdRe;

        public string Name => Kind switch
        {
            MediaKind.CdRom => "CD-ROM",
            MediaKind.CdR => "CD-R",
            MediaKind.CdRw => "CD-RW",
            MediaKind.DvdRom => "DVD-ROM",
            MediaKind.DvdRam => "DVD-RAM",
            MediaKind.DvdPlusR => "DVD+R",
            MediaKind.DvdPlusRw => "DVD+RW",
            MediaKind.DvdPlusRDl => "DVD+R DL",
            MediaKind.DvdDashR => "DVD-R",
            MediaKind.DvdDashRw => "DVD-RW",
            MediaKind.DvdDashRDl => "DVD-R DL",
            MediaKind.DvdPlusRwDl => "DVD+RW DL",
            MediaKind.BdRom => "BD-ROM",
            MediaKind.BdR => "BD-R",
            MediaKind.BdRe => "BD-RE",
            _ => "Disc",
        };
    }

    public sealed class DiscInfo
    {
        public char Drive;
        public int FirstTrack, LastTrack;
        public int LeadOutLba;
        public int SessionCount = 1;
        public List<TrackInfo> Tracks = new List<TrackInfo>();
        public MediaInfo Media;              // null until IMAPI answers (or if it can't)
        public FileSystemInfo FileSystem;    // null for pure audio discs

        public string AlbumTitle = "";
        public string AlbumArtist = "";
        public string Year = "";
        public string Genre = "";
        public string MusicBrainzReleaseId = "";
        public string CoverPath = "";        // cached cover art, if any
        public bool HasCdText;

        public IEnumerable<TrackInfo> AudioTracks => Tracks.Where(t => t.IsAudio);
        public bool HasAudio => Tracks.Any(t => t.IsAudio);
        public bool HasData => Tracks.Any(t => !t.IsAudio);
        public bool IsDvd => Media != null && (Media.IsDvd || Media.IsBluRay) || LeadOutLba > 405000;
        public TimeSpan AudioDuration => TimeSpan.FromSeconds(AudioTracks.Sum(t => t.Sectors) / 75.0);

        /// <summary>The value used for "used space": end of the last session's program area.</summary>
        public int UsedSectors => LeadOutLba;

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(AlbumTitle)) return AlbumTitle;
                if (FileSystem != null && !string.IsNullOrEmpty(FileSystem.VolumeLabel)) return FileSystem.VolumeLabel;
                if (HasAudio) return "Audio CD";
                return Media?.Name ?? "Disc";
            }
        }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 1024) return b + " B";
            double v = b;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + u[i];
        }

        public static string Time(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        public static string Msf(int lba)
        {
            int f = lba + 150;
            return $"{f / 4500:00}:{f / 75 % 60:00}.{f % 75:00}";
        }
    }
}
