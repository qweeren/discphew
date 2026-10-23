using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Disc;
using Discphew.Print;

namespace Discphew.UI
{
    /// <summary>
    /// Print-ready jewel case inserts: finds the exact release (tagged ID, disc ID, or a search to pick from), fetches
    /// its artwork from the Cover Art Archive (iTunes for a bigger front), generates a back inlay from the track list
    /// when there's no scan of one, and exports a PDF with crop marks.
    /// </summary>
    sealed class CoverView : ToolView
    {
        /// <summary>Something that can go on a part: a downloaded scan, a local image, or "make one from the track list".</summary>
        sealed class ArtChoice
        {
            public string Label = "";
            public ArtImage Remote;
            public byte[] Local;
            public bool Generate;
            public Bitmap Image;            // preview quality; null until loaded
            public bool Loading, Failed;
            public override string ToString() => Label + (Loading ? "  (loading…)" : Failed ? "  (unavailable)" : Image != null && !Generate ? $"  ·  {Image.Width} × {Image.Height}" : "");
        }

        const string PickFile = "Choose an image file…", NoArt = "No artwork";
        static readonly string[] PartNames = { "Front", "Back inlay", "Disc label", "Booklet" };
        const int BookletTab = 3;

        readonly Card albumCard = new Card { Dock = DockStyle.Top };
        readonly FlatButton discButton = new FlatButton("Use the disc", Icons.Disc);
        readonly FlatButton folderButton = new FlatButton("Open album folder…", Icons.Folder);
        readonly Dropdown releaseBox = new Dropdown();
        readonly TextField artistField = new TextField("Album artist");
        readonly TextField albumField = new TextField("Album title");
        readonly FlatButton findButton = new FlatButton("Find artwork", Icons.Search);
        readonly Caption lookupStatus = new Caption("", Theme.Small, Theme.Faint);
        readonly Panel artistWrap, albumWrap;

        readonly Card previewCard = new Card { Dock = DockStyle.Fill };
        readonly Segmented partTabs = new Segmented(PartNames);
        readonly Dropdown artBox = new Dropdown();
        readonly FlatButton turnLeft = new FlatButton("↺") { Subtle = true };
        readonly FlatButton turnRight = new FlatButton("↻") { Subtle = true };
        readonly FlatButton resetButton = new FlatButton("Reset", Icons.Refresh) { Subtle = true };
        readonly SheetPreview preview = new SheetPreview();
        readonly Caption hint = new Caption("", Theme.Small, Theme.Faint);

        readonly Card outputCard = new Card { Dock = DockStyle.Bottom };
        readonly Dropdown paperBox = new Dropdown();
        readonly CheckField discCheck = new CheckField("Disc label", true);
        readonly CheckField bookletCheck = new CheckField("Full booklet", false);
        readonly FlatButton exportButton = new FlatButton("Export PDF…", Icons.File) { Primary = true };
        readonly ProgressStrip progress = new ProgressStrip { Visible = false };
        readonly Caption status = new Caption("", Theme.Small, Theme.Muted);

        CoverAlbum source;              // as read from the disc or folder
        CoverAlbum album;               // with the chosen release's details filled in, and the names as typed
        DiscInfo sourceDisc;            // when the album is the disc in the drive
        int generation;                 // bumped whenever the album or release changes, so late downloads are dropped
        List<ReleaseMatch> releases = new List<ReleaseMatch>();
        readonly List<ArtChoice> choices = new List<ArtChoice>();
        readonly List<ArtChoice> booklet = new List<ArtChoice>();
        readonly Dictionary<CoverPart, ArtChoice> chosen = new Dictionary<CoverPart, ArtChoice>();
        readonly Dictionary<CoverPart, Placement> places = new Dictionary<CoverPart, Placement>();
        readonly HashSet<CoverPart> picked = new HashSet<CoverPart>();      // parts whose art the user chose, not to be overridden
        readonly ArtChoice generate = new ArtChoice { Label = "Generate from the track list", Generate = true };
        ArtChoice own;                  // the cover the album came with
        string typedArtist, typedTitle; // what the user typed over the names, if they did
        string foundBy = "";
        bool itunesAsked, filling, exporting;

