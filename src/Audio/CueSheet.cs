using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Discphew.Audio
{
    /// <summary>
    /// A cue sheet's audio tracks, each resolved to a span of its source file in CD sectors (1/75 s).
    /// A track runs from its INDEX 01 to the next track's INDEX 01 in the same file (so pregaps stay with
    /// the track before, as they play on a gapless disc), or to the end of its file. The first track in each
    /// file starts at the top of the file, so nothing before its INDEX 01 is lost (hidden track one audio,
    /// gaps a ripper put at the head of the next file).
    /// </summary>
    public sealed class CueSheet
    {
        public sealed class Track
        {
            public int Number;
            public string File = "";        // full path; the cue's own name for it if no such file was found
            public bool FileFound;
            public string Title = "", Performer = "", Isrc = "";
            public long Start;              // sectors into File
            public long End = -1;           // sectors into File, -1 = to the end of it
        }

        public string Path = "";
        public string Title = "", Performer = "", Catalog = "";
        public readonly List<Track> Tracks = new List<Track>();

        public static CueSheet Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.Default.GetString(bytes); }      // older rippers write the ANSI code page
            var cue = Parse(text, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            cue.Path = path;
            return cue;
        }

        public static CueSheet Parse(string text, string folder)
        {
            var cue = new CueSheet();
            string file = null;
            bool found = false, firstInFile = false;
            Track track = null;
            foreach (var raw in text.TrimStart('﻿').Split('\n'))
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string key = line.Substring(0, sp).ToUpperInvariant(), rest = line.Substring(sp + 1).Trim();
                switch (key)
                {
                    case "FILE":
                        // FILE "name" TYPE, though some writers leave the quotes off names with spaces.
                        string name = rest.StartsWith("\"") ? rest.Substring(1, Math.Max(0, rest.IndexOf('"', 1) - 1)) : rest.Contains(' ') ? rest.Substring(0, rest.LastIndexOf(' ')) : rest;
                        file = Resolve(folder, name, out found);
                        firstInFile = true;
                        break;
                    case "TRACK":
                        track = null;
                        var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (file == null || parts.Length < 2 || !parts[1].Equals("AUDIO", StringComparison.OrdinalIgnoreCase)) break;
                        track = new Track { Number = int.TryParse(parts[0], out int n) ? n : cue.Tracks.Count + 1, File = file, FileFound = found, Start = -1 };
                        cue.Tracks.Add(track);
                        break;
                    case "TITLE":
                        if (track != null) track.Title = Unquote(rest); else if (cue.Tracks.Count == 0) cue.Title = Unquote(rest);
                        break;
                    case "PERFORMER":
                        if (track != null) track.Performer = Unquote(rest); else if (cue.Tracks.Count == 0) cue.Performer = Unquote(rest);
                        break;
                    case "ISRC":
                        if (track != null) track.Isrc = Unquote(rest);
                        break;
                    case "CATALOG":
                        cue.Catalog = Unquote(rest);
                        break;
                    case "INDEX":
                        var idx = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (track == null || idx.Length < 2 || !int.TryParse(idx[0], out int number) || number != 1) break;
                        // A track belongs to the file its INDEX 01 is in; its pregap may have been in the file before.
                        track.File = file;
                        track.FileFound = found;
                        track.Start = firstInFile ? 0 : Msf(idx[1]);
                        if (!firstInFile)
                        {
                            var previous = cue.Tracks.Count > 1 ? cue.Tracks[cue.Tracks.Count - 2] : null;
                            if (previous != null && previous.File == file) previous.End = Msf(idx[1]);
                        }
                        firstInFile = false;
                        break;
                }
            }
            cue.Tracks.RemoveAll(t => t.Start < 0);
            foreach (var t in cue.Tracks)
                if (t.Performer.Length == 0) t.Performer = cue.Performer;
            return cue;
        }

        /// <summary>The file the cue names, or one with the same name and another audio extension (the cue was written for a WAV that got compressed).</summary>
        static string Resolve(string folder, string name, out bool found)
        {
            string path = System.IO.Path.Combine(folder, name.Replace('/', '\\'));
            found = File.Exists(path);
            if (found) return path;
            string stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileNameWithoutExtension(path));
            foreach (var ext in AudioFile.NativeExtensions.Concat(AudioFile.FfmpegExtensions))
                if (File.Exists(stem + ext)) { found = true; return stem + ext; }
            return path;
        }

        static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;

        static long Msf(string s)
        {
            var p = s.Split(':');
            return p.Length == 3 && int.TryParse(p[0], out int m) && int.TryParse(p[1], out int sec) && int.TryParse(p[2], out int f) ? (m * 60L + sec) * 75 + f : 0;
        }
    }
}
