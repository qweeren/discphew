using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Discphew.Audio;
using Discphew.UI;

namespace Discphew.Tests
{
    /// <summary>Cue sheet parsing, and burning cue tracks out of one file.</summary>
    static partial class TestProgram
    {
        static void CueParsing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "discphew-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "Artist - Album.flac"), new byte[0]);

                // One file for the album, as EAC writes it: pregaps (INDEX 00) and hidden audio before track 1.
                var single = CueSheet.Parse(string.Join("\r\n",
                    "REM GENRE Hip-Hop",
                    "CATALOG 0602537387046",
                    "PERFORMER \"Kanye West\"",
                    "TITLE \"Yeezus\"",
                    "FILE \"Artist - Album.wav\" WAVE",
                    "  TRACK 01 AUDIO",
                    "    TITLE \"On Sight\"",
                    "    INDEX 00 00:00:00",
                    "    INDEX 01 00:00:32",
                    "  TRACK 02 AUDIO",
                    "    TITLE \"Black Skinhead\"",
                    "    PERFORMER \"Someone Else\"",
                    "    ISRC USUM71307491",
                    "    INDEX 00 02:35:70",
                    "    INDEX 01 02:36:00",
                    "  TRACK 03 AUDIO",
                    "    INDEX 01 05:44:10"), dir);
                Equal("Yeezus", single.Title, "album title");
                Equal("Kanye West", single.Performer, "album performer");
                Equal("0602537387046", single.Catalog, "catalog number");
                Equal(3, single.Tracks.Count, "track count");
                Check(single.Tracks.All(t => t.FileFound && t.File.EndsWith(".flac")), "a cue written for a WAV finds the FLAC");
                Equal(0L, single.Tracks[0].Start, "track 1 starts at the top of the file (hidden audio kept)");
                Equal((2 * 60 + 36) * 75L, single.Tracks[0].End, "track 1 runs to track 2's INDEX 01 (pregap stays with it)");
                Equal((2 * 60 + 36) * 75L, single.Tracks[1].Start, "track 2 starts at its INDEX 01");
                Equal((5 * 60 + 44) * 75L + 10, single.Tracks[1].End, "track 2 ends where track 3 starts");
                Equal(-1L, single.Tracks[2].End, "the last track runs to the end of the file");
                Equal("Kanye West", single.Tracks[0].Performer, "tracks inherit the album performer");
                Equal("Someone Else", single.Tracks[1].Performer, "track performer");
                Equal("USUM71307491", single.Tracks[1].Isrc, "ISRC");
                Equal("", single.Tracks[2].Title, "untitled track");

                // One file per track, gaps appended to the end of the track before; unquoted names; a data track.
                var multi = CueSheet.Parse(string.Join("\n",
                    "FILE 01 First.wav WAVE",
                    "  TRACK 01 AUDIO",
                    "    INDEX 01 00:00:00",
                    "  TRACK 02 AUDIO",
                    "    TITLE Second Song",
                    "    INDEX 00 03:00:00",
                    "FILE \"02 Second.wav\" WAVE",
                    "    INDEX 01 00:00:00",
                    "FILE \"data.bin\" BINARY",
                    "  TRACK 03 MODE1/2352",
                    "    INDEX 01 00:00:00"), dir);
                Equal(2, multi.Tracks.Count, "data tracks are left out");
                Equal(Path.Combine(dir, "01 First.wav"), multi.Tracks[0].File, "unquoted file name with spaces");
                Check(!multi.Tracks[0].FileFound, "missing file is reported");
                Equal(-1L, multi.Tracks[0].End, "track 1 keeps its whole file, appended gap included");
                Equal(Path.Combine(dir, "02 Second.wav"), multi.Tracks[1].File, "track 2 belongs to the file of its INDEX 01");
                Equal("Second Song", multi.Tracks[1].Title, "unquoted title");

                // Old rippers write the ANSI code page rather than UTF-8.
                string cuePath = Path.Combine(dir, "ansi.cue");
                File.WriteAllBytes(cuePath, Encoding.Default.GetBytes("TITLE \"Café\"\r\nFILE \"Artist - Album.flac\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n"));
                Equal("Café", CueSheet.Load(cuePath).Title, "ANSI cue sheet");
                File.WriteAllText(cuePath, "TITLE \"Æon — 日本\"\r\n", new UTF8Encoding(true));
                Equal("Æon — 日本", CueSheet.Load(cuePath).Title, "UTF-8 cue sheet with BOM");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void CueSplitting()
        {
            string dir = Path.Combine(Path.GetTempPath(), "discphew-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 30 seconds of 44.1 kHz stereo where every sample says where it is.
                const int frames = 44100 * 30;
                var pcm = new byte[frames * 4];
                for (int i = 0; i < frames; i++)
                {
                    pcm[i * 4] = (byte)i; pcm[i * 4 + 1] = (byte)(i >> 8);
                    pcm[i * 4 + 2] = (byte)(i >> 16); pcm[i * 4 + 3] = (byte)(i >> 24);
                }
                string wav = Path.Combine(dir, "album.wav");
                using (var w = new BinaryWriter(File.Create(wav)))
                {
                    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
                    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
                }
                var cue = CueSheet.Parse("FILE \"album.wav\" WAVE\n TRACK 01 AUDIO\n  INDEX 01 00:00:00\n TRACK 02 AUDIO\n  INDEX 01 00:08:37\n TRACK 03 AUDIO\n  INDEX 01 00:19:01\n", dir);
                var tracks = cue.Tracks.Select(t => (t.File, t.Start, t.End)).ToList();

                byte[] Read(List<string> files) => files.SelectMany(File.ReadAllBytes).ToArray();
                var inOrder = BurnerView.PreparePcm(tracks, dir, null);
                Check(Read(inOrder).SequenceEqual(pcm), "cue tracks in order put the file back together exactly");
                Equal((8 * 75 + 37) * 2352L, new FileInfo(inOrder[0]).Length, "track 1 ends at track 2's INDEX 01");

                // Backwards, so every track reopens the file and seeks to its start.
                var reversed = Enumerable.Reverse(tracks).ToList();
                var sub = Directory.CreateDirectory(Path.Combine(dir, "rev")).FullName;
                var backwards = BurnerView.PreparePcm(reversed, sub, null);
                Check(Read(Enumerable.Reverse(backwards).ToList()).SequenceEqual(pcm), "reordered cue tracks seek to the same samples");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
