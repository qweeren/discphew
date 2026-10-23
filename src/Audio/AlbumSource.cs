using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Discphew.Disc;

namespace Discphew.Audio
{
    public sealed class PlayTrack
    {
        public int Number;
        public string Title = "", Artist = "";
        public long StartFrame, Frames;
        public TimeSpan Duration(int rate) => TimeSpan.FromSeconds(Frames / (double)rate);
    }

    /// <summary>An album as one continuous stream of 16-bit stereo PCM, so playback is gapless.</summary>
    public interface IAlbumSource : IDisposable
    {
        int SampleRate { get; }
        string Album { get; }
        string Artist { get; }
        string Year { get; }
        byte[] Cover { get; }
        IReadOnlyList<PlayTrack> Tracks { get; }
        long TotalFrames { get; }
        bool IsDisc { get; }
        int Read(byte[] buffer, int count);
        void Seek(long frame);
    }

    /// <summary>Plays straight off the disc, including pregaps, exactly like a CD player.</summary>
    public sealed class CdAlbumSource : IAlbumSource
    {
        readonly CdDrive drive;
        readonly int firstLba, endLba;
        readonly byte[] buf = new byte[CdDrive.MaxRawSectorsPerRead * CdDrive.RawSectorSize];
        int bufLba = -1, bufCount;
        long pos;     // frame position from firstLba
        readonly List<PlayTrack> tracks = new List<PlayTrack>();

        public int SampleRate => 44100;
        public string Album { get; }
        public string Artist { get; }
        public string Year { get; }
        public byte[] Cover { get; }
        public IReadOnlyList<PlayTrack> Tracks => tracks;
        public long TotalFrames => (long)(endLba - firstLba) * 588;
        public bool IsDisc => true;
        public int ReadErrors { get; private set; }

        public CdAlbumSource(DiscInfo d)
        {
            var audio = d.AudioTracks.ToList();
            if (audio.Count == 0) throw new DiscException("There are no audio tracks on this disc.");
            firstLba = audio[0].StartLba;
            endLba = audio[audio.Count - 1].EndLba;
            foreach (var t in audio)
                tracks.Add(new PlayTrack
                {
                    Number = t.Number,
                    Title = t.Title.Length > 0 ? t.Title : $"Track {t.Number}",
                    Artist = t.Artist.Length > 0 ? t.Artist : d.AlbumArtist,
                    StartFrame = (long)(t.StartLba - firstLba) * 588,
                    Frames = (long)t.Sectors * 588,
                });
            Album = d.AlbumTitle.Length > 0 ? d.AlbumTitle : "Audio CD";
            Artist = d.AlbumArtist;
            Year = d.Year;
            try { if (File.Exists(d.CoverPath)) Cover = File.ReadAllBytes(d.CoverPath); } catch { }
            drive = new CdDrive(d.Drive);
        }

        public int Read(byte[] buffer, int count)
        {
            int written = 0;
            count &= ~3;
            while (written < count)
            {
                long frame = pos;
                int lba = firstLba + (int)(frame / 588);
                if (lba >= endLba) break;
                if (lba < bufLba || lba >= bufLba + bufCount)
                {
                    int n = Math.Min(CdDrive.MaxRawSectorsPerRead, endLba - lba);
                    ReadErrors += drive.ReadAudioRobust(lba, n, buf, 0, 1);
                    bufLba = lba; bufCount = n;
                }
                int offset = (lba - bufLba) * CdDrive.RawSectorSize + (int)(frame % 588) * 4;
                int avail = bufCount * CdDrive.RawSectorSize - offset;
                int take = Math.Min(avail, count - written);
                Buffer.BlockCopy(buf, offset, buffer, written, take);
                written += take;
                pos += take / 4;
            }
            return written;
        }

        public void Seek(long frame) => pos = Math.Max(0, Math.Min(TotalFrames, frame));
        public void Dispose() => drive.Dispose();
    }

    /// <summary>A folder of FLAC/WAV (or anything ffmpeg reads) played as one album.</summary>
    public sealed class FolderAlbumSource : IAlbumSource
    {
        readonly List<string> files;
        readonly List<PlayTrack> tracks = new List<PlayTrack>();
        int current = -1;
        IAudioReader reader;
        CdPcmStream pcm;
        long pos;

        public int SampleRate { get; }
        public string Album { get; } = "";
        public string Artist { get; } = "";
        public string Year { get; } = "";
        public byte[] Cover { get; }
        public IReadOnlyList<PlayTrack> Tracks => tracks;
        public long TotalFrames { get; }
        public bool IsDisc => false;
        public string Folder { get; }
        public string ReleaseId { get; } = "";      // MUSICBRAINZ_ALBUMID, as Picard tags files
        public int DiscNumber { get; }

        public static bool LooksLikeAlbum(string folder) =>
            Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(AudioFile.IsAudioFile);

