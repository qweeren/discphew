using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Discphew.Disc;

namespace Discphew.UI
{
    sealed class VisualizerView : ToolView
    {
        readonly DiscCanvas canvas = new DiscCanvas { Dock = DockStyle.Fill };
        readonly Card summary = new Card();
        readonly Label summaryText = new Label { BackColor = Color.Transparent, ForeColor = Theme.Muted, UseMnemonic = false, Font = Theme.Body };
        readonly Caption summaryTitle = new Caption("", Theme.Heading, Theme.Text);
        readonly Segmented listMode = new Segmented("Layout", "File types");
        readonly DarkGrid grid = new DarkGrid();
        readonly Panel side = new Panel { Dock = DockStyle.Right, BackColor = Theme.Bg };

        List<DiscSegment> segments = new List<DiscSegment>();
        DiscSegment[] rows = new DiscSegment[0];
        List<(Color color, string name, int count, long bytes)> legend = new List<(Color, string, int, long)>();
        DiscInfo shown;
        bool syncing;
        // Moving the list's current row scrolls and repaints the grid; sweeping across thousands of files would do
        // that on every mouse move, so hover follows the list at most every 50 ms (and always lands on the last one).
        readonly Timer hoverSync = new Timer { Interval = 50 };
        int hoverPending = -1;

        public VisualizerView(AppState state) : base(state)
        {
            side.Width = Theme.S(400);
            side.Padding = new Padding(Theme.S(20), 0, 0, 0);

            summary.Dock = DockStyle.Top;
            summary.Height = Theme.S(180);
            summaryTitle.Dock = DockStyle.Top;
            summaryText.Dock = DockStyle.Fill;
            summary.Controls.Add(summaryText);
            summary.Controls.Add(summaryTitle);

            var modeRow = new Panel { Dock = DockStyle.Top, Height = Theme.S(54), BackColor = Theme.Bg };
            listMode.Location = new Point(0, Theme.S(12));
            listMode.Width = Theme.S(260);
            listMode.SelectedIndexChanged += (s, e) => FillGrid();
            modeRow.Controls.Add(listMode);

            var gridCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(6)) };
            grid.Dock = DockStyle.Fill;
            grid.VirtualMode = true;
            grid.ReadOnly = true;
            grid.AddText("", "swatch", 26);
            grid.AddText("Name", "name", fill: true);
            grid.AddText("Size", "size", 84, right: true);
            grid.AddText("Where", "where", 92, right: true);
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.CellPainting += OnCellPainting;
            grid.SelectionChanged += (s, e) =>
            {
                if (syncing || listMode.SelectedIndex != 0) return;
                var row = grid.CurrentRow?.Index ?? -1;
                canvas.Selected = segments.FindIndex(x => x.Index == row);
            };
            grid.CellClick += (s, e) =>
            {
                if (listMode.SelectedIndex != 0 || e.RowIndex < 0) return;
                var seg = RowSegment(e.RowIndex);
                if (seg != null) canvas.FocusSegment(segments.IndexOf(seg));
            };
            grid.CellDoubleClick += (s, e) =>
            {
                if (listMode.SelectedIndex != 0 || e.RowIndex < 0) return;
                var seg = RowSegment(e.RowIndex);
                if (seg?.FilePath != null && State.Disc != null) OpenFile(State.Disc.Drive + ":\\" + seg.FilePath);
            };
            gridCard.Controls.Add(grid);

            side.Controls.Add(gridCard);
            side.Controls.Add(modeRow);
            side.Controls.Add(summary);

            canvas.SegmentClicked += i => SelectRow(i, true);
            canvas.SegmentHovered += i =>
            {
                if (i < 0) return;
                if (hoverSync.Enabled) { hoverPending = i; return; }
                SelectRow(i, false);
                hoverSync.Start();
            };
            hoverSync.Tick += (s, e) =>
            {
                hoverSync.Stop();
                if (hoverPending < 0) return;
                SelectRow(hoverPending, false);
                hoverPending = -1;
                hoverSync.Start();
            };

