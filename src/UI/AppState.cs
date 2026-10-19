using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Disc;

namespace Discphew.UI
{
    /// <summary>The selected drive and what we know about the disc in it. Shared by every tool.</summary>
    sealed class AppState
    {
        readonly Control ui;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 2000 };
        readonly Dictionary<char, string> labels = new Dictionary<char, string>();
        int generation;
        string busyReason;
        bool polling, mediaSeen, labelsPending;

        public char Drive { get; private set; }
        public DiscInfo Disc { get; private set; }
        public bool Loading { get; private set; }
        public bool MediaLoading { get; private set; }
        public string Message { get; private set; } = "";
        public bool Busy => busyReason != null;
        public string BusyReason => busyReason;

        /// <summary>Raised on the UI thread whenever anything above changes.</summary>
        public event Action Changed;

        /// <summary>Raised on the UI thread when the drives' volume labels have been re-read.</summary>
        public event Action LabelsChanged;

        public AppState(Control ui)
        {
            this.ui = ui;
            poll.Tick += (s, e) => Poll();
            poll.Start();
        }

        public char[] Drives => CdDrive.Enumerate();

        /// <summary>"E:  LABEL" from the last time the labels were read. Never touches the drive.</summary>
        public string DriveName(char letter) =>
            labels.TryGetValue(letter, out var label) && label.Length > 0 ? $"{letter}:  {label}" : $"{letter}:";

        void Raise() => Changed?.Invoke();

        void Post(Action a)
        {
            if (ui.IsDisposed) return;
            try { ui.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        public void SelectDrive(char letter)
        {
            if (Busy || letter == Drive) return;
            Drive = letter;
            Settings.Set("drive", letter.ToString());
            Reload();
        }

        public void Reload()
        {
            if (Busy) return;
            int gen = ++generation;
            char letter = Drive;
            if (letter == '\0')
            {
                Disc = null; Loading = false; Message = "No optical drive found.";
                Raise();
                return;
            }
            Loading = true;
            Message = "Reading disc…";
            Raise();

            Task.Run(() =>
            {
                DiscInfo disc = null;
                string message = "";
                bool present = false;
                // Right after a disc goes in, the drive answers "not ready" for a few seconds while it spins up.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    try { (disc, message, present) = ReadDrive(letter); break; }
                    catch (Exception e) when (IsNotReady(e) && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(1000);
                        if (gen != Volatile.Read(ref generation)) return;
                    }
                    catch (Exception e) { message = e.Message; present = !IsNotReady(e); break; }   // still not ready: let Poll try again
                }

                Post(() =>
                {
                    if (gen != generation) return;
                    Disc = disc;
                    Message = message;
                    Loading = false;
                    MediaLoading = present;
                    mediaSeen = present;
                    Raise();
                    RefreshLabels();
                });

                if (!present) return;
                MediaInfo media = null;
                try { media = Imapi.QueryMedia(letter); } catch { }
                Post(() =>
                {
                    if (gen != generation) return;
                    MediaLoading = false;
                    if (Disc != null) Disc.Media = media;
                    if (Disc != null && Disc.Tracks.Count == 0 && media != null && media.Blank) Message = $"Blank {media.Name}";
                    Raise();
                });
            });
        }

        static (DiscInfo disc, string message, bool present) ReadDrive(char letter)
        {
            using var drive = new CdDrive(letter);
            if (!drive.MediaPresent()) return (null, "No disc in the drive.", false);
            try
            {
                var disc = drive.ReadDisc();
                if (disc.HasData) disc.FileSystem = FileSystemReader.Read(drive, disc);
                if (disc.HasAudio) MetadataCache.Load(disc);
                return (disc, "", true);
            }
            catch (DiscException e) when (!e.NotReady)
            {
                // Blank or unreadable: IMAPI can still tell us what it is.
                return (new DiscInfo { Drive = letter }, "No readable tracks (blank disc?)", true);
            }
        }

        static bool IsNotReady(Exception e) =>
            e is DiscException d ? d.NotReady : e is IOException && CdDrive.IsNotReady(e.HResult & 0xFFFF);

        /// <summary>
        /// While the drive looks empty, keep an eye on it: Windows doesn't always announce a disc
        /// going in, and when it does the drive is often still spinning up.
        /// </summary>
        void Poll()
        {
            if (polling || Busy || Loading || Disc != null || Drive == '\0') return;
            polling = true;
            char letter = Drive;
            int gen = generation;
            Task.Run(() =>
            {
                bool present = false;
                try { using var d = new CdDrive(letter); present = d.MediaPresent(); } catch { }
                Post(() =>
                {
                    polling = false;
                    if (gen != generation || Busy || Loading || Disc != null) return;
                    if (present && !mediaSeen) Reload();
                    else mediaSeen = present;
                });
            });
        }

        /// <summary>Re-reads every drive's volume label in the background.</summary>
        public void RefreshLabels()
        {
            if (labelsPending) return;
            labelsPending = true;
            var drives = Drives;
            Task.Run(() =>
            {
                var found = drives.Select(c => (c, label: CdDrive.VolumeLabel(c))).ToList();
                Post(() =>
                {
                    labelsPending = false;
                    labels.Clear();
                    foreach (var (c, label) in found) labels[c] = label;
                    LabelsChanged?.Invoke();
                });
            });
        }

        /// <summary>Notify listeners that the disc's metadata was edited.</summary>
        public void Touch() => Raise();

        /// <summary>Marks the drive as in use (ripping, burning, playing). Dispose to release and re-read the disc.</summary>
        public IDisposable Lock(string reason, bool reloadAfter = true)
        {
            busyReason = reason;
            Raise();
            return new Unlocker(() =>
            {
                busyReason = null;
                if (reloadAfter) Reload(); else Raise();
            });
        }

        sealed class Unlocker : IDisposable
        {
            Action a;
            public Unlocker(Action a) { this.a = a; }
            public void Dispose() { a?.Invoke(); a = null; }
        }

        public void PickInitialDrive()
        {
            var drives = Drives;
            var saved = Settings.Get("drive");
            Drive = saved.Length == 1 && drives.Contains(saved[0]) ? saved[0] : drives.FirstOrDefault();
            Reload();
        }

        public void DrivesChanged()
        {
            var drives = Drives;
            if (!drives.Contains(Drive)) { Drive = drives.FirstOrDefault(); }
            Reload();
        }
    }
}