        public FolderAlbumSource(string folder)
        {
            Folder = folder;
            var candidates = Directory.GetFiles(folder).Where(AudioFile.IsAudioFile).ToList();
            if (candidates.Count == 0) throw new IOException("No audio files in that folder.");
            var infos = new List<(string path, int number, string title, string artist, long frames, int rate)>();
            byte[] embedded = null;
            string album = "", albumArtist = "", year = "", releaseId = "", discNumber = "";
            foreach (var f in candidates)
            {
                try
                {
                    using var r = AudioFile.Open(f);
                    int number = int.TryParse(r.Tag("TRACKNUMBER").Split('/')[0], out var n) ? n : 0;
                    if (number == 0)
                    {
                        var m = Regex.Match(Path.GetFileName(f), @"^\s*(\d{1,3})");
                        if (m.Success) number = int.Parse(m.Groups[1].Value);
                    }
                    long frames = r.TotalFrames > 0 ? r.TotalFrames : (long)((Ffmpeg.ProbeDuration(f) ?? TimeSpan.Zero).TotalSeconds * r.SampleRate);
                    string title = r.Tag("TITLE");
                    if (title.Length == 0) title = Regex.Replace(Path.GetFileNameWithoutExtension(f), @"^\s*\d{1,3}\s*[-._ ]\s*", "");
                    infos.Add((f, number, title, r.Tag("ARTIST"), frames, r.SampleRate));
                    if (album.Length == 0) album = r.Tag("ALBUM");
                    if (albumArtist.Length == 0) albumArtist = r.Tag("ALBUMARTIST").Length > 0 ? r.Tag("ALBUMARTIST") : r.Tag("ARTIST");
                    if (year.Length == 0) year = r.Tag("DATE");
                    if (embedded == null) embedded = r.Picture;
                    if (releaseId.Length == 0) releaseId = r.Tag("MUSICBRAINZ_ALBUMID");
                    if (discNumber.Length == 0) discNumber = r.Tag("DISCNUMBER");
                }
                catch { }
            }
            if (infos.Count == 0) throw new IOException("None of the audio files in that folder could be opened.");
            infos = infos.OrderBy(i => i.number == 0 ? int.MaxValue : i.number).ThenBy(i => Path.GetFileName(i.path), StringComparer.OrdinalIgnoreCase).ToList();

            SampleRate = infos[0].rate;
            files = infos.Select(i => i.path).ToList();
            long start = 0;
            for (int i = 0; i < infos.Count; i++)
            {
                long frames = (long)(infos[i].frames * (double)SampleRate / infos[i].rate);
                tracks.Add(new PlayTrack
                {
                    Number = infos[i].number > 0 ? infos[i].number : i + 1,
                    Title = infos[i].title,
                    Artist = infos[i].artist.Length > 0 ? infos[i].artist : albumArtist,
                    StartFrame = start,
                    Frames = frames,
                });
                start += frames;
            }
            TotalFrames = start;
            Album = album.Length > 0 ? album : Path.GetFileName(folder);
            Artist = albumArtist;
            Year = year.Length >= 4 ? year.Substring(0, 4) : year;
            ReleaseId = releaseId;
            DiscNumber = int.TryParse(discNumber.Split('/')[0], out int disc) ? disc : 0;
            Cover = embedded ?? CoverFile(folder);
        }

        /// <summary>The cover image file next to an album's tracks (cover.jpg, folder.jpg…), or null.</summary>
        public static byte[] CoverFile(string folder)
        {
            foreach (var name in new[] { "cover.jpg", "folder.jpg", "front.jpg", "cover.png", "folder.png", "front.png" })
            {
                var p = Path.Combine(folder, name);
                if (File.Exists(p)) { try { return File.ReadAllBytes(p); } catch { } break; }
            }
            return null;
        }

        void Open(int index, long localFrame)
        {
            reader?.Dispose();
            reader = null; pcm = null;
            current = index;
            if (index < 0 || index >= files.Count) return;
            reader = AudioFile.Open(files[index]);
            if (localFrame > 0) reader.Seek((long)(localFrame * (double)reader.SampleRate / SampleRate));
            pcm = new CdPcmStream(reader, SampleRate);
        }

        public int Read(byte[] buffer, int count)
        {
            int written = 0;
            count &= ~3;
            while (written < count)
            {
                if (pcm == null)
                {
                    int idx = tracks.FindIndex(t => pos < t.StartFrame + t.Frames);
                    if (idx < 0) break;
                    Open(idx, pos - tracks[idx].StartFrame);
                }
                int n = pcm.Read(buffer, written, count - written);
                if (n == 0)
                {
                    // Track ended; jump to the next track's start (frame estimates can be slightly off).
                    if (current + 1 >= tracks.Count) { pcm = null; reader?.Dispose(); reader = null; pos = TotalFrames; break; }
                    pos = tracks[current + 1].StartFrame;
                    Open(current + 1, 0);
                    continue;
                }
                written += n;
                pos += n / 4;
                if (current >= 0 && current + 1 < tracks.Count && pos > tracks[current + 1].StartFrame) pos = tracks[current + 1].StartFrame - 1;
            }
            return written;
        }

        public void Seek(long frame)
        {
            pos = Math.Max(0, Math.Min(TotalFrames, frame));
            int idx = tracks.FindIndex(t => pos < t.StartFrame + t.Frames);
            if (idx < 0) { reader?.Dispose(); reader = null; pcm = null; return; }
            Open(idx, pos - tracks[idx].StartFrame);
        }

        public void Dispose() => reader?.Dispose();
    }
}
