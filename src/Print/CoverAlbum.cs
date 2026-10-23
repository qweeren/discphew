using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Discphew.Audio;
using Discphew.Disc;

namespace Discphew.Print
{
    public sealed class CoverTrack
    {
        public int Number;
        public string Title = "", Artist = "";
        public TimeSpan Length;
    }

    /// <summary>
    /// What a cover needs to know about an album, and what identifies its release on MusicBrainz:
    /// a release ID from the tags, a track layout to compute the disc ID from, or failing both its name.
    /// </summary>
    public sealed class CoverAlbum
    {
        public string Artist = "", Title = "", Year = "";
        public readonly List<CoverTrack> Tracks = new List<CoverTrack>();
        public string ReleaseId = "";       // MUSICBRAINZ_ALBUMID, or the release a lookup of this disc settled on
        public int DiscNumber;
        public DiscInfo Toc;                // the disc, or the one these files make when burned
        public byte[] Cover;                // the album's own front cover, if it came with one
        public string Source = "";

        public TimeSpan Total => TimeSpan.FromTicks(Tracks.Sum(t => t.Length.Ticks));

        public CoverAlbum Copy()
        {
            var a = new CoverAlbum { Artist = Artist, Title = Title, Year = Year, ReleaseId = ReleaseId, DiscNumber = DiscNumber, Toc = Toc, Cover = Cover, Source = Source };
            a.Tracks.AddRange(Tracks.Select(t => new CoverTrack { Number = t.Number, Title = t.Title, Artist = t.Artist, Length = t.Length }));
            return a;
        }

        public static CoverAlbum FromDisc(DiscInfo d)
        {
            var a = new CoverAlbum
            {
                Artist = d.AlbumArtist, Title = d.AlbumTitle, Year = d.Year, ReleaseId = d.MusicBrainzReleaseId,
                Toc = d, Source = $"the disc in {d.Drive}:",
            };
            foreach (var t in d.AudioTracks)
                a.Tracks.Add(new CoverTrack { Number = t.Number, Title = t.Title, Artist = t.Artist, Length = t.Duration });
            try { if (File.Exists(d.CoverPath)) a.Cover = File.ReadAllBytes(d.CoverPath); } catch (IOException) { }
            return a;
        }

        /// <summary>
        /// An album folder: one file per track, or one file per album (or several) with a cue sheet. Track lengths are
        /// rounded up to whole CD sectors, as the Burner writes them, so a lossless rip has its CD's disc ID.
        /// </summary>
        public static CoverAlbum FromFolder(string folder)
        {
            var a = new CoverAlbum { Source = folder };
            var cue = Directory.GetFiles(folder, "*.cue").Select(TryLoadCue).FirstOrDefault(c => c != null && c.Tracks.Count > 0 && c.Tracks.All(t => t.FileFound));
            if (cue != null)
            {
                a.Title = cue.Title;
                a.Artist = cue.Performer;
                var fileSectors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in cue.Tracks)
                {
                    long end = t.End;
                    if (end < 0)
                    {
                        if (!fileSectors.TryGetValue(t.File, out end)) fileSectors[t.File] = end = Sectors(t.File);
                        if (end <= t.Start) end = t.Start;
                    }
                    a.Tracks.Add(new CoverTrack { Number = t.Number, Title = t.Title, Artist = t.Performer, Length = TimeSpan.FromSeconds((end - t.Start) / 75.0) });
                }
                a.Toc = MusicBrainz.LayOut(a.Tracks.Select(t => (long)Math.Round(t.Length.TotalSeconds * 75)));
                // The cue sheet names the album; the files' tags can still say which release it is.
                try
                {
                    using var r = AudioFile.Open(cue.Tracks[0].File);
                    a.ReleaseId = r.Tag("MUSICBRAINZ_ALBUMID");
                    a.DiscNumber = int.TryParse(r.Tag("DISCNUMBER").Split('/')[0], out int disc) ? disc : 0;
                    a.Year = YearOf(r.Tag("DATE"));
                    if (a.Title.Length == 0) a.Title = r.Tag("ALBUM");
                    if (a.Artist.Length == 0) a.Artist = r.Tag("ALBUMARTIST").Length > 0 ? r.Tag("ALBUMARTIST") : r.Tag("ARTIST");
                    a.Cover = r.Picture;
                }
                catch (Exception e) when (e is IOException || e is NotSupportedException || e is InvalidDataException) { }
                a.Cover = a.Cover ?? FolderAlbumSource.CoverFile(folder);
                return a;
            }

            using var src = new FolderAlbumSource(folder);
            a.Title = src.Album;
            a.Artist = src.Artist;
            a.Year = src.Year;
            a.ReleaseId = src.ReleaseId;
            a.DiscNumber = src.DiscNumber;
            a.Cover = src.Cover;
            foreach (var t in src.Tracks)
                a.Tracks.Add(new CoverTrack { Number = t.Number, Title = t.Title, Artist = t.Artist, Length = t.Duration(src.SampleRate) });
            a.Toc = MusicBrainz.LayOut(src.Tracks.Select(t => (long)Math.Ceiling(t.Frames * 44100.0 / src.SampleRate / 588)));
            return a;
        }

        static CueSheet TryLoadCue(string path)
        {
            try { return CueSheet.Load(path); }
            catch (IOException) { return null; }
        }

        /// <summary>A file's length in CD sectors, rounded up.</summary>
        static long Sectors(string path)
        {
            using var r = AudioFile.Open(path);
            if (r.TotalFrames > 0) return (long)Math.Ceiling(r.TotalFrames * 44100.0 / r.SampleRate / 588);
            return (long)Math.Ceiling((Ffmpeg.ProbeDuration(path) ?? TimeSpan.Zero).TotalSeconds * 75);
        }

        static string YearOf(string date) => date.Length >= 4 ? date.Substring(0, 4) : date;

        /// <summary>
        /// Fills in what this album doesn't know from the release: names, year, and track titles and lengths where the
        /// album has none (a disc with no CD-Text). What the tags or the disc already say is kept.
        /// </summary>
        public void Fill(ReleaseMatch r)
        {
            if (r == null) return;
            if (Title.Length == 0) Title = r.Title;
            if (Artist.Length == 0) Artist = r.Artist;
            if (Year.Length == 0) Year = r.Year;
            if (Tracks.Count == 0)
                for (int i = 0; i < r.Tracks.Count; i++)
                    Tracks.Add(new CoverTrack { Number = i + 1 });
            for (int i = 0; i < Tracks.Count && i < r.Tracks.Count; i++)
            {
                var t = Tracks[i];
                if (t.Title.Length == 0) t.Title = r.Tracks[i].title;
                if (t.Artist.Length == 0) t.Artist = r.Tracks[i].artist;
                if (t.Length == TimeSpan.Zero) t.Length = r.Tracks[i].length;
            }
        }
    }
}
