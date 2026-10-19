using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Audio;
using Discphew.Disc;

namespace Discphew.UI
{
    sealed class BurnerView : ToolView
    {
        sealed class DataItem { public string Path = ""; public bool Folder; public long Bytes = -1; public int Files; }
        sealed class AudioItem { public string Path = ""; public string Title = "", Artist = ""; public TimeSpan Length; public string Format = ""; public string Error = ""; }

        readonly Segmented mode = new Segmented("Data disc", "Audio CD", "Disc image");
        readonly FlatButton eraseButton = new FlatButton("Erase disc", Icons.Refresh) { Subtle = true };
        readonly Card listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(14)) };
        readonly Panel toolbar = new Panel { Dock = DockStyle.Top, BackColor = Color.Transparent };
        readonly DarkGrid grid = new DarkGrid { Dock = DockStyle.Fill };
        readonly Caption emptyHint = new Caption("", Theme.Heading, Theme.Faint) { TextAlign = ContentAlignment.MiddleCenter };

        readonly FlatButton addFiles = new FlatButton("Add files…", Icons.Plus);
        readonly FlatButton addFolder = new FlatButton("Add folder…", Icons.Folder);
        readonly FlatButton removeButton = new FlatButton("Remove", Icons.Minus) { Subtle = true };
        readonly FlatButton upButton = new FlatButton("", Icons.Up) { Subtle = true };
        readonly FlatButton downButton = new FlatButton("", Icons.Down) { Subtle = true };
        readonly FlatButton clearButton = new FlatButton("Clear", Icons.Close) { Subtle = true };
        readonly TextField labelField = new TextField("Disc name");
        readonly FlatButton chooseImage = new FlatButton("Choose image…", Icons.Disc);

        readonly Card bottom = new Card { Dock = DockStyle.Bottom };
        readonly CapacityBar capacity = new CapacityBar();
        readonly Dropdown speedBox = new Dropdown();
        readonly CheckField closeCheck = new CheckField("Close disc", true);
        readonly CheckField ejectCheck = new CheckField("Eject when done", true);
        readonly FlatButton burnButton = new FlatButton("Burn", Icons.Flame) { Primary = true };
        readonly ProgressStrip progress = new ProgressStrip { Visible = false };
        readonly Caption status = new Caption("", Theme.Small, Theme.Muted);

        readonly List<DataItem> dataItems = new List<DataItem>();
        readonly List<AudioItem> audioItems = new List<AudioItem>();
        string imagePath;
        bool burning;
        char speedsFor;
        int speedsMode = -1;

        public BurnerView(AppState state) : base(state)
        {
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(50), BackColor = Theme.Bg };
            mode.Width = Theme.S(390);
            mode.Location = new Point(0, 0);
            mode.SelectedIndex = Math.Max(0, Math.Min(2, Settings.GetInt("burn.mode", 0)));
            mode.SelectedIndexChanged += (s, e) => { Settings.Set("burn.mode", mode.SelectedIndex); ModeChanged(); };
            eraseButton.Width = eraseButton.PreferredWidth;
            eraseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            eraseButton.Click += (s, e) => Erase();
            top.Controls.Add(mode);
            top.Controls.Add(eraseButton);
            top.Resize += (s, e) => eraseButton.Location = new Point(top.Width - eraseButton.Width, 0);

            toolbar.Height = Theme.S(48);
            foreach (var b in new[] { addFiles, addFolder, removeButton, clearButton, chooseImage }) b.Width = b.PreferredWidth;
            upButton.Width = downButton.Width = Theme.S(34);
            addFiles.Click += (s, e) => AddFilesDialog();
            addFolder.Click += (s, e) => AddFolderDialog();
            removeButton.Click += (s, e) => RemoveSelected();
            clearButton.Click += (s, e) => { dataItems.Clear(); audioItems.Clear(); Refill(); };
            upButton.Click += (s, e) => MoveTrack(-1);
            downButton.Click += (s, e) => MoveTrack(1);
            chooseImage.Click += (s, e) => ChooseImage();
            labelField.Width = Theme.S(220);
            labelField.Text = Settings.Get("burn.label", "");
            labelField.TextChanged += (s, e) => Settings.Set("burn.label", labelField.Text);
            toolbar.Controls.AddRange(new Control[] { addFiles, addFolder, chooseImage, upButton, downButton, removeButton, clearButton, labelField });
            toolbar.Resize += (s, e) => LayoutToolbar();

            grid.MultiSelect = true;
            grid.ReadOnly = true;
            grid.AllowDrop = true;
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            gridHost.Controls.Add(grid);
            gridHost.Controls.Add(emptyHint);
            emptyHint.Dock = DockStyle.Fill;
            emptyHint.AllowDrop = true;
            listCard.Controls.Add(gridHost);
            listCard.Controls.Add(toolbar);
            foreach (var c in new Control[] { grid, emptyHint, listCard })
            {
                c.AllowDrop = true;
                c.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !burning ? DragDropEffects.Copy : DragDropEffects.None;
                c.DragDrop += (s, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
            }

            bottom.Height = Theme.S(150);
            speedBox.Width = Theme.S(150);
            speedBox.Format = o => o is WriteSpeed w ? "Speed: " + w.Label : "Speed: max";
            speedBox.SetItems(new object[] { "max" });
            closeCheck.Checked = Settings.GetBool("burn.close", true);
            ejectCheck.Checked = Settings.GetBool("burn.eject", true);
            closeCheck.CheckedChanged += (s, e) => Settings.Set("burn.close", closeCheck.Checked);
            ejectCheck.CheckedChanged += (s, e) => Settings.Set("burn.eject", ejectCheck.Checked);
            burnButton.Width = Theme.S(150);
            burnButton.Click += (s, e) => { if (burning) Imapi.Cancel(); else Burn(); };
            bottom.Controls.AddRange(new Control[] { capacity, speedBox, closeCheck, ejectCheck, burnButton, progress, status });
            bottom.Resize += (s, e) => LayoutBottom();

            var gap1 = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(16), BackColor = Theme.Bg };
            Controls.Add(listCard);
            Controls.Add(top);
            Controls.Add(gap1);
            Controls.Add(bottom);
            ModeChanged();
        }

        public override string Title => "Burner";

        public override string Subtitle
        {
            get
            {
                var m = State.Disc?.Media;
                if (State.Loading) return "Reading disc…";
                if (m == null) return State.MediaLoading ? "Checking the disc…" : State.Disc == null ? "Insert a blank CD or DVD" : "This drive or disc can't be written by Windows";
                if (m.Writable && m.Blank) return $"Blank {m.Name} · {Fmt.Bytes(m.FreeSectors * 2048)} / {Fmt.Time(TimeSpan.FromSeconds(m.FreeSectors / 75.0))} free";
                if (m.Writable && m.Appendable) return $"{m.Name} with room for another session · {Fmt.Bytes(m.FreeSectors * 2048)} free";
                if (m.Rewritable) return $"{m.Name} is full or closed — erase it to reuse";
                return $"{m.Name} · not writable. Insert a blank disc to burn.";
            }
        }

        // ------------------------------------------------------------------ layout

        void LayoutToolbar()
        {
            int x = 0, y = 0, gap = Theme.S(8);
            foreach (Control c in new Control[] { addFiles, addFolder, chooseImage, upButton, downButton, removeButton, clearButton })
            {
                if (!c.Visible) continue;
                c.Location = new Point(x, y);
                x = c.Right + gap;
            }
            labelField.Location = new Point(toolbar.Width - labelField.Width, y);
        }

        void LayoutBottom()
        {
            int pad = bottom.Padding.Left;
            int w = bottom.Width - pad * 2;
            capacity.SetBounds(pad, pad, w, capacity.Height);
            int y = capacity.Bottom + Theme.S(16);
            speedBox.Location = new Point(pad, y);
            closeCheck.Location = new Point(speedBox.Right + Theme.S(20), y + (speedBox.Height - closeCheck.Height) / 2);
            ejectCheck.Location = new Point(closeCheck.Right + Theme.S(18), closeCheck.Top);
            burnButton.Location = new Point(bottom.Width - pad - burnButton.Width, y);
            status.SetBounds(ejectCheck.Right + Theme.S(18), y, Math.Max(10, burnButton.Left - ejectCheck.Right - Theme.S(30)), burnButton.Height);
            status.TextAlign = ContentAlignment.MiddleRight;
            progress.SetBounds(pad, bottom.Height - pad - progress.Height + Theme.S(6), w, progress.Height);
        }

        // ------------------------------------------------------------------ state

        public override void OnDiscChanged()
        {
            var m = State.Disc?.Media;
            eraseButton.Visible = m != null && m.Rewritable && !burning;
            if (m != null && (State.Drive != speedsFor || speedsMode != mode.SelectedIndex)) LoadSpeeds();
            UpdateCapacity();
        }

        void ModeChanged()
        {
            int m = mode.SelectedIndex;
            addFiles.Visible = m != 2;
            addFolder.Visible = m != 2;
            removeButton.Visible = clearButton.Visible = m != 2;
            upButton.Visible = downButton.Visible = m == 1;
            labelField.Visible = m == 0;
            chooseImage.Visible = m == 2;
            addFiles.Text = m == 1 ? "Add tracks…" : "Add files…";
            addFiles.Width = addFiles.PreferredWidth;
            addFolder.Text = m == 1 ? "Add album folder…" : "Add folder…";
            addFolder.Width = addFolder.PreferredWidth;
            closeCheck.Text = m == 0 ? "Close disc (no more sessions)" : "Close disc";
            closeCheck.Width = TextRenderer.MeasureText(closeCheck.Text, Theme.Body).Width + Theme.S(34);

            grid.Columns.Clear();
            if (m == 0)
            {
                grid.AddText("", "icon", 36);
                grid.AddText("Name", "name", fill: true);
                grid.AddText("Contents", "kind", 140);
                grid.AddText("Size", "size", 100, right: true);
            }
            else if (m == 1)
            {
                grid.AddText("#", "num", 44, right: true);
                grid.AddText("Title", "title", fill: true);
                grid.AddText("Artist", "artist", 200);
                grid.AddText("Source", "fmt", 110);
                grid.AddText("Length", "len", 80, right: true);
            }
            else
            {
                grid.AddText("", "icon", 36);
                grid.AddText("Image", "name", fill: true);
                grid.AddText("Size", "size", 120, right: true);
            }
            grid.CellPainting -= OnCellPainting;
            grid.CellPainting += OnCellPainting;
            emptyHint.Text = m == 0 ? "Drop files and folders here" : m == 1 ? "Drop FLAC, WAV or other audio files here\n(each file becomes a track)" : "Drop an .iso image here";
            LayoutToolbar();
            LayoutBottom();
            LoadSpeeds();
            Refill();
        }

        void LoadSpeeds()
        {
            char letter = State.Drive;
            int m = mode.SelectedIndex;
            speedsFor = letter;
            speedsMode = m;
            if (letter == '\0' || State.Disc?.Media == null || State.Busy) return;
            Task.Run(() => { try { return Imapi.QuerySpeeds(letter, m == 1); } catch { return new List<WriteSpeed>(); } }).ContinueWith(t =>
            {
                if (letter != State.Drive || m != mode.SelectedIndex) return;
                var items = new List<object> { "max" };
                items.AddRange(t.Result);
                int saved = Settings.GetInt("burn.speed", 0);
                int idx = items.FindIndex(o => o is WriteSpeed w && w.SectorsPerSecond == saved);
                speedBox.SetItems(items, Math.Max(0, idx));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Refill()
        {
            grid.Rows.Clear();
            int m = mode.SelectedIndex;
            if (m == 0)
                foreach (var it in dataItems)
                    grid.Rows.Add("", Path.GetFileName(it.Path.TrimEnd('\\')) is string n && n.Length > 0 ? n : it.Path,
                        it.Folder ? (it.Bytes < 0 ? "Folder · counting…" : $"Folder · {it.Files:N0} files") : "File",
                        it.Bytes < 0 ? "…" : Fmt.Bytes(it.Bytes));
            else if (m == 1)
                for (int i = 0; i < audioItems.Count; i++)
                {
                    var a = audioItems[i];
                    grid.Rows.Add((i + 1).ToString(), a.Error.Length > 0 ? $"{a.Title} — {a.Error}" : a.Title, a.Artist, a.Format, a.Length > TimeSpan.Zero ? Fmt.Time(a.Length) : "…");
                }
            else if (imagePath != null)
                grid.Rows.Add("", imagePath, Fmt.Bytes(new FileInfo(imagePath).Length));

            emptyHint.Visible = grid.Rows.Count == 0;
            grid.Visible = grid.Rows.Count > 0;
            UpdateCapacity();
        }

        long NeededSectors(out bool unknown)
        {
            unknown = false;
            switch (mode.SelectedIndex)
            {
                case 0:
                    long sectors = 0;
                    foreach (var it in dataItems)
                    {
                        if (it.Bytes < 0) { unknown = true; continue; }
                        sectors += (it.Bytes + 2047) / 2048 + Math.Max(1, it.Files);    // file rounding + directory records (approx.)
                    }
                    return sectors == 0 ? 0 : sectors + 300 + sectors / 100;           // volume descriptors, path tables, UDF structures
                case 1:
                    double secs = audioItems.Sum(a => Math.Ceiling(a.Length.TotalSeconds * 75) / 75.0 + 2);
                    if (audioItems.Any(a => a.Length == TimeSpan.Zero && a.Error.Length == 0)) unknown = true;
                    return (long)(secs * 75);
                default:
                    return imagePath == null ? 0 : (new FileInfo(imagePath).Length + 2047) / 2048;
            }
        }

        void UpdateCapacity()
        {
            var media = State.Disc?.Media;
            bool known = media != null && media.Writable && media.FreeSectors > 0;
            bool audio = mode.SelectedIndex == 1;
            long cap = known ? media.FreeSectors : audio ? 360_000 : 359_847;
            long need = NeededSectors(out bool unknown);
            capacity.Capacity = cap;
            capacity.Used = 0;
            capacity.Planned = need;
            string disc = known ? (media.Blank ? $"blank {media.Name}" : $"free space on {media.Name}") : audio ? "an 80-minute CD" : "a 700 MB CD";
            capacity.LeftText = audio
                ? $"{Fmt.Time(TimeSpan.FromSeconds(need / 75.0))} of {Fmt.Time(TimeSpan.FromSeconds(cap / 75.0))}{(unknown ? " (measuring…)" : "")}"
                : $"{Fmt.Bytes(need * 2048)} of {Fmt.Bytes(cap * 2048)}{(unknown ? " (counting…)" : "")}";
            capacity.RightText = capacity.Over ? $"Too big for {disc}" : known ? $"on {disc}" : $"assuming {disc}";
            if (!audio && !known && need > cap && need < 2_295_104) capacity.RightText = "Too big for a CD — needs a DVD";
            capacity.Invalidate();
            burnButton.Enabled = burning || (need > 0 && !capacity.Over && !unknown && !State.Busy && (audio ? audioItems.All(a => a.Error.Length == 0) : true));
            (Owner as MainForm)?.UpdateHeader();
        }

        // ------------------------------------------------------------------ adding things

        void AddFilesDialog()
        {
            using var d = new OpenFileDialog { Multiselect = true, Title = mode.SelectedIndex == 1 ? "Add tracks" : "Add files" };
            if (mode.SelectedIndex == 1) d.Filter = "Audio|*.flac;*.wav;*.mp3;*.m4a;*.ogg;*.opus;*.aiff;*.aif;*.wv;*.ape|All files|*.*";
            if (d.ShowDialog(Owner) == DialogResult.OK) AddPaths(d.FileNames);
        }

        void AddFolderDialog()
        {
            var f = Ui.PickFolder(Owner, mode.SelectedIndex == 1 ? "Add every audio file in a folder" : "Add a folder (it keeps its name on the disc)", Settings.Get("burn.lastfolder", ""));
            if (f == null) return;
            Settings.Set("burn.lastfolder", f);
            AddPaths(new[] { f });
        }

        void AddPaths(string[] paths)
        {
            if (burning) return;
            int m = mode.SelectedIndex;
            if (m == 2)
            {
                var iso = paths.FirstOrDefault(p => File.Exists(p) && (p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".img", StringComparison.OrdinalIgnoreCase)));
                if (iso != null) { imagePath = iso; Refill(); }
                else if (paths.Any(p => p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) == false)
                {
                    if (paths.Any(AudioFile.IsAudioFile)) { mode.SelectedIndex = 1; AddPaths(paths); }
                    else { mode.SelectedIndex = 0; AddPaths(paths); }
                }
                return;
            }
            if (m == 0)
            {
                foreach (var p in paths)
                {
                    if (dataItems.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                    var it = new DataItem { Path = p, Folder = Directory.Exists(p) };
                    if (!it.Folder) { it.Bytes = new FileInfo(p).Length; it.Files = 1; }
                    dataItems.Add(it);
                    if (it.Folder)
                        Task.Run(() =>
                        {
                            long bytes = 0; int files = 0;
                            try
                            {
                                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                                {
                                    try { bytes += new FileInfo(f).Length; files++; } catch { }
                                }
                            }
                            catch { }
                            return (bytes, files);
                        }).ContinueWith(t => { it.Bytes = t.Result.bytes; it.Files = t.Result.files; if (mode.SelectedIndex == 0) Refill(); }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                Refill();
                return;
            }

            // Audio: expand folders to their audio files, in name order.
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p).Where(AudioFile.IsAudioFile).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                else if (AudioFile.IsAudioFile(p)) files.Add(p);
            }
            var added = new List<AudioItem>();
            foreach (var f in files)
            {
                var a = new AudioItem { Path = f, Title = Path.GetFileNameWithoutExtension(f), Format = Path.GetExtension(f).TrimStart('.').ToUpperInvariant() };
                if (!Path.GetExtension(f).Equals(".flac", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(f).StartsWith(".wav", StringComparison.OrdinalIgnoreCase) && !Ffmpeg.Available)
                    a.Error = "needs ffmpeg";
                audioItems.Add(a);
                added.Add(a);
            }
            if (files.Count == 0 && paths.Length > 0) status.Text = "No audio files there.";
            Refill();
            Task.Run(() =>
            {
                foreach (var a in added.Where(x => x.Error.Length == 0))
                {
                    try
                    {
                        using var r = AudioFile.Open(a.Path);
                        a.Length = r.TotalFrames > 0 ? TimeSpan.FromSeconds(r.TotalFrames / (double)r.SampleRate) : Ffmpeg.ProbeDuration(a.Path) ?? TimeSpan.Zero;
                        if (r.Tag("TITLE").Length > 0) a.Title = r.Tag("TITLE");
                        a.Artist = r.Tag("ARTIST");
                        a.Format = $"{a.Format} {r.SampleRate / 1000.0:0.#}k/{r.BitsPerSample}";
                        if (a.Length == TimeSpan.Zero) a.Error = "unknown length";
                    }
                    catch (Exception e) { a.Error = e.Message; }
                }
            }).ContinueWith(t => { if (mode.SelectedIndex == 1) Refill(); }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ChooseImage()
        {
            using var d = new OpenFileDialog { Filter = "Disc images|*.iso;*.img|All files|*.*", Title = "Choose a disc image" };
            if (d.ShowDialog(Owner) == DialogResult.OK) { imagePath = d.FileName; Refill(); }
        }

        void RemoveSelected()
        {
            var rows = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderByDescending(i => i).ToList();
            foreach (int i in rows)
            {
                if (mode.SelectedIndex == 0 && i < dataItems.Count) dataItems.RemoveAt(i);
                if (mode.SelectedIndex == 1 && i < audioItems.Count) audioItems.RemoveAt(i);
            }
            if (mode.SelectedIndex == 2) imagePath = null;
            Refill();
        }

        void MoveTrack(int delta)
        {
            if (mode.SelectedIndex != 1 || grid.CurrentRow == null) return;
            int i = grid.CurrentRow.Index, j = i + delta;
            if (j < 0 || j >= audioItems.Count) return;
            (audioItems[i], audioItems[j]) = (audioItems[j], audioItems[i]);
            Refill();
            grid.CurrentCell = grid.Rows[j].Cells[1];
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "icon") return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            float s = Theme.S(16f);
            var r = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - s) / 2, e.CellBounds.Y + (e.CellBounds.Height - s) / 2, s, s);
            Theme.Hq(e.Graphics);
            if (mode.SelectedIndex == 2) Icons.Disc(e.Graphics, r, Theme.Accent);
            else if (e.RowIndex < dataItems.Count && dataItems[e.RowIndex].Folder) Icons.Folder(e.Graphics, r, Theme.Warning);
            else Icons.File(e.Graphics, r, Theme.Muted);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ burning

        void SetBurning(bool on)
        {
            burning = on;
            mode.Enabled = !on;
            foreach (Control c in toolbar.Controls) c.Enabled = !on;
            speedBox.Enabled = closeCheck.Enabled = ejectCheck.Enabled = !on;
            burnButton.Text = on ? "Cancel" : "Burn";
            burnButton.Icon = on ? (Icons.Painter)Icons.Close : Icons.Flame;
            burnButton.Primary = !on;
            progress.Visible = on;
            eraseButton.Visible = !on && State.Disc?.Media?.Rewritable == true;
            burnButton.Invalidate();
        }

        void Burn()
        {
            if (State.Busy || State.Drive == '\0') return;
            var media = State.Disc?.Media;
            if (media != null && !media.Writable)
            {
                Dialogs.Error(Owner, "Not a writable disc", media.Rewritable ? $"This {media.Name} is full or closed. Erase it first, or insert a blank disc." : $"The disc in {State.Drive}: is a {media.Name}, which can't be written. Insert a blank CD-R/RW or DVD±R/RW.");
                return;
            }
            int m = mode.SelectedIndex;
            if (m == 1 && media != null && media.IsDvd) { Dialogs.Error(Owner, "Audio CDs need a CD", "Insert a blank CD-R (or CD-RW). Audio discs can't be burned to DVD."); return; }

            var opts = new BurnOptions
            {
                Drive = State.Drive,
                Speed = speedBox.SelectedItem is WriteSpeed ws ? ws.SectorsPerSecond : 0,
                CloseDisc = closeCheck.Checked,
                EjectWhenDone = ejectCheck.Checked,
            };
            Settings.Set("burn.speed", opts.Speed);
            string what = m == 0 ? $"{dataItems.Count} item{(dataItems.Count == 1 ? "" : "s")}" : m == 1 ? $"{audioItems.Count} track{(audioItems.Count == 1 ? "" : "s")}" : Path.GetFileName(imagePath);
            if (!Dialogs.Confirm(Owner, "Ready to burn", $"Write {what} to the disc in {State.Drive}:?\n\nDon't eject the disc or put the PC to sleep until it's done.", "Burn"))
                return;

            var unlock = State.Lock("Burning — keep the disc in the drive", reloadAfter: true);
            SetBurning(true);
            progress.Value = -1;
            var lastUi = DateTime.MinValue;
            opts.Progress = p =>
            {
                if ((DateTime.Now - lastUi).TotalMilliseconds < 150 && p.Fraction < 1) return;
                lastUi = DateTime.Now;
                BeginInvoke(new Action(() =>
                {
                    progress.Value = p.Fraction;
                    status.Text = p.Phase + (p.Fraction >= 0 && p.Fraction < 1 ? $" · {p.Fraction * 100:0}%" : "") + (p.RemainingSeconds > 0 ? $" · {Fmt.Time(TimeSpan.FromSeconds(p.RemainingSeconds))} left" : "");
                }));
            };

            var paths = dataItems.Select(x => x.Path).ToList();
            var tracks = audioItems.Select(x => x.Path).ToList();
            string label = labelField.Text;
            string image = imagePath;
            string temp = null;

            Task.Run(() =>
            {
                switch (m)
                {
                    case 0: Imapi.BurnData(opts, label, paths); break;
                    case 1:
                        temp = Path.Combine(Path.GetTempPath(), "discphew-burn-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temp);
                        var pcm = PreparePcm(tracks, temp, opts.Progress);
                        Imapi.BurnAudio(opts, pcm);
                        break;
                    default: Imapi.BurnImage(opts, image); break;
                }
            }).ContinueWith(t =>
            {
                if (temp != null) try { Directory.Delete(temp, true); } catch { }
                SetBurning(false);
                unlock.Dispose();
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    status.Text = ex is OperationCanceledException ? "Cancelled" : "Burn failed";
                    if (!(ex is OperationCanceledException)) Dialogs.Error(Owner, "Burn failed", ex.Message);
                    return;
                }
                status.Text = "Burned successfully";
                Dialogs.Info(Owner, "Done", m == 1 ? "Your audio CD is ready." : "The disc has been burned.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Decodes each track to raw CD audio (44.1 kHz, 16-bit, stereo), padded to whole sectors.</summary>
        static List<string> PreparePcm(List<string> files, string folder, Action<BurnProgress> progress)
        {
            var result = new List<string>();
            var buf = new byte[2352 * 32];
            for (int i = 0; i < files.Count; i++)
            {
                progress?.Invoke(new BurnProgress { Phase = $"Preparing track {i + 1} of {files.Count}", Fraction = i / (double)files.Count });
                string outPath = Path.Combine(folder, $"{i + 1:00}.pcm");
                using (var reader = AudioFile.Open(files[i]))
                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var pcm = new CdPcmStream(reader);
                    long total = 0;
                    int n;
                    while ((n = pcm.Read(buf, 0, buf.Length)) > 0) { fs.Write(buf, 0, n); total += n; }
                    long pad = (2352 - total % 2352) % 2352;
                    if (total < 2352 * 75 * 4) pad += 2352 * 75 * 4 - total;      // tracks must be at least 4 seconds
                    if (pad > 0) fs.Write(new byte[pad], 0, (int)pad);
                }
                result.Add(outPath);
            }
            return result;
        }

        void Erase()
        {
            if (State.Busy) return;
            var m = State.Disc?.Media;
            if (!Dialogs.Confirm(Owner, "Erase this disc?", $"Everything on the {m?.Name ?? "disc"} in {State.Drive}: will be gone. A quick erase takes a minute or two.", "Erase", true))
                return;
            char letter = State.Drive;
            var unlock = State.Lock("Erasing the disc…", reloadAfter: true);
            SetBurning(true);
            burnButton.Enabled = false;
            progress.Value = -1;
            Task.Run(() => Imapi.Erase(letter, false, p => BeginInvoke(new Action(() =>
            {
                progress.Value = p.Fraction;
                status.Text = p.Phase + (p.RemainingSeconds > 0 ? $" · about {Fmt.Time(TimeSpan.FromSeconds(p.RemainingSeconds))} left" : "");
            })))).ContinueWith(t =>
            {
                SetBurning(false);
                unlock.Dispose();
                if (t.IsFaulted) { status.Text = "Erase failed"; Dialogs.Error(Owner, "Erase failed", t.Exception.GetBaseException().Message); }
                else status.Text = "Erased — ready to burn";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