        public CoverView(AppState state) : base(state)
        {
            foreach (CoverPart p in Enum.GetValues(typeof(CoverPart))) places[p] = new Placement();

            // ---- album
            foreach (var b in new[] { discButton, folderButton, findButton }) b.Width = b.PreferredWidth;
            discButton.Click += (s, e) => LoadDisc();
            folderButton.Click += (s, e) => OpenFolder();
            findButton.Click += (s, e) => Identify();
            releaseBox.Format = o => o?.ToString() ?? "";
            releaseBox.SelectedIndexChanged += (s, e) => { if (!filling && releaseBox.SelectedItem is ReleaseMatch m) UseRelease(m); };
            releaseBox.Visible = false;
            artistWrap = Ui.Labeled("Album artist", artistField, 200);
            albumWrap = Ui.Labeled("Album", albumField, 200);
            artistField.TextChanged += (s, e) => { if (!filling && album != null) { album.Artist = typedArtist = artistField.Text; Redraw(); } };
            albumField.TextChanged += (s, e) => { if (!filling && album != null) { album.Title = typedTitle = albumField.Text; Redraw(); } };
            albumCard.Controls.AddRange(new Control[] { discButton, folderButton, releaseBox, artistWrap, albumWrap, findButton, lookupStatus });
            albumCard.Height = Theme.S(136);
            albumCard.Resize += (s, e) => LayoutAlbum();

            // ---- preview
            partTabs.SelectedIndexChanged += (s, e) => ShowPart();
            artBox.Width = Theme.S(260);
            artBox.Format = o => o?.ToString() ?? "";
            artBox.SelectedIndexChanged += (s, e) => { if (!filling) ArtPicked(); };
            turnLeft.Width = turnRight.Width = Theme.S(34);
            resetButton.Width = resetButton.PreferredWidth;
            turnLeft.Click += (s, e) => preview.Turn(-0.5);
            turnRight.Click += (s, e) => preview.Turn(0.5);
            resetButton.Click += (s, e) => preview.Reset();
            preview.Changed += () => resetButton.Enabled = preview.Place != null && !preview.Place.IsDefault;
            previewCard.Controls.AddRange(new Control[] { partTabs, artBox, turnLeft, turnRight, resetButton, preview, hint });
            previewCard.Resize += (s, e) => LayoutPreview();

            // ---- output
            paperBox.Width = Theme.S(170);
            paperBox.SetItems(new object[] { "A4", "US Letter" }, Settings.Get("covers.paper", "A4") == "US Letter" ? 1 : 0);
            paperBox.Format = o => "Paper: " + o;
            paperBox.SelectedIndexChanged += (s, e) => Settings.Set("covers.paper", paperBox.SelectedItem?.ToString() ?? "A4");
            discCheck.Checked = Settings.GetBool("covers.disc", true);
            discCheck.CheckedChanged += (s, e) => Settings.Set("covers.disc", discCheck.Checked);
            bookletCheck.CheckedChanged += (s, e) => { if (bookletCheck.Checked && partTabs.SelectedIndex != BookletTab) partTabs.SelectedIndex = BookletTab; };
            exportButton.Width = Math.Max(Theme.S(150), exportButton.PreferredWidth);
            exportButton.Click += (s, e) => Export();
            outputCard.Controls.AddRange(new Control[] { paperBox, discCheck, bookletCheck, status, exportButton, progress });
            outputCard.Height = Theme.S(70);
            outputCard.Resize += (s, e) => LayoutOutput();

            Controls.Add(previewCard);
            Controls.Add(new Panel { Dock = DockStyle.Top, Height = Theme.S(14), BackColor = Theme.Bg });
            Controls.Add(albumCard);
            Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = Theme.S(14), BackColor = Theme.Bg });
            Controls.Add(outputCard);
            ShowAlbum();
        }

        public override string Title => "Cover Printer";

        public override string Subtitle =>
            album == null ? "Print a front booklet, tray inlay and disc label for the CD in the drive or an album folder"
            : "Jewel case inserts for " + (album.Source.StartsWith("the disc") ? album.Source : Path.GetFileName(album.Source.TrimEnd('\\')));

        // ------------------------------------------------------------------ layout

        void LayoutAlbum()
        {
            int pad = albumCard.Padding.Left, gap = Theme.S(10), w = albumCard.Width - pad * 2;
            discButton.Location = new Point(pad, pad);
            folderButton.Location = new Point(discButton.Right + Theme.S(8), pad);
            int rx = folderButton.Right + Theme.S(16);
            releaseBox.SetBounds(rx, pad, Math.Max(Theme.S(120), albumCard.Width - pad - rx), discButton.Height);
            int y2 = discButton.Bottom + Theme.S(12);
            int fieldsW = Math.Max(Theme.S(200), w - findButton.Width - gap) - gap;
            int aw = fieldsW * 2 / 5;
            artistWrap.SetBounds(pad, y2, aw, artistWrap.Height);
            albumWrap.SetBounds(artistWrap.Right + gap, y2, fieldsW - aw, albumWrap.Height);
            findButton.Location = new Point(albumWrap.Right + gap, y2 + albumWrap.Height - findButton.Height);
            // The status line shares the release picker's row while there's no list to pick from.
            lookupStatus.SetBounds(rx, pad, Math.Max(10, albumCard.Width - pad - rx), discButton.Height);
        }

        void LayoutPreview()
        {
            int pad = previewCard.Padding.Left;
            partTabs.Location = new Point(pad, pad);
            resetButton.Location = new Point(previewCard.Width - pad - resetButton.Width, pad);
            turnRight.Location = new Point(resetButton.Left - Theme.S(4) - turnRight.Width, pad);
            turnLeft.Location = new Point(turnRight.Left - Theme.S(2) - turnLeft.Width, pad);
            int artLeft = partTabs.Right + Theme.S(12);
            artBox.SetBounds(artLeft, pad, Math.Max(Theme.S(120), turnLeft.Left - Theme.S(12) - artLeft), partTabs.Height);
            hint.SetBounds(pad, previewCard.Height - pad - hint.Height + Theme.S(4), previewCard.Width - pad * 2, hint.Height);
            hint.TextAlign = ContentAlignment.MiddleCenter;
            int top = partTabs.Bottom + Theme.S(8);
            preview.SetBounds(pad, top, previewCard.Width - pad * 2, Math.Max(10, hint.Top - top - Theme.S(4)));
        }

        void LayoutOutput()
        {
            int pad = outputCard.Padding.Left, h = outputCard.Height;
            exportButton.Location = new Point(outputCard.Width - pad - exportButton.Width, (h - exportButton.Height) / 2);
            paperBox.Location = new Point(pad, exportButton.Top);
            discCheck.Location = new Point(paperBox.Right + Theme.S(18), exportButton.Top + (exportButton.Height - discCheck.Height) / 2);
            bookletCheck.Location = new Point(discCheck.Right + Theme.S(18), discCheck.Top);
            int sx = bookletCheck.Right + Theme.S(18);
            status.SetBounds(sx, exportButton.Top, Math.Max(10, exportButton.Left - sx - Theme.S(12)), exportButton.Height);
            status.TextAlign = ContentAlignment.MiddleRight;
            progress.SetBounds(pad, h - Theme.S(12), outputCard.Width - pad * 2, progress.Height);
        }

        // ------------------------------------------------------------------ the album

        public override void OnActivated()
        {
            if (source == null && State.Disc?.HasAudio == true) LoadDisc();
        }

        public override void OnDiscChanged()
        {
            discButton.Enabled = State.Disc?.HasAudio == true;
            // Follow a disc change while showing the disc; a folder stays put.
            if (sourceDisc != null && !ReferenceEquals(sourceDisc, State.Disc))
            {
                if (State.Disc?.HasAudio == true && Visible) LoadDisc();
                else SetAlbum(null, null);          // picked up again when the tool is opened
            }
        }

        void LoadDisc()
        {
            var d = State.Disc;
            if (d == null || !d.HasAudio) return;
            SetAlbum(CoverAlbum.FromDisc(d), d);
        }

        void OpenFolder()
        {
            var folder = Ui.PickFolder(Owner, "Choose an album folder (FLAC, WAV, or anything ffmpeg can read; a cue sheet is used if there is one)",
                Settings.Get("covers.folder", Settings.Get("player.folder", Ui.DefaultMusicFolder)));
            if (folder == null) return;
            Settings.Set("covers.folder", folder);
            Cursor = Cursors.WaitCursor;
            Task.Run(() => CoverAlbum.FromFolder(folder)).ContinueWith(t =>
            {
                Cursor = Cursors.Default;
                if (t.IsFaulted) { Dialogs.Error(Owner, "Can't read that folder", t.Exception.GetBaseException().Message); return; }
                SetAlbum(t.Result, null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetAlbum(CoverAlbum a, DiscInfo disc)
        {
            generation++;
            source = a;
            sourceDisc = disc;
            album = a?.Copy();
            typedArtist = typedTitle = null;
            releases = new List<ReleaseMatch>();
            ClearArt();
            if (a?.Cover != null)
            {
                own = new ArtChoice { Label = "Front · " + (disc != null ? "saved with the disc" : "from the album folder"), Local = a.Cover, Image = CoverRender.Decode(a.Cover, 1600) };
                if (own.Image != null) { choices.Add(own); chosen[CoverPart.Front] = chosen[CoverPart.Disc] = own; }
                else own = null;
            }
            if (!chosen.ContainsKey(CoverPart.Tray)) chosen[CoverPart.Tray] = generate;
            ShowAlbum();
            if (a != null) Identify();
        }

        void ClearArt()
        {
            foreach (var c in choices.Concat(booklet)) c.Image?.Dispose();
            choices.Clear();
            booklet.Clear();
            chosen.Clear();
            picked.Clear();
            foreach (var p in places.Values) p.Reset();
            own = null;
            itunesAsked = false;
            bookletCheck.Checked = false;
        }

        void ShowAlbum()
        {
            filling = true;
            artistField.Text = album?.Artist ?? "";
            albumField.Text = album?.Title ?? "";
            releaseBox.SetItems(releases.Cast<object>(), Math.Max(0, releases.FindIndex(r => r.Id == album?.ReleaseId)));
            filling = false;
            releaseBox.Visible = releases.Count > 1;
            lookupStatus.Visible = !releaseBox.Visible;
            findButton.Enabled = album != null;
            exportButton.Enabled = album != null && !exporting;
            bookletCheck.Enabled = booklet.Count > 0;
            bookletCheck.Text = booklet.Count > 0 ? $"Full booklet ({booklet.Count} scan{(booklet.Count == 1 ? "" : "s")})" : "Full booklet";
            bookletCheck.Width = TextRenderer.MeasureText(bookletCheck.Text, Theme.Body).Width + Theme.S(34);
            if (album == null) lookupStatus.Text = State.Disc?.HasAudio == true ? "" : "Insert an audio CD, or open an album folder.";
            LayoutOutput();
            ShowPart();
            (Owner as MainForm)?.UpdateHeader();
        }

        // ------------------------------------------------------------------ finding the release

        /// <summary>
        /// Pins down the exact release: the MusicBrainz ID in the tags, else the disc ID (exact pressing, no tags needed),
        /// else a search on the artist and title as typed, best match first, for the user to correct.
        /// </summary>
        void Identify()
        {
            if (album == null) return;
            var a = source;
            string artist = artistField.Text.Trim(), title = albumField.Text.Trim();
            int gen = ++generation;
            foundBy = "";
            findButton.Enabled = false;
            lookupStatus.Text = "Looking up the release on MusicBrainz…";
            releaseBox.Visible = false;
            lookupStatus.Visible = true;
            Task.Run(() =>
            {
                if (a.ReleaseId.Length > 0)
                {
                    var r = MusicBrainz.Release(a.ReleaseId, a.DiscNumber, a.Tracks.Count);
                    if (r != null) return (new List<ReleaseMatch> { r }, "the release it's tagged with");
                }
                if (a.Toc != null && a.Toc.Tracks.Count > 0)
                {
                    var list = MusicBrainz.Lookup(a.Toc);
                    if (list.Count > 0) return (list, "its disc ID");
                }
                return (MusicBrainz.Search(artist, title, a.Tracks.Count), "search");
            }).ContinueWith(t =>
            {
                if (gen != generation) return;
                findButton.Enabled = true;
                if (t.IsFaulted) { lookupStatus.Text = "Lookup failed: " + t.Exception.GetBaseException().Message; return; }
                var (list, how) = t.Result;
                releases = list;
                if (list.Count == 0)
                {
                    lookupStatus.Text = title.Length == 0 ? "Not found on MusicBrainz. Type the artist and album, then Find artwork." : "No release by that name on MusicBrainz. Check the spelling, or choose image files below.";
                    ShowAlbum();
                    return;
                }
                foundBy = how == "search" ? list.Count == 1 ? "Found by name" : "" : $"Found by {how}";
                lookupStatus.Text = foundBy;
                filling = true;
                releaseBox.SetItems(list, 0);
                filling = false;
                releaseBox.Visible = list.Count > 1;
                lookupStatus.Visible = !releaseBox.Visible;
                UseRelease(list[0]);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Fills the album in from the release and lists its artwork.</summary>
        void UseRelease(ReleaseMatch r)
        {
            int gen = ++generation;
            var a = source;
            foreach (var c in choices.Concat(booklet).Where(c => c.Remote != null)) c.Image?.Dispose();
            choices.RemoveAll(c => c.Remote != null);
            booklet.Clear();
            foreach (var p in chosen.Where(kv => kv.Value.Remote != null).Select(kv => kv.Key).ToList()) { chosen.Remove(p); picked.Remove(p); }
            itunesAsked = false;
            lookupStatus.Text = "Fetching the artwork list…";
            Task.Run(() =>
            {
                // Search results come without their track lists.
                var full = r.Tracks.Count > 0 ? r : MusicBrainz.Release(r.Id, a.DiscNumber, a.Tracks.Count) ?? r;
                return (full, CoverArt.List(full));
            }).ContinueWith(t =>
            {
                if (gen != generation) return;
                if (t.IsFaulted) { lookupStatus.Text = "Couldn't reach the Cover Art Archive: " + t.Exception.GetBaseException().Message; ShowAlbum(); return; }
                var (full, images) = t.Result;
                album = a.Copy();
                album.Fill(full);
                album.ReleaseId = full.Id;
                // Names the user typed win over the files', the disc's and the release's.
                if (!string.IsNullOrWhiteSpace(typedArtist)) album.Artist = typedArtist;
                if (!string.IsNullOrWhiteSpace(typedTitle)) album.Title = typedTitle;
                foreach (var img in images)
                {
                    var c = new ArtChoice { Label = img.ToString(), Remote = img };
                    if (img.Is("Booklet") && !img.Is("Front")) booklet.Add(c);
                    choices.Add(c);
                }
                int counted = images.Count;
                string art = counted == 0 ? "no artwork for it on the Cover Art Archive" : $"{counted} image{(counted == 1 ? "" : "s")} on the Cover Art Archive";
                lookupStatus.Text = foundBy.Length > 0 ? foundBy + "  ·  " + art : char.ToUpperInvariant(art[0]) + art.Substring(1);
                Pick(CoverPart.Front, choices.FirstOrDefault(c => c.Remote != null && c.Remote.Is("Front")) ?? (chosen.ContainsKey(CoverPart.Front) ? null : own));
                Pick(CoverPart.Tray, choices.FirstOrDefault(c => c.Remote != null && c.Remote.Is("Back")));
                Pick(CoverPart.Disc, choices.FirstOrDefault(c => c.Remote != null && c.Remote.Is("Medium")));
                CheckFront();
                ShowAlbum();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Makes <paramref name="c"/> the art for a part, unless the user chose that part's art themselves. The disc follows the front.</summary>
        void Pick(CoverPart part, ArtChoice c)
        {
            if (c == null || picked.Contains(part)) return;
            var old = chosen.TryGetValue(part, out var o) ? o : null;
            chosen[part] = c;
            if (part == CoverPart.Front && !picked.Contains(CoverPart.Disc) && (!chosen.TryGetValue(CoverPart.Disc, out var disc) || disc == old || disc.Remote == null || !disc.Remote.Is("Medium")))
                chosen[CoverPart.Disc] = c;
            Fetch(c);
        }

        /// <summary>Too small to print sharp: 120 mm at 300 DPI wants 1400-odd pixels, and under 1000 it starts to show.</summary>
        static bool Small(ArtChoice c) => c == null || c.Failed || c.Image != null && c.Image.Width < 1000;

        /// <summary>Looks for a bigger front on iTunes when there's none, or only a small one (the archive's own, or the 500 px one the Ripper saved).</summary>
        void CheckFront()
        {
            if (Small(chosen.TryGetValue(CoverPart.Front, out var f) ? f : null)) AskItunes();
        }

        /// <summary>The iTunes front, usually 3000 px; it becomes the front unless the user picked one.</summary>
        void AskItunes()
        {
            if (itunesAsked || album == null) return;
            itunesAsked = true;
            int gen = generation;
            string artist = album.Artist, title = album.Title;
            Task.Run(() => CoverArt.Itunes(artist, title)).ContinueWith(t =>
            {
                if (gen != generation || t.IsFaulted || t.Result == null) return;
                var c = new ArtChoice { Label = "Front · iTunes", Remote = t.Result };
                choices.Add(c);
                if (Small(chosen.TryGetValue(CoverPart.Front, out var f) ? f : null)) Pick(CoverPart.Front, c);
                ShowPart();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Downloads (or takes from the cache) a preview-sized copy of a scan.</summary>
        void Fetch(ArtChoice c)
        {
            if (c == null || c.Image != null || c.Loading || c.Remote == null) return;
            c.Loading = true;
            c.Failed = false;
            int gen = generation;
            var img = c.Remote;
            Task.Run(() => CoverRender.Decode(CoverArt.Fetch(img, 1200), 1600)).ContinueWith(t =>
            {
                var bmp = t.IsFaulted ? null : t.Result;
                if (gen != generation) { bmp?.Dispose(); return; }
                c.Loading = false;
                c.Image = bmp;
                c.Failed = bmp == null;
                if (chosen.TryGetValue(CoverPart.Front, out var front) && front == c) CheckFront();
                ShowPart();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ------------------------------------------------------------------ parts and the preview

        CoverPart? CurrentPart => partTabs.SelectedIndex switch { 0 => CoverPart.Front, 1 => CoverPart.Tray, 2 => CoverPart.Disc, _ => (CoverPart?)null };

        void ShowPart()
        {
            var part = CurrentPart;
            filling = true;
            if (part.HasValue && album != null)
            {
                var items = new List<object>();
                if (part == CoverPart.Tray) items.Add(generate);
                items.AddRange(choices);
                items.Add(PickFile);
                var current = chosen.TryGetValue(part.Value, out var c) ? c : null;
                if (current == null) items.Insert(0, NoArt);
                artBox.SetItems(items, Math.Max(0, items.IndexOf((object)current ?? NoArt)));
                artBox.Visible = true;
            }
            else artBox.Visible = false;
            filling = false;
            if (part == null) foreach (var b in booklet) Fetch(b);

            bool adjustable = part.HasValue && album != null && chosen.TryGetValue(part.Value, out var art) && !art.Generate;
            turnLeft.Visible = turnRight.Visible = resetButton.Visible = adjustable;
            preview.Place = adjustable ? places[part.Value] : null;
            preview.Part = part;
            preview.Booklet = part == null;
            resetButton.Enabled = adjustable && !places[part.Value].IsDefault;
            preview.Render = album == null ? null : (Func<double, Bitmap>)(k => RenderPreview(part, k));
            preview.Message = album == null ? "Insert an audio CD or open an album folder to make its covers"
                : part == null && booklet.Count == 0 ? "The Cover Art Archive has no booklet scans for this release"
                : part.HasValue && !chosen.ContainsKey(part.Value) ? "No artwork yet: choose an image above" : "";
            hint.Text = adjustable ? "Drag to move  ·  scroll or +/− to zoom  ·  [ ] to straighten  ·  arrow keys nudge  ·  double-click resets"
                : part == CoverPart.Tray ? "The back inlay is made from the track list over a blur of the front cover. Pick a scan above to use that instead."
                : part == null && booklet.Count > 0 ? "Each sheet prints double-sided and folds in the middle. Nest the sheets in order and staple at the fold."
                : "";
            preview.Dirty();
        }

        void Redraw() { if (CurrentPart != CoverPart.Front) preview.Dirty(); }

        void ArtPicked()
        {
            var part = CurrentPart;
            if (!part.HasValue) return;
            if (NoArt.Equals(artBox.SelectedItem)) return;
            if (PickFile.Equals(artBox.SelectedItem))
            {
                using var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", Title = $"Choose the {PartNames[partTabs.SelectedIndex].ToLowerInvariant()} image" };
                if (dlg.ShowDialog(Owner) == DialogResult.OK)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(dlg.FileName);
                        var image = CoverRender.Decode(bytes, 1600) ?? throw new InvalidDataException("That file isn't an image Windows can read.");
                        var c = new ArtChoice { Label = Path.GetFileName(dlg.FileName), Local = bytes, Image = image };
                        choices.Add(c);
                        Choose(part.Value, c);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                    {
                        Dialogs.Error(Owner, "Couldn't use that image", ex.Message);
                    }
                }
                ShowPart();
                return;
            }
            if (artBox.SelectedItem is ArtChoice choice) Choose(part.Value, choice);
            ShowPart();
        }

        void Choose(CoverPart part, ArtChoice c)
        {
            var old = chosen.TryGetValue(part, out var o) ? o : null;
            chosen[part] = c;
            picked.Add(part);
            places[part].Reset();
            // A disc label that was showing the front keeps showing it.
            if (part == CoverPart.Front && !picked.Contains(CoverPart.Disc) && chosen.TryGetValue(CoverPart.Disc, out var disc) && disc == old)
            {
                chosen[CoverPart.Disc] = c;
                places[CoverPart.Disc].Reset();
            }
            Fetch(c);
        }

        Bitmap ArtFor(CoverPart part) => chosen.TryGetValue(part, out var c) && !c.Generate ? c.Image : null;

        Bitmap RenderPreview(CoverPart? part, double k)
        {
            if (part.HasValue) return CoverRender.Render(part.Value, ArtFor(part.Value), places[part.Value], album, ArtFor(CoverPart.Front), k);
            // Booklet: every printed side in the order they print, a sheet (outside, inside) to a row, or as many
            // sheets to a row as makes them biggest in the preview.
            using var front = CoverRender.Render(CoverPart.Front, ArtFor(CoverPart.Front), places[CoverPart.Front], album, null, k);
            var sides = BookletSides(CoverRender.BookletFront(front), booklet.Select(b => b.Image).Where(i => i != null).ToList(), k);
            int grid = BookletGrid(sides.Count);
            double gap = 8 * k;
            int sw = sides.Count > 0 ? sides[0].Width : 1, sh = sides.Count > 0 ? sides[0].Height : 1;
            int rows = (sides.Count + grid - 1) / Math.Max(1, grid);
            var all = new Bitmap(Math.Max(1, (int)(grid * sw + (grid - 1) * gap)), Math.Max(1, (int)(rows * sh + (rows - 1) * gap)));
            using (var g = Graphics.FromImage(all))
                for (int i = 0; i < sides.Count; i++)
                {
                    g.DrawImage(sides[i], (float)(i % grid * (sw + gap)), (float)(i / grid * (sh + gap)), sw, sh);
                    sides[i].Dispose();
                }
            return all;
        }

        /// <summary>Sides per row: one, or a whole number of sheets, whichever shows them biggest in the preview.</summary>
        int BookletGrid(int sides)
        {
            double best = 0;
            int pick = 1;
            for (int cols = 1; cols <= Math.Max(1, sides); cols = cols == 1 ? 2 : cols + 2)
            {
                int rows = (sides + cols - 1) / cols;
                double w = cols * 246 + (cols - 1) * 8, h = rows * 126 + (rows - 1) * 8;
                double fit = Math.Min(preview.Width / w, preview.Height / h);
                if (fit > best) { best = fit; pick = cols; }
            }
            return pick;
        }

        /// <summary>The booklet's printed sides: the front and the scanned pages imposed for folding. Takes ownership of <paramref name="front"/>.</summary>
        static List<Bitmap> BookletSides(Bitmap front, List<Bitmap> scans, double k)
        {
            var pages = CoverRender.BookletPages(front, scans);
            front.Dispose();
            var sides = CoverRender.Impose(pages.Count).Select(s => CoverRender.BookletSide(s.left < 0 ? null : pages[s.left], s.right < 0 ? null : pages[s.right], k)).ToList();
            foreach (var p in pages) p.Dispose();
            return sides;
        }

        // Only while the preview has focus: elsewhere the arrow keys belong to the drop-downs.
        public override bool HandleKey(Keys key) => preview.Focused && preview.HandleKey(key);

        // ------------------------------------------------------------------ export

        void Export()
        {
            if (album == null || exporting) return;
            bool withBooklet = bookletCheck.Checked && booklet.Count > 0;
            using var dlg = new SaveFileDialog
            {
                Filter = "PDF|*.pdf",
                FileName = Ui.SafeName($"{album.Artist} - {album.Title} (covers)", "covers") + ".pdf",
                InitialDirectory = Settings.Get("covers.pdffolder", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            };
            if (dlg.ShowDialog(Owner) != DialogResult.OK) return;
            Settings.Set("covers.pdffolder", Path.GetDirectoryName(dlg.FileName));
            string path = dlg.FileName;

            // Everything the background job needs, copied: the user can carry on changing things meanwhile.
            (ArtImage remote, byte[] local, Bitmap fallback) Snap(ArtChoice c) =>
                c == null || c.Generate ? (null, null, null) : (c.Remote, c.Local, c.Image == null ? null : new Bitmap(c.Image));
            var parts = new Dictionary<CoverPart, (ArtImage, byte[], Bitmap)>();
            foreach (CoverPart p in Enum.GetValues(typeof(CoverPart))) parts[p] = Snap(chosen.TryGetValue(p, out var c) ? c : null);
            var place = places.ToDictionary(kv => kv.Key, kv => new Placement { Zoom = kv.Value.Zoom, X = kv.Value.X, Y = kv.Value.Y, Angle = kv.Value.Angle });
            var scans = withBooklet ? booklet.Select(Snap).ToList() : new List<(ArtImage, byte[], Bitmap)>();
            var a = album.Copy();
            bool letter = paperBox.SelectedIndex == 1, disc = discCheck.Checked;

            exporting = true;
            exportButton.Enabled = false;
            progress.Visible = true;
            progress.Value = -1;
            status.Text = "Fetching full-size artwork…";
            int total = 3 + scans.Count, done = 0;
            void Step(string what) => BeginInvoke(new Action(() => { status.Text = what; progress.Value = ++done / (double)total; }));

            Task.Run(() =>
            {
                Bitmap Full((ArtImage remote, byte[] local, Bitmap fallback) s)
                {
                    // The original for print, falling back to the preview copy if it can't be had.
                    var bytes = s.remote != null ? CoverArt.Fetch(s.remote, 0) ?? CoverArt.Fetch(s.remote, 1200) : s.local;
                    var bmp = CoverRender.Decode(bytes, 3000);
                    if (bmp != null) { s.fallback?.Dispose(); return bmp; }
                    return s.fallback;
                }
                var art = new Dictionary<CoverPart, Bitmap>();
                var pages = new List<Bitmap>();
                var sheet = new CoverRender.Sheet { PaperWidth = letter ? 215.9 : 210, PaperHeight = letter ? 279.4 : 297, Caption = $"{a.Artist} – {a.Title}" };
                try
                {
                    double k = Templates.PrintScale;
                    foreach (var p in parts.Keys) art[p] = Full(parts[p]);
                    Step("Laying out the front");
                    var front = CoverRender.Render(CoverPart.Front, art[CoverPart.Front], place[CoverPart.Front], a, null, k);
                    Step("Laying out the back inlay");
                    sheet.Tray = CoverRender.Render(CoverPart.Tray, art[CoverPart.Tray], place[CoverPart.Tray], a, art[CoverPart.Front], k);
                    if (disc) sheet.Disc = CoverRender.Render(CoverPart.Disc, art[CoverPart.Disc], place[CoverPart.Disc], a, art[CoverPart.Front], k);
                    Step(disc ? "Laying out the disc label" : "Laying out");
                    if (scans.Count > 0)
                    {
                        foreach (var s in scans) { var b = Full(s); if (b != null) pages.Add(b); Step($"Fetched booklet page {pages.Count} of {scans.Count}"); }
                        sheet.BookletSides.AddRange(BookletSides(CoverRender.BookletFront(front), pages, k));
                        front.Dispose();
                    }
                    else sheet.Front = front;
                    File.WriteAllBytes(path, CoverRender.Export(sheet));
                    return sheet.BookletSides.Count;
                }
                finally
                {
                    foreach (var b in art.Values.Distinct()) b?.Dispose();
                    foreach (var b in pages) b.Dispose();
                    sheet.Front?.Dispose(); sheet.Tray?.Dispose(); sheet.Disc?.Dispose();
                    foreach (var b in sheet.BookletSides) b.Dispose();
                }
            }).ContinueWith(t =>
            {
                exporting = false;
                exportButton.Enabled = album != null;
                progress.Visible = false;
                if (t.IsFaulted)
                {
                    status.Text = "Export failed";
                    Dialogs.Error(Owner, "Couldn't make the PDF", t.Exception.GetBaseException().Message);
                    return;
                }
                status.Text = "Saved " + Path.GetFileName(path);
                string what = t.Result > 0 ? $"The tray inlay{(disc ? " and disc label" : "")}, then {t.Result / 2} booklet sheet{(t.Result == 2 ? "" : "s")} to print double-sided." : $"The front booklet and tray inlay{(disc ? ", then the disc label" : "")}.";
                if (Dialogs.Confirm(Owner, "PDF ready", what + "\n\nPrint it at actual size (100 %), not “fit to page”, and cut along the crop marks.", "Open PDF"))
                    try { System.Diagnostics.Process.Start(path); } catch (Exception ex) { Dialogs.Error(Owner, "Couldn't open the PDF", ex.Message); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { generation++; foreach (var c in choices.Concat(booklet)) c.Image?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One part at screen size, with its cut lines, bleed and folds shown, and the artwork moved with the mouse:
    /// drag to move, scroll to zoom, [ and ] to straighten a crooked scan, arrow keys to nudge.
    /// </summary>
    sealed class SheetPreview : Painted
    {
        public Func<double, Bitmap> Render;
        public CoverPart? Part;
        public bool Booklet;
        public Placement Place;
        public string Message = "";
        public event Action Changed;

        Bitmap shown;
        double shownScale;
        bool dirty = true, dragging;
        Point last;

        public SheetPreview() { TabStop = true; }

        public void Dirty() { dirty = true; Invalidate(); }

        void Moved() { dirty = true; Invalidate(); Changed?.Invoke(); }

        public void Turn(double degrees) { if (Place == null) return; Place.Angle = Math.Round(Place.Angle + degrees, 2); Moved(); }
        public void Reset() { if (Place == null) return; Place.Reset(); Moved(); }

        /// <summary>The size in mm that has to fit: the part with its bleed, or every booklet side stacked.</summary>
        SizeF Mm()
        {
            if (Part.HasValue) return Templates.WithBleed(Part.Value);
            return new SizeF(246, 126);
        }

        /// <summary>Pixels per mm that fit the part in the control with a margin.</summary>
        double Fit
        {
            get
            {
                var mm = Mm();
                float margin = Theme.S(18f);
                if (Booklet && shown != null && shownScale > 0) mm = new SizeF((float)(shown.Width / shownScale), (float)(shown.Height / shownScale));
                return Math.Max(0.05, Math.Min((Width - margin * 2) / mm.Width, (Height - margin * 2) / mm.Height));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            if (Render == null)
            {
                Theme.DrawText(g, Message, Theme.Heading, Theme.Faint, ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            double k = Fit;
            if (dirty || shown == null || Math.Abs(shownScale - k) > 0.01)
            {
                shown?.Dispose();
                shown = null;
                try { shown = Render(k); shownScale = k; }
                catch (ArgumentException) { }       // GDI+ out of its depth (a vanishingly small control)
                dirty = false;
                if (Booklet && shown != null && Math.Abs(Fit - k) > 0.01) { Dirty(); return; }      // now that the stack's height is known
            }
            if (shown == null) return;
            float x = (Width - shown.Width) / 2f, y = (Height - shown.Height) / 2f;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(shown, x, y, shown.Width, shown.Height);
            if (Part.HasValue) Guides(g, Part.Value, new RectangleF(x, y, shown.Width, shown.Height), (float)k);
            if (Message.Length > 0)
                Theme.DrawText(g, Message, Theme.Body, Theme.Muted, new Rectangle(0, Height - Theme.S(26), Width, Theme.S(24)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Shades the bleed (it's cut off), outlines the cut and dashes the folds.</summary>
        void Guides(Graphics g, CoverPart part, RectangleF r, float k)
        {
            float b = (float)Templates.Bleed * k;
            var trim = RectangleF.Inflate(r, -b, -b);
            using var shade = new SolidBrush(Theme.WithAlpha(Theme.Panel, 170));
            using var cut = new Pen(Theme.WithAlpha(Theme.Accent, 200), 1f);
            using var fold = new Pen(Theme.WithAlpha(Theme.Accent, 200), 1f) { DashStyle = DashStyle.Dash };
            if (part == CoverPart.Disc)
            {
                float outer = (float)(Templates.DiscOuter / 2) * k, hole = (float)(Templates.DiscHole / 2) * k, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                using (var ring = new GraphicsPath(FillMode.Alternate))
                {
                    ring.AddRectangle(r);
                    ring.AddEllipse(cx - outer, cy - outer, outer * 2, outer * 2);
                    g.FillPath(shade, ring);
                }
                g.FillEllipse(shade, cx - hole, cy - hole, hole * 2, hole * 2);
                g.DrawEllipse(cut, cx - outer, cy - outer, outer * 2, outer * 2);
                g.DrawEllipse(cut, cx - hole, cy - hole, hole * 2, hole * 2);
                return;
            }
            using (var band = new GraphicsPath(FillMode.Alternate))
            {
                band.AddRectangle(r);
                band.AddRectangle(trim);
                g.FillPath(shade, band);
            }
            g.DrawRectangle(cut, trim.X, trim.Y, trim.Width, trim.Height);
            if (part == CoverPart.Tray)
                foreach (var fx in new[] { trim.X + (float)Templates.Spine * k, trim.Right - (float)Templates.Spine * k })
                    g.DrawLine(fold, fx, trim.Y, fx, trim.Bottom);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left && Place != null) { dragging = true; last = e.Location; Cursor = Cursors.SizeAll; }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging && Place != null)
            {
                double k = Fit;
                Place.X += (e.X - last.X) / k;
                Place.Y += (e.Y - last.Y) / k;
                last = e.Location;
                Moved();
            }
            else Cursor = Place != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Cursor = Place != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Place != null) Zoom(Math.Pow(1.05, e.Delta / 120.0));
            base.OnMouseWheel(e);
        }

        protected override void OnDoubleClick(EventArgs e) { Reset(); base.OnDoubleClick(e); }

        void Zoom(double factor) { Place.Zoom = Math.Max(0.2, Math.Min(8, Place.Zoom * factor)); Moved(); }

        void Nudge(double dx, double dy) { Place.X += dx; Place.Y += dy; Moved(); }

        /// <summary>Arrows nudge 0.5 mm (Shift: 0.1, Ctrl: 2), [ ] turn 0.25° (Shift: 1°), + − zoom, 0 resets.</summary>
        public bool HandleKey(Keys key)
        {
            if (Place == null) return false;
            var code = key & Keys.KeyCode;
            double step = (key & Keys.Shift) != 0 ? 0.1 : (key & Keys.Control) != 0 ? 2 : 0.5;
            switch (code)
            {
                case Keys.Left: Nudge(-step, 0); return true;
                case Keys.Right: Nudge(step, 0); return true;
                case Keys.Up: Nudge(0, -step); return true;
                case Keys.Down: Nudge(0, step); return true;
                case Keys.OemOpenBrackets: Turn((key & Keys.Shift) != 0 ? -1 : -0.25); return true;
                case Keys.OemCloseBrackets: Turn((key & Keys.Shift) != 0 ? 1 : 0.25); return true;
                case Keys.Oemplus: case Keys.Add: Zoom(1.02); return true;
                case Keys.OemMinus: case Keys.Subtract: Zoom(1 / 1.02); return true;
                case Keys.D0: case Keys.NumPad0: Reset(); return true;
            }
            return false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            var code = keyData & Keys.KeyCode;
            return code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleKey(e.KeyData)) e.Handled = true;
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing) { if (disposing) shown?.Dispose(); base.Dispose(disposing); }
    }
}
