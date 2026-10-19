using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Disc;

namespace Discphew.UI
{
    /// <summary>The selected drive and what we know about the disc in it. Shared by every tool.</summary>
    sealed class AppState
    {
        readonly Control ui;
        int generation;
        string busyReason;

        public char Drive { get; private set; }
        public DiscInfo Disc { get; private set; }
        public bool Loading { get; private set; }
        public bool MediaLoading { get; private set; }
        public string Message { get; private set; } = "";
        public bool Busy => busyReason != null;
        public string BusyReason => busyReason;

        /// <summary>Raised on the UI thread whenever anything above changes.</summary>
        public event Action Changed;

        public AppState(Control ui) { this.ui = ui; }

        public char[] Drives => CdDrive.Enumerate();

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
                try
                {
                    using var drive = new CdDrive(letter);
                    present = drive.MediaPresent();
                    if (!present) message = "No disc in the drive.";
                    else
                    {
                        try
                        {
                            disc = drive.ReadDisc();
                            if (disc.HasData) disc.FileSystem = FileSystemReader.Read(drive, disc);
                            if (disc.HasAudio) MetadataCache.Load(disc);
                        }
                        catch (DiscException)
                        {
                            disc = new DiscInfo { Drive = letter };     // blank or unreadable: IMAPI can still tell us what it is
                            message = "No readable tracks (blank disc?)";
                        }
                    }
                }
                catch (Exception e) { message = e.Message; }

                Post(() =>
                {
                    if (gen != generation) return;
                    Disc = disc;
                    Message = message;
                    Loading = false;
                    MediaLoading = present;
                    Raise();
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
