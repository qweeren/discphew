using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Audio;
using Discphew.Disc;

namespace Discphew.UI
{
    sealed class RipperView : ToolView
    {
        readonly Card metaCard = new Card { Dock = DockStyle.Top };
        readonly CoverBox cover = new CoverBox { Caption = "Click to set cover", Cursor = Cursors.Hand };
        readonly TextField artistField = new TextField("Album artist");
        readonly TextField albumField = new TextField("Album title");
        readonly TextField yearField = new TextField("Year");
        readonly TextField genreField = new TextField("Genre");
        readonly FlatButton lookupButton = new FlatButton("Look up online", Icons.Search);
        readonly Dropdown releaseBox = new Dropdown();
        readonly Caption lookupStatus = new Caption("", Theme.Small, Theme.Faint);
        Panel artistWrap, albumWrap, yearWrap, genreWrap;

        readonly Card tracksCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(6)) };
        readonly DarkGrid grid = new DarkGrid { Dock = DockStyle.Fill };
        readonly Caption emptyHint = new Caption("", Theme.Heading, Theme.Faint) { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };

        readonly Card dataCard = new Card { Dock = DockStyle.Bottom };
        readonly Caption dataTitle = new Caption("Data track", Theme.Strong, Theme.Text);
        readonly Caption dataInfo = new Caption("", Theme.Small, Theme.Muted);
        readonly FlatButton isoButton = new FlatButton("Save ISO image…", Icons.Disc);
        readonly FlatButton copyButton = new FlatButton("Copy files…", Icons.Folder);
        readonly Dropdown videoBox = new Dropdown();

        readonly Card outputCard = new Card { Dock = DockStyle.Bottom };
        readonly TextField folderField = new TextField("Output folder");
        readonly FlatButton browseButton = new FlatButton("", Icons.Folder);
        readonly Dropdown formatBox = new Dropdown();
        readonly TextField templateField = new TextField(RipOptions.DefaultTemplate);
        readonly CheckField verifyCheck = new CheckField("Verify (read twice)", true);
        readonly CheckField coverCheck = new CheckField("Save cover.jpg", true);
        readonly CheckField logCheck = new CheckField("Cue sheet + log", true);
        readonly TextField offsetField = new TextField("not set");
        readonly FlatButton detectButton = new FlatButton("Detect");
        readonly FlatButton ripButton = new FlatButton("Rip tracks", Icons.Rip) { Primary = true };
        readonly ProgressStrip progress = new ProgressStrip { Visible = false };
        readonly Caption status = new Caption("", Theme.Small, Theme.Muted);
        Panel folderWrap, formatWrap, templateWrap, offsetWrap;
        string offsetKey;

        readonly Panel spacerTop = new Panel { Dock = DockStyle.Top, Height = Theme.S(14), BackColor = Theme.Bg };
        readonly Panel spacerBottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(14), BackColor = Theme.Bg };
        readonly Panel spacerData = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(14), BackColor = Theme.Bg };

        DiscInfo shown;
        List<ReleaseMatch> releases = new List<ReleaseMatch>();
        byte[] coverBytes;
        CancellationTokenSource job;
        bool filling;

        public RipperView(AppState state) : base(state)
        {
            // ---- metadata
            artistWrap = Ui.Labeled("Album artist", artistField, 200);
            albumWrap = Ui.Labeled("Album", albumField, 200);
            yearWrap = Ui.Labeled("Year", yearField, Theme.S(90));
            genreWrap = Ui.Labeled("Genre", genreField, Theme.S(160));
            releaseBox.Format = o => o?.ToString() ?? "";
            releaseBox.SelectedIndexChanged += (s, e) => { if (!filling && releaseBox.SelectedItem is ReleaseMatch m) ApplyRelease(m); };
            releaseBox.Visible = false;
            lookupButton.Width = lookupButton.PreferredWidth;
            lookupButton.Click += (s, e) => Lookup();
            cover.Click += (s, e) => PickCover();
            metaCard.Controls.AddRange(new Control[] { cover, artistWrap, albumWrap, yearWrap, genreWrap, lookupButton, releaseBox, lookupStatus });
            metaCard.Resize += (s, e) => LayoutMeta();
            metaCard.Height = Theme.S(164);
            artistField.TextChanged += (s, e) => { if (!filling && shown != null) shown.AlbumArtist = artistField.Text; };
            albumField.TextChanged += (s, e) => { if (!filling && shown != null) shown.AlbumTitle = albumField.Text; };
            yearField.TextChanged += (s, e) => { if (!filling && shown != null) shown.Year = yearField.Text; };
            genreField.TextChanged += (s, e) => { if (!filling && shown != null) shown.Genre = genreField.Text; };

            // ---- tracks
            grid.AddText("", "pick", 40);
            grid.AddText("#", "num", 44, right: true);
            grid.AddText("Title", "title", fill: true, readOnly: false);
            grid.AddText("Artist", "artist", 200, readOnly: false);
            grid.AddText("Length", "len", 76, right: true);
            grid.AddText("Status", "status", 190);
            grid.Columns["status"].DefaultCellStyle.ForeColor = Theme.Muted;
            grid.CellPainting += OnCellPainting;
            grid.CellClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0 && job == null)
                {
                    var row = grid.Rows[e.RowIndex];
                    row.Cells[0].Value = !(row.Cells[0].Value is bool b && b);
                    UpdateRipButton();
                }
                else if (e.RowIndex == -1 && e.ColumnIndex == 0 && job == null)
                {
                    bool all = grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[0].Value is bool b && b);
                    foreach (DataGridViewRow r in grid.Rows) r.Cells[0].Value = !all;
                    UpdateRipButton();
                }
            };
            grid.CellValueChanged += (s, e) =>
            {
                if (filling || e.RowIndex < 0 || !(grid.Rows[e.RowIndex].Tag is TrackInfo t)) return;
                var v = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
                if (grid.Columns[e.ColumnIndex].Name == "title") t.Title = v;
                if (grid.Columns[e.ColumnIndex].Name == "artist") t.Artist = v;
            };
            tracksCard.Controls.Add(grid);
            tracksCard.Controls.Add(emptyHint);

            // ---- data track
            dataCard.Height = Theme.S(84);
            isoButton.Width = isoButton.PreferredWidth;
            copyButton.Width = copyButton.PreferredWidth;
            isoButton.Click += (s, e) => SaveIso();
            copyButton.Click += (s, e) => CopyFiles();
            videoBox.SetItems(new object[] { "Copy video as-is", "Convert video to MP4", "Convert video to MOV", "Remux video to MKV" });
            videoBox.Width = Theme.S(210);
            dataCard.Controls.AddRange(new Control[] { dataTitle, dataInfo, isoButton, copyButton, videoBox });
            dataCard.Resize += (s, e) => LayoutData();

            // ---- output
            folderField.Text = Settings.Get("rip.folder", Ui.DefaultMusicFolder);
            templateField.Text = Settings.Get("rip.template", RipOptions.DefaultTemplate);
            verifyCheck.Checked = Settings.GetBool("rip.verify", true);
            coverCheck.Checked = Settings.GetBool("rip.cover", true);
            logCheck.Checked = Settings.GetBool("rip.log", true);
            folderWrap = Ui.Labeled("Save to", folderField, 300);
            templateWrap = Ui.Labeled("File names  ·  {albumartist} {artist} {album} {year} {track} {title} {genre}", templateField, 300);
            formatBox.Format = o => o is RipFormat f ? f.Name + (f.NeedsFfmpeg && !Ffmpeg.Available ? "   (needs ffmpeg)" : "") : "";
            formatBox.IsEnabled = o => o is RipFormat f && (!f.NeedsFfmpeg || Ffmpeg.Available);
            formatBox.SetItems(RipFormat.All, Math.Max(0, Math.Min(RipFormat.All.Length - 1, Settings.GetInt("rip.format", 0))));
            if (((RipFormat)formatBox.SelectedItem).NeedsFfmpeg && !Ffmpeg.Available) formatBox.SetItems(RipFormat.All, 0);
            formatWrap = Ui.Labeled("Format", formatBox, Theme.S(250));
            browseButton.Width = Theme.S(40);
            browseButton.Click += (s, e) =>
            {
                var f = Ui.PickFolder(Owner, "Where should ripped albums go?", folderField.Text);
                if (f != null) folderField.Text = f;
            };
            offsetWrap = Ui.Labeled("Read offset (samples)", offsetField, Theme.S(150));
            offsetField.TextChanged += (s, e) =>
            {
                if (filling || offsetKey == null) return;
                string v = offsetField.Text.Trim();
                if (v.Length == 0) DriveOffsets.Set(offsetKey, null);
                else if (int.TryParse(v, out int n)) DriveOffsets.Set(offsetKey, n);
            };
            detectButton.Width = detectButton.PreferredWidth;
            detectButton.Click += (s, e) => DetectOffset();
            ripButton.Click += (s, e) => { if (job != null) job.Cancel(); else RipAudio(); };
            outputCard.Controls.AddRange(new Control[] { folderWrap, browseButton, formatWrap, templateWrap, offsetWrap, detectButton, verifyCheck, coverCheck, logCheck, ripButton, progress, status });
            outputCard.Resize += (s, e) => LayoutOutput();
            outputCard.Height = Theme.S(212);

            Controls.Add(tracksCard);
            Controls.Add(spacerTop);
            Controls.Add(metaCard);
            Controls.Add(spacerData);
            Controls.Add(dataCard);
            Controls.Add(spacerBottom);
            Controls.Add(outputCard);
        }

        public override string Title => "Ripper";

        public override string Subtitle
        {
            get
            {
                var d = State.Disc;
                if (d == null) return State.Loading ? "Reading disc…" : "Insert an audio CD to rip it to FLAC, WAV or (with ffmpeg) MP3, AAC, Opus…";
                if (d.HasAudio) return $"{d.AudioTracks.Count()} tracks · {Fmt.Time(d.AudioDuration)}" + (d.HasCdText ? " · CD-Text found" : "");
                if (d.HasData) return "Copy the files or save an exact ISO image of the disc";
                return State.Message;
            }
        }

        // ------------------------------------------------------------------ layout

        void LayoutMeta()
        {
            int pad = metaCard.Padding.Left, gap = Theme.S(12);
            int size = metaCard.Height - pad * 2;
            cover.SetBounds(pad, pad, size, size);
            int x = cover.Right + Theme.S(20), w = metaCard.Width - x - pad;
            int half = (w - gap) / 2;
            artistWrap.SetBounds(x, pad, half, artistWrap.Height);
            albumWrap.SetBounds(x + half + gap, pad, w - half - gap, albumWrap.Height);
            int y2 = artistWrap.Bottom + Theme.S(12);
            yearWrap.SetBounds(x, y2, Theme.S(90), yearWrap.Height);
            genreWrap.SetBounds(yearWrap.Right + gap, y2, Theme.S(170), genreWrap.Height);
            int by = y2 + yearWrap.Height - lookupButton.Height;
            lookupButton.Location = new Point(genreWrap.Right + gap, by);
            releaseBox.SetBounds(lookupButton.Right + gap, by, Math.Max(Theme.S(120), metaCard.Width - pad - lookupButton.Right - gap), lookupButton.Height);
            lookupStatus.SetBounds(lookupButton.Right + gap, by, Math.Max(10, metaCard.Width - pad - lookupButton.Right - gap), lookupButton.Height);
        }

        void LayoutData()
        {
            int pad = dataCard.Padding.Left;
            int bx = dataCard.Width - pad;
            copyButton.Location = new Point(bx - copyButton.Width, (dataCard.Height - copyButton.Height) / 2);
            videoBox.Location = new Point(copyButton.Left - Theme.S(8) - videoBox.Width, copyButton.Top);
            int left = videoBox.Visible ? videoBox.Left : copyButton.Left;
            isoButton.Location = new Point(left - Theme.S(8) - isoButton.Width, copyButton.Top);
            int tw = Math.Max(10, isoButton.Left - pad - Theme.S(12));
            dataTitle.SetBounds(pad, dataCard.Height / 2 - dataTitle.Height, tw, dataTitle.Height);
            dataInfo.SetBounds(pad, dataCard.Height / 2, tw, dataInfo.Height);
        }

        void LayoutOutput()
        {
            int pad = outputCard.Padding.Left, gap = Theme.S(12);
            int w = outputCard.Width - pad * 2;
            formatWrap.SetBounds(outputCard.Width - pad - Theme.S(250), pad, Theme.S(250), formatWrap.Height);
            browseButton.SetBounds(formatWrap.Left - gap - browseButton.Width, pad + formatWrap.Height - folderField.Height, browseButton.Width, folderField.Height);
            folderWrap.SetBounds(pad, pad, browseButton.Left - Theme.S(6) - pad, folderWrap.Height);
            int y2 = folderWrap.Bottom + Theme.S(10);
            detectButton.SetBounds(outputCard.Width - pad - detectButton.Width, y2 + offsetWrap.Height - offsetField.Height, detectButton.Width, offsetField.Height);
            offsetWrap.SetBounds(detectButton.Left - Theme.S(6) - offsetWrap.Width, y2, offsetWrap.Width, offsetWrap.Height);
            templateWrap.SetBounds(pad, y2, Math.Max(Theme.S(100), offsetWrap.Left - gap - pad), templateWrap.Height);
            int y3 = templateWrap.Bottom + Theme.S(14);
            verifyCheck.Location = new Point(pad, y3 + (ripButton.Height - verifyCheck.Height) / 2);
            coverCheck.Location = new Point(verifyCheck.Right + Theme.S(18), verifyCheck.Top);
            logCheck.Location = new Point(coverCheck.Right + Theme.S(18), verifyCheck.Top);
            ripButton.Width = Math.Max(Theme.S(150), ripButton.PreferredWidth);
            ripButton.Location = new Point(outputCard.Width - pad - ripButton.Width, y3);
            status.SetBounds(logCheck.Right + Theme.S(18), y3, Math.Max(10, ripButton.Left - logCheck.Right - Theme.S(30)), ripButton.Height);
            status.TextAlign = ContentAlignment.MiddleRight;
            progress.SetBounds(pad, outputCard.Height - pad - progress.Height + Theme.S(6), w, progress.Height);
        }

        // ------------------------------------------------------------------ disc

        public override void OnDiscChanged()
        {
            var d = State.Disc;
            if (job != null) { UpdateRipButton(); return; }
            bool hasAudio = d != null && d.HasAudio;
            bool hasData = d != null && d.HasData;
            metaCard.Visible = spacerTop.Visible = hasAudio;
            outputCard.Visible = spacerBottom.Visible = hasAudio;
            tracksCard.Visible = true;
            dataCard.Visible = spacerData.Visible = hasData;
            dataCard.Dock = spacerData.Dock = hasAudio ? DockStyle.Bottom : DockStyle.Top;
            emptyHint.Visible = !hasAudio;
            grid.Visible = hasAudio;
            emptyHint.Text = State.Loading ? "Reading disc…" : d == null ? "Insert an audio CD to rip it" : hasData ? "This is a data disc — copy its files or save an ISO image above" : "There's no audio on this disc";
            if (hasData)
            {
                var fs = d.FileSystem;
                dataInfo.Text = fs != null ? $"{fs.FileCount:N0} files · {Fmt.Bytes(fs.TotalFileBytes)} · {fs.Kind}" + (fs.VolumeLabel.Length > 0 ? $" · “{fs.VolumeLabel}”" : "") : "Unknown file system";
                dataTitle.Text = hasAudio ? "Data track" : "Data disc";
                videoBox.Visible = Ffmpeg.Available;
                LayoutData();
            }

            if (!ReferenceEquals(d, shown))
            {
                if (shown != null && shown.HasAudio) MetadataCache.Save(shown);
                shown = d;
                releases = new List<ReleaseMatch>();
                releaseBox.Visible = false;
                lookupStatus.Text = "";
                coverBytes = null;
                FillFields();
                if (d != null && d.HasAudio && File.Exists(d.CoverPath))
                {
                    cover.LoadFile(d.CoverPath);
                    try { coverBytes = File.ReadAllBytes(d.CoverPath); } catch { }
                }
                else cover.Image = null;
                if (d != null && d.HasAudio && d.AlbumTitle.Length == 0 && Settings.GetBool("rip.autolookup", true)) Lookup();
            }
            ShowOffset();
            UpdateRipButton();
        }

        /// <summary>Shows the read offset remembered for the current drive once its name is known.</summary>
        void ShowOffset()
        {
            string key = DriveOffsets.Key(State.Disc, State.MediaLoading);
            if (key == offsetKey) return;
            offsetKey = key;
            filling = true;
            int? v = DriveOffsets.Get(key);
            offsetField.Text = v.HasValue ? DriveOffsets.Format(v.Value) : "";
            filling = false;
        }

        void FillFields()
        {
            filling = true;
            var d = shown;
            artistField.Text = d?.AlbumArtist ?? "";
            albumField.Text = d?.AlbumTitle ?? "";
            yearField.Text = d?.Year ?? "";
            genreField.Text = d?.Genre ?? "";
            grid.Rows.Clear();
            if (d != null)
                foreach (var t in d.AudioTracks)
                {
                    int i = grid.Rows.Add(true, t.Number.ToString(), t.Title, t.Artist, Fmt.Time(t.Duration), t.PreEmphasis ? "Pre-emphasis" : "");
                    grid.Rows[i].Tag = t;
                }
            filling = false;
        }

        void UpdateRipButton()
        {
            int n = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells[0].Value is bool b && b);
            ripButton.Text = job != null ? "Cancel" : n == grid.Rows.Count ? "Rip all tracks" : n == 1 ? "Rip 1 track" : $"Rip {n} tracks";
            ripButton.Primary = job == null;
            ripButton.Enabled = job != null || (n > 0 && !State.Busy);
            isoButton.Enabled = copyButton.Enabled = job == null && !State.Busy;
            lookupButton.Enabled = job == null;
            detectButton.Enabled = job == null && !State.Busy && offsetKey != null && shown != null && shown.HasAudio;
            offsetField.ReadOnly = job != null || offsetKey == null;
            LayoutOutput();
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0) return;
            e.PaintBackground(e.CellBounds, e.RowIndex >= 0 && (e.State & DataGridViewElementStates.Selected) != 0);
            bool on = e.RowIndex >= 0 ? grid.Rows[e.RowIndex].Cells[0].Value is bool b && b : grid.Rows.Count > 0 && grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[0].Value is bool bb && bb);
            float s = Theme.S(16f);
            var box = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - s) / 2, e.CellBounds.Y + (e.CellBounds.Height - s) / 2, s, s);
            Theme.Hq(e.Graphics);
            if (on) { Theme.FillRound(e.Graphics, Theme.Accent, box, Theme.S(4f)); Icons.Check(e.Graphics, box, Theme.AccentText); }
            else Theme.DrawRound(e.Graphics, Theme.Faint, box, Theme.S(4f), 1.2f);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ metadata

        void Lookup()
        {
            var d = shown;
            if (d == null || !d.HasAudio) return;
            lookupButton.Enabled = false;
            lookupStatus.Text = "Searching MusicBrainz…";
            releaseBox.Visible = false;
            Task.Run(() =>
            {
                try { return (MusicBrainz.Lookup(d), (string)null); }
                catch (Exception e) { return (new List<ReleaseMatch>(), e.Message); }
            }).ContinueWith(t =>
            {
                lookupButton.Enabled = job == null;
                if (!ReferenceEquals(d, shown)) return;
                var (list, error) = t.Result;
                releases = list;
                if (error != null) { lookupStatus.Text = "Lookup failed: " + error; return; }
                if (list.Count == 0) { lookupStatus.Text = "Not found on MusicBrainz — fill in the details by hand."; return; }
                lookupStatus.Text = "";
                filling = true;
                releaseBox.SetItems(list, 0);
                filling = false;
                releaseBox.Visible = true;
                ApplyRelease(list[0]);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ApplyRelease(ReleaseMatch m)
        {
            var d = shown;
            if (d == null) return;
            MusicBrainz.Apply(d, m);
            FillFields();
            MetadataCache.Save(d);
            State.Touch();
            Task.Run(() => MusicBrainz.FetchCover(m)).ContinueWith(t =>
            {
                if (!ReferenceEquals(d, shown) || t.Result == null) return;
                coverBytes = t.Result;
                cover.LoadBytes(coverBytes);
                MetadataCache.SaveCover(d, coverBytes);
                State.Touch();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void PickCover()
        {
            if (shown == null || !shown.HasAudio) return;
            using var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png|All files|*.*", Title = "Choose cover art" };
            if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
            try
            {
                coverBytes = File.ReadAllBytes(dlg.FileName);
                cover.LoadBytes(coverBytes);
                MetadataCache.SaveCover(shown, coverBytes);
                State.Touch();
            }
            catch (Exception ex) { Dialogs.Error(Owner, "Couldn't load that image", ex.Message); }
        }

        // ------------------------------------------------------------------ jobs

        void SaveSettings()
        {
            Settings.Set("rip.folder", folderField.Text);
            Settings.Set("rip.template", templateField.Text);
            Settings.Set("rip.format", formatBox.SelectedIndex);
            Settings.Set("rip.verify", verifyCheck.Checked);
            Settings.Set("rip.cover", coverCheck.Checked);
            Settings.Set("rip.log", logCheck.Checked);
        }

        void RipAudio()
        {
            var d = shown;
            if (d == null || State.Busy) return;
            var format = (RipFormat)formatBox.SelectedItem;
            if (format.NeedsFfmpeg && !Ffmpeg.Available) { Dialogs.Error(Owner, "ffmpeg not found", "This format is encoded with ffmpeg. Install it (for example: winget install ffmpeg) or pick FLAC or WAV."); return; }
            if (folderField.Text.Trim().Length == 0) { Dialogs.Error(Owner, "Choose a folder", "Pick where the ripped files should go."); return; }
            int? readOffset = null;
            if (offsetField.Text.Trim().Length > 0)
            {
                if (!int.TryParse(offsetField.Text.Trim(), out int v) || Math.Abs(v) > 50000)
                {
                    Dialogs.Error(Owner, "Check the read offset", "Enter the drive's read offset in samples (for example +6 or -472), leave it empty, or press Detect with a well-known CD in the drive.");
                    return;
                }
                readOffset = v;
            }
            grid.EndEdit();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is bool b && b).ToList();
            var tracks = rows.Select(r => (TrackInfo)r.Tag).ToList();
            if (tracks.Count == 0) return;
            SaveSettings();
            MetadataCache.Save(d);

            var o = new RipOptions
            {
                Drive = d.Drive,
                Folder = folderField.Text.Trim(),
                Template = templateField.Text.Trim().Length > 0 ? templateField.Text.Trim() : RipOptions.DefaultTemplate,
                Format = format,
                Verify = verifyCheck.Checked,
                SaveCoverFile = coverCheck.Checked,
                WriteCueAndLog = logCheck.Checked,
                AccurateRip = Settings.GetBool("rip.accuraterip", true),
                ReadOffset = readOffset,
                Cover = coverBytes,
            };
            var existing = tracks.Select(t => AudioRipper.BuildPath(d, t, o)).Where(File.Exists).ToList();
            if (existing.Count > 0 && !Dialogs.Confirm(Owner, "Replace existing files?", $"{existing.Count} of these files already exist, starting with:\n{existing[0]}", "Replace"))
                return;

            foreach (var r in rows) r.Cells["status"].Value = "Waiting";
            job = new CancellationTokenSource();
            var token = job.Token;
            var unlock = State.Lock($"Ripping {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}…", reloadAfter: false);
            progress.Visible = true;
            progress.Value = 0;
            grid.ReadOnly = true;
            UpdateRipButton();
            var started = DateTime.Now;
            long totalSectors = tracks.Sum(t => (long)t.Sectors);
            var lastUi = DateTime.MinValue;

            Task.Run(() => AudioRipper.Rip(d, tracks, o, (i, f, s) =>
            {
                if ((DateTime.Now - lastUi).TotalMilliseconds < 120 && f < 1) return;
                lastUi = DateTime.Now;
                BeginInvoke(new Action(() =>
                {
                    rows[i].Cells["status"].Value = f >= 1 ? s : $"{f * 100:0}% · {s}";
                    long before = tracks.Take(i).Sum(t => (long)t.Sectors);
                    double overall = (before + tracks[i].Sectors * f) / totalSectors;
                    progress.Value = overall;
                    status.Text = $"Track {i + 1} of {tracks.Count} · {overall * 100:0}%";
                }));
            }, () => token.IsCancellationRequested)).ContinueWith(t =>
            {
                job = null;
                progress.Visible = false;
                grid.ReadOnly = false;
                grid.Columns["num"].ReadOnly = grid.Columns["len"].ReadOnly = grid.Columns["status"].ReadOnly = true;
                unlock.Dispose();
                UpdateRipButton();
                if (t.IsFaulted)
                {
                    status.Text = "Rip failed";
                    Dialogs.Error(Owner, "Rip failed", t.Exception.GetBaseException().Message);
                    return;
                }
                var results = t.Result;
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    rows[i].Cells["status"].Value = TrackStatus(r);
                }
                int good = results.Count(r => r.Ok && r.ReadErrors == 0 && r.Unverified == 0);
                int accurate = results.Count(r => r.Ar == ArState.Match);
                bool inDatabase = results.Any(r => r.Ar == ArState.Match || r.Ar == ArState.Mismatch);
                var took = DateTime.Now - started;
                status.Text = token.IsCancellationRequested ? "Cancelled"
                    : $"Done in {Fmt.Time(took)} · {good} of {tracks.Count} clean" + (inDatabase ? $" · {accurate} accurate" : "");
                if (!token.IsCancellationRequested && results.Count > 0 && results.Any(r => r.Ok))
                {
                    string folder = Path.GetDirectoryName(results.First(r => r.Ok).Path);
                    string summary = good == tracks.Count
                        ? $"All {tracks.Count} tracks were read consistently{(o.Verify ? " on both passes" : "")}."
                        : $"{good} of {tracks.Count} tracks are clean. The rest had read problems; check the status column and rip.log.";
                    if (accurate == tracks.Count)
                        summary += tracks.Count == 1 ? " AccurateRip confirms it matches other people's rips." : " AccurateRip confirms every track matches other people's rips.";
                    else if (inDatabase)
                        summary += $" {accurate} of {tracks.Count} match AccurateRip." + (!o.ReadOffset.HasValue && accurate == 0 ? " Set the drive's read offset (or press Detect) and rip again to check." : "");
                    else if (results.Any(r => r.Ar == ArState.NotInDatabase))
                        summary += " This disc isn't in AccurateRip yet.";
                    if (Dialogs.Confirm(Owner, "Rip finished", summary + "\n\n" + folder, "Open folder"))
                        Ui.OpenFolder(folder);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string TrackStatus(TrackResult r)
        {
            if (!r.Ok) return r.Error;
            if (r.Ar == ArState.Match) return $"✓ AccurateRip (conf {r.ArConfidence})";
            if (r.ReadErrors > 0) return $"⚠ {r.ReadErrors} bad sectors";
            if (r.Unverified > 0) return $"⚠ {r.Unverified} unverified";
            if (r.Ar == ArState.Mismatch) return "✗ AccurateRip mismatch";
            return $"✓ {r.Crc:X8}" + (r.Ar == ArState.NotInDatabase ? " · not in AR" : "");
        }

        void DetectOffset()
        {
            var d = shown;
            string key = offsetKey;
            if (d == null || !d.HasAudio || key == null || State.Busy || job != null) return;
            job = new CancellationTokenSource();
            var token = job.Token;
            var unlock = State.Lock("Detecting the drive's read offset…", reloadAfter: false);
            progress.Visible = true;
            progress.Value = 0;
            UpdateRipButton();
            var lastUi = DateTime.MinValue;

            Task.Run(() => AudioRipper.DetectOffset(d, (f, s) =>
            {
                if ((DateTime.Now - lastUi).TotalMilliseconds < 120 && f < 1) return;
                lastUi = DateTime.Now;
                BeginInvoke(new Action(() => { progress.Value = f; status.Text = $"{s} · {f * 100:0}%"; }));
            }, () => token.IsCancellationRequested)).ContinueWith(t =>
            {
                job = null;
                progress.Visible = false;
                unlock.Dispose();
                UpdateRipButton();
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    status.Text = ex is OperationCanceledException ? "Cancelled" : "Offset not found";
                    if (!(ex is OperationCanceledException)) Dialogs.Error(Owner, "Couldn't detect the read offset", ex.Message);
                    return;
                }
                var m = t.Result;
                string value = DriveOffsets.Format(m.Offset);
                status.Text = $"Read offset {value}";
                string how = m.TracksMatched == m.TracksChecked
                    ? (m.TracksChecked == 1 ? "The track checked matches" : $"All {m.TracksChecked} tracks checked match")
                    : $"{m.TracksMatched} of {m.TracksChecked} tracks checked match";
                string text = $"{how} AccurateRip at {value} samples (confidence {m.Confidence}).\n\n"
                    + "A different pressing of the same album can shift this, so if you're unsure, check it with a second disc.";
                if (Dialogs.Confirm(Owner, "Read offset found", text, $"Use {value}"))
                {
                    if (key == offsetKey) offsetField.Text = value;
                    else DriveOffsets.Set(key, m.Offset);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void RunDataJob(string busyText, Func<Action<double, string>, Func<bool>, string> work)
        {
            if (State.Busy) return;
            job = new CancellationTokenSource();
            var token = job.Token;
            var unlock = State.Lock(busyText, reloadAfter: false);
            var cancel = new FlatButton("Cancel") { };
            cancel.Width = cancel.PreferredWidth;
            isoButton.Visible = copyButton.Visible = videoBox.Visible = false;
            cancel.Location = new Point(dataCard.Width - dataCard.Padding.Right - cancel.Width, (dataCard.Height - cancel.Height) / 2);
            cancel.Anchor = AnchorStyles.Right;
            cancel.Click += (s, e) => job?.Cancel();
            dataCard.Controls.Add(cancel);
            var bar = new ProgressStrip { Value = 0 };
            bar.SetBounds(dataCard.Padding.Left, dataCard.Height - Theme.S(14), dataCard.Width - dataCard.Padding.Horizontal, bar.Height);
            dataCard.Controls.Add(bar);
            string before = dataInfo.Text;
            var lastUi = DateTime.MinValue;

            Task.Run(() => work((f, s) =>
            {
                if ((DateTime.Now - lastUi).TotalMilliseconds < 150 && f < 1) return;
                lastUi = DateTime.Now;
                BeginInvoke(new Action(() => { bar.Value = f; dataInfo.Text = s; }));
            }, () => token.IsCancellationRequested)).ContinueWith(t =>
            {
                job = null;
                dataCard.Controls.Remove(cancel); cancel.Dispose();
                dataCard.Controls.Remove(bar); bar.Dispose();
                isoButton.Visible = copyButton.Visible = true;
                videoBox.Visible = Ffmpeg.Available;
                dataInfo.Text = before;
                unlock.Dispose();
                UpdateRipButton();
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    if (!(ex is OperationCanceledException)) Dialogs.Error(Owner, "That didn't work", ex.Message);
                    return;
                }
                var (message, folder) = (t.Result, (string)null);
                if (message.Contains("\n")) { folder = message.Substring(message.IndexOf('\n') + 1); message = message.Substring(0, message.IndexOf('\n')); }
                if (folder != null && Dialogs.Confirm(Owner, "Finished", message, "Open folder")) Ui.OpenFolder(folder);
                else if (folder == null) Dialogs.Info(Owner, "Finished", message);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SaveIso()
        {
            var d = shown;
            if (d == null) return;
            var label = d.FileSystem?.VolumeLabel;
            using var dlg = new SaveFileDialog
            {
                Filter = "ISO image|*.iso",
                FileName = Ui.SafeName(string.IsNullOrEmpty(label) ? "disc" : label) + ".iso",
                InitialDirectory = Settings.Get("rip.isofolder", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            };
            if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
            Settings.Set("rip.isofolder", Path.GetDirectoryName(dlg.FileName));
            string path = dlg.FileName;
            RunDataJob("Saving disc image…", (p, c) =>
            {
                int bad = DataRipper.SaveImage(d, path, p, c);
                return (bad == 0 ? "The image is an exact copy of the data track." : $"Saved, but {bad} sectors couldn't be read and were filled with zeros.") + "\n" + Path.GetDirectoryName(path);
            });
        }

        void CopyFiles()
        {
            var d = shown;
            if (d == null) return;
            var target = Ui.PickFolder(Owner, "Copy the disc's files into…", Settings.Get("rip.copyfolder", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            if (target == null) return;
            Settings.Set("rip.copyfolder", target);
            var label = d.FileSystem?.VolumeLabel;
            string dest = Path.Combine(target, Ui.SafeName(string.IsNullOrEmpty(label) ? "Disc" : label));
            string container = videoBox.Visible ? videoBox.SelectedIndex switch { 1 => "mp4", 2 => "mov", 3 => "mkv", _ => "" } : "";
            char letter = d.Drive;
            RunDataJob("Copying files…", (p, c) =>
            {
                var errors = DataRipper.CopyFiles(letter, dest, container, p, c);
                return (errors.Count == 0 ? "Every file was copied." : $"{errors.Count} files had problems, first: {errors[0]}") + "\n" + dest;
            });
        }
    }
}