            Controls.Add(canvas);
            Controls.Add(side);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hoverSync.Dispose();
            base.Dispose(disposing);
        }

        public override string Title => "Disc Visualizer";

        public override string Subtitle
        {
            get
            {
                var d = State.Disc;
                if (d == null) return State.Loading ? "Reading disc…" : State.Message;
                return d.DisplayTitle + (d.AlbumArtist.Length > 0 ? " · " + d.AlbumArtist : "") + "  —  the physical layout, from the hub outward";
            }
        }

        public override bool HandleKey(Keys key)
        {
            switch (key)
            {
                case Keys.Oemplus: case Keys.Add: case Keys.Shift | Keys.Oemplus: canvas.ZoomBy(1.6); return true;
                case Keys.OemMinus: case Keys.Subtract: canvas.ZoomBy(1 / 1.6); return true;
                case Keys.D0: case Keys.NumPad0: case Keys.Home: canvas.ResetView(); return true;
                case Keys.Enter:
                    if (canvas.Selected >= 0) { canvas.FocusSegment(canvas.Selected); return true; }
                    return false;
            }
            return false;
        }

        void SelectRow(int segIndex, bool select)
        {
            if (listMode.SelectedIndex != 0 || segIndex < 0 || segIndex >= segments.Count) return;
            int row = segments[segIndex].Index;
            if (row < 0 || row >= grid.RowCount) return;
            syncing = true;
            try
            {
                grid.CurrentCell = grid.Rows[row].Cells[1];
                if (select) canvas.Selected = segIndex;
            }
            catch { }
            syncing = false;
        }

        public override void OnDiscChanged()
        {
            var d = State.Disc;
            bool fresh = !ReferenceEquals(d, shown);
            shown = d;
            hoverPending = -1;
            canvas.EmptyText = State.Loading ? "Reading disc…" : State.Disc == null ? (State.Message.Length > 0 ? State.Message : "Insert a disc") : "";
            BuildSegments(d);
            canvas.SetDisc(d, segments, UsedSectors(d), fresh && d != null);
            listMode.Visible = d?.FileSystem != null && d.FileSystem.HasPositions;
            if (!listMode.Visible) listMode.SelectedIndex = 0;
            UpdateSummary(d);
            FillGrid();
        }

        static long UsedSectors(DiscInfo d)
        {
            if (d == null) return 0;
            if (d.Tracks.Count > 0) return d.LeadOutLba;
            if (d.Media != null && d.Media.TotalSectors > 0) return d.Media.TotalSectors - d.Media.FreeSectors;
            return 0;
        }

        void BuildSegments(DiscInfo d)
        {
            segments = new List<DiscSegment>();
            legend = new List<(Color, string, int, long)>();
            if (d == null) return;

            int row = 0;
            int audioIndex = 0;
            foreach (var t in d.Tracks)
            {
                if (t.IsAudio)
                {
                    var color = Theme.Palette[audioIndex++ % Theme.Palette.Length];
                    string name = t.Title.Length > 0 ? t.Title : "Audio track";
                    segments.Add(new DiscSegment
                    {
                        Start = t.StartLba, End = t.EndLba, Color = color, Index = row++,
                        Title = $"{t.Number:00}  {name}",
                        Detail = $"{Fmt.Time(t.Duration)} · {Fmt.Bytes(t.Bytes)}\nStarts at {Fmt.Msf(t.StartLba)} (sector {t.StartLba:N0})"
                                 + (t.Artist.Length > 0 && t.Artist != d.AlbumArtist ? "\n" + t.Artist : "")
                                 + (t.PreEmphasis ? "\nPre-emphasis" : ""),
                    });
                    continue;
                }

                var fs = d.FileSystem;
                if (fs == null || !fs.HasPositions)
                {
                    segments.Add(new DiscSegment
                    {
                        Start = t.StartLba, End = t.EndLba, Color = Theme.Palette[6], Index = row++,
                        Title = $"Data track {t.Number}" + (fs != null && fs.VolumeLabel.Length > 0 ? $" · {fs.VolumeLabel}" : ""),
                        Detail = $"{Fmt.Bytes((long)t.Sectors * 2048)} · {fs?.Kind ?? "unknown file system"}" + (fs != null ? "\nFile positions aren't available for this file system." : ""),
                    });
                    continue;
                }

                // Files, coloured by type (largest types get their own colour, like WinDirStat).
                var files = fs.Entries.Where(e => !e.IsDirectory && e.Size > 0 && e.Lba >= t.StartLba && e.Lba < t.EndLba).OrderBy(e => e.Lba).ToList();
                var byExt = files.GroupBy(f => f.Extension.Length > 0 ? f.Extension : "(none)")
                                 .Select(gp => (ext: gp.Key, count: gp.Count(), bytes: gp.Sum(f => f.Size)))
                                 .OrderByDescending(x => x.bytes).ToList();
                var colors = new Dictionary<string, Color>();
                for (int i = 0; i < byExt.Count; i++)
                {
                    var col = i < Theme.Palette.Length - 1 ? Theme.Palette[i] : Color.FromArgb(0x8A, 0x90, 0x99);
                    colors[byExt[i].ext] = col;
                }
                foreach (var gp in byExt.Take(Theme.Palette.Length - 1)) legend.Add((colors[gp.ext], gp.ext, gp.count, gp.bytes));
                if (byExt.Count >= Theme.Palette.Length)
                {
                    var rest = byExt.Skip(Theme.Palette.Length - 1).ToList();
                    legend.Add((Color.FromArgb(0x8A, 0x90, 0x99), $"{rest.Count} other types", rest.Sum(x => x.count), rest.Sum(x => x.bytes)));
                }

                var sys = new DiscSegment { Start = t.StartLba, End = t.StartLba + 16, Color = Color.FromArgb(0x5A, 0x60, 0x6A), Title = "System area", Detail = "16 reserved sectors (boot code on bootable discs)." };
                segments.Add(sys);
                var dirs = fs.Entries.Where(e => e.IsDirectory && e.Lba >= 0).ToList();
                foreach (var f in files)
                {
                    long end = f.Lba + Math.Max(1, f.Sectors);
                    if (segments.Count > 0 && segments[segments.Count - 1].End > f.Lba) segments[segments.Count - 1].End = Math.Max(segments[segments.Count - 1].Start + 1, f.Lba);
                    segments.Add(new DiscSegment
                    {
                        Start = f.Lba, End = end, Color = colors[f.Extension.Length > 0 ? f.Extension : "(none)"], Index = row++,
                        Title = f.Name,
                        FilePath = f.Path,
                        Detail = $"{Fmt.Bytes(f.Size)} · sectors {f.Lba:N0}–{end - 1:N0}\n{System.IO.Path.GetDirectoryName(f.Path)}".TrimEnd('\n'),
                    });
                }
            }
            segments = segments.OrderBy(x => x.Start).ToList();
            rows = segments.Where(x => x.Index >= 0).OrderBy(x => x.Index).ToArray();
        }

        void UpdateSummary(DiscInfo d)
        {
            if (d == null)
            {
                summaryTitle.Text = State.Loading ? "Reading…" : "No disc";
                summaryText.Text = State.Loading ? "" : "Insert a CD or DVD to see how it's laid out.\n\nAudio tracks and files appear as rings at their real position on the spiral, from the hub outward.";
                FitSummary();
                return;
            }
            var geo = canvas.Geometry;
            var lines = new List<string>();
            string kind = d.Media != null && d.Media.Kind != MediaKind.Unknown ? d.Media.Name : d.IsDvd ? "DVD" : "CD";
            summaryTitle.Text = d.HasAudio && d.HasData ? $"Mixed-mode {kind}" : d.HasAudio ? $"Audio {kind}" : d.HasData ? $"Data {kind}" : d.Media?.Blank == true ? $"Blank {kind}" : kind;

            long used = UsedSectors(d);
            double pct = geo.CapacitySectors > 0 ? 100.0 * used / geo.CapacitySectors : 0;
            if (d.HasAudio) lines.Add($"{d.AudioTracks.Count()} audio tracks · {Fmt.Time(d.AudioDuration)}");
            if (d.FileSystem != null) lines.Add($"{d.FileSystem.FileCount:N0} files · {Fmt.Bytes(d.FileSystem.TotalFileBytes)} · {d.FileSystem.Kind}");
            if (d.Tracks.Count > 0) lines.Add($"Spiral ends at {geo.Radius(used):0.0} mm of 58 mm ({pct:0}% of a {(geo.Dvd ? Fmt.Bytes(geo.CapacitySectors * 2048) : Fmt.Time(TimeSpan.FromSeconds(geo.CapacitySectors / 75.0)))} disc)");
            if (d.SessionCount > 1) lines.Add($"{d.SessionCount} sessions");
            if (d.Media != null && d.Media.Writable)
                lines.Add(d.Media.Blank ? $"Blank and ready to burn · {Fmt.Bytes(d.Media.FreeSectors * 2048)} free" : d.Media.Appendable ? $"Open for more sessions · {Fmt.Bytes(d.Media.FreeSectors * 2048)} free" : "Closed");
            if (d.HasCdText) lines.Add("Has CD-Text");
            if (d.Tracks.Any(t => t.PreEmphasis)) lines.Add("Some tracks use pre-emphasis");
            summaryText.Text = string.Join("\n", lines);
            FitSummary();
        }

        void FitSummary()
        {
            int textH = TextRenderer.MeasureText(summaryText.Text.Length > 0 ? summaryText.Text : " ", summaryText.Font,
                new Size(Math.Max(10, side.Width - side.Padding.Left - Theme.S(32)), 0), TextFormatFlags.WordBreak).Height;
            summary.Height = summary.Padding.Vertical + summaryTitle.Height + textH + Theme.S(6);
        }

        void FillGrid()
        {
            grid.Rows.Clear();
            bool layout = listMode.SelectedIndex == 0;
            grid.Columns["where"].Visible = layout;
            grid.Columns["name"].HeaderText = layout ? (State.Disc?.HasAudio == true && State.Disc?.HasData != true ? "Track" : "Name") : "Type";
            grid.Columns["where"].HeaderText = "Starts";
            grid.Columns["size"].HeaderText = layout ? (State.Disc?.HasAudio == true && State.Disc?.HasData != true ? "Length" : "Size") : "Size";
            grid.RowCount = layout ? rows.Length : legend.Count;
            grid.Invalidate();
        }

        /// <summary>Opens a file from the disc with its default app. Shell calls can stall while the drive spins up, so it runs on its own STA thread.</summary>
        void OpenFile(string path)
        {
            var t = new System.Threading.Thread(() =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    try { BeginInvoke(new Action(() => Dialogs.Error(Owner, "Couldn't open the file", $"{path}\n\n{ex.Message}"))); }
                    catch (InvalidOperationException) { }
                }
            }) { IsBackground = true };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        DiscSegment RowSegment(int row) => row >= 0 && row < rows.Length ? rows[row] : null;

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (listMode.SelectedIndex == 0)
            {
                var d = State.Disc;
                var seg = RowSegment(e.RowIndex);
                if (seg == null || d == null) return;
                switch (grid.Columns[e.ColumnIndex].Name)
                {
                    case "name": e.Value = seg.Title; break;
                    case "size":
                        e.Value = d.HasAudio && seg.Title.Length > 2 && char.IsDigit(seg.Title[0]) ? Fmt.Time(TimeSpan.FromSeconds((seg.End - seg.Start) / 75.0)) : Fmt.Bytes((seg.End - seg.Start) * 2048);
                        break;
                    case "where": e.Value = $"{canvas.Geometry.Radius(seg.Start):0.0} mm"; break;
                }
            }
            else if (e.RowIndex < legend.Count)
            {
                var l = legend[e.RowIndex];
                switch (grid.Columns[e.ColumnIndex].Name)
                {
                    case "name": e.Value = $"{l.name}  ·  {l.count:N0} files"; break;
                    case "size": e.Value = Fmt.Bytes(l.bytes); break;
                }
            }
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            Color? color = listMode.SelectedIndex == 0 ? RowSegment(e.RowIndex)?.Color : e.RowIndex < legend.Count ? legend[e.RowIndex].color : (Color?)null;
            if (color.HasValue)
            {
                Theme.Hq(e.Graphics);
                float s = Theme.S(12f);
                Theme.FillRound(e.Graphics, color.Value, new RectangleF(e.CellBounds.X + (e.CellBounds.Width - s) / 2, e.CellBounds.Y + (e.CellBounds.Height - s) / 2, s, s), Theme.S(3f));
            }
            e.Handled = true;
        }
    }
}
