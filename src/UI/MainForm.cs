using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Discphew.Disc;
using Discphew.Native;

namespace Discphew.UI
{
    abstract class ToolView : UserControl
    {
        protected readonly AppState State;

        protected ToolView(AppState state)
        {
            State = state;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            DoubleBuffered = true;
        }

        public abstract string Title { get; }
        public virtual string Subtitle => "";
        public virtual void OnDiscChanged() { }
        public virtual void OnActivated() { }
        public virtual bool HandleKey(Keys key) => false;
        protected Form Owner => FindForm();
    }

    sealed class NavItem : Painted
    {
        readonly Icons.Painter icon;
        bool selected;

        public NavItem(string text, Icons.Painter icon)
        {
            Text = text;
            this.icon = icon;
            Height = Theme.S(42);
            Cursor = Cursors.Hand;
        }

        public bool Selected { get => selected; set { selected = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            if (selected) Theme.FillRound(g, Theme.Surface, r, Theme.S(9f));
            else if (Hover) Theme.FillRound(g, Theme.WithAlpha(Theme.Surface, 140), r, Theme.S(9f));
            if (selected) Theme.FillRound(g, Theme.Accent, new RectangleF(0, Theme.S(11), Theme.S(3), Height - Theme.S(22)), Theme.S(2f));
            int s = Theme.S(18);
            icon(g, new RectangleF(Theme.S(14), (Height - s) / 2f, s, s), selected ? Theme.Accent : Theme.Muted);
            Theme.DrawText(g, Text, selected ? Theme.Strong : Theme.Body, selected ? Theme.Text : Theme.Muted, new Rectangle(Theme.S(44), 0, Width - Theme.S(48), Height));
        }
    }

    sealed class Logo : Painted
    {
        public Logo() { Height = Theme.S(76); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            int s = Theme.S(30);
            var r = new RectangleF(Theme.S(22), (Height - s) / 2f, s, s);
            DiscArt.DrawMiniDisc(g, r);
            Theme.DrawText(g, "discphew", Theme.Heading, Theme.Text, new Rectangle((int)r.Right + Theme.S(12), 0, Width, Height));
        }
    }

    sealed class MainForm : Form
    {
        readonly AppState state;
        readonly List<(NavItem nav, ToolView view)> tools = new List<(NavItem, ToolView)>();
        readonly Panel host;
        readonly Caption title, subtitle;
        readonly Dropdown driveBox;
        readonly FlatButton ejectButton, reloadButton;
        readonly Label driveStatus;
        readonly Timer deviceDebounce = new Timer { Interval = 800 };
        ToolView current;

        public MainForm()
        {
            Text = "discphew";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(1000), Theme.S(660));
            Size = new Size(Theme.S(1280), Theme.S(820));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            state = new AppState(this);

            // ---- sidebar
            var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(240), BackColor = Theme.Sidebar };
            var logo = new Logo { Dock = DockStyle.Top };
            var navPanel = new Panel { Dock = DockStyle.Top, Height = Theme.S(4 * 46 + 16), BackColor = Theme.Sidebar, Padding = new Padding(Theme.S(12), Theme.S(8), Theme.S(12), 0) };

            var drivePanel = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(196), BackColor = Theme.Sidebar };
            var driveLabel = new Caption("DRIVE", Theme.Small, Theme.Faint) { Bounds = new Rectangle(Theme.S(16), Theme.S(8), Theme.S(200), Theme.S(20)) };
            driveBox = new Dropdown { Bounds = new Rectangle(Theme.S(14), Theme.S(32), Theme.S(240 - 28 - 42 - 6), Theme.S(36)) };
            driveBox.Format = o => o is char c ? state.DriveName(c) : "";
            driveBox.SelectedIndexChanged += (s, e) => { if (driveBox.SelectedItem is char c) state.SelectDrive(c); };
            ejectButton = new FlatButton("", Icons.Eject) { Bounds = new Rectangle(driveBox.Right + Theme.S(6), Theme.S(32), Theme.S(42), Theme.S(36)) };
            ejectButton.Click += (s, e) => Eject();
            driveStatus = new Label
            {
                Bounds = new Rectangle(Theme.S(16), Theme.S(76), Theme.S(240 - 32), Theme.S(64)),
                ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = Theme.Small, UseMnemonic = false,
            };
            reloadButton = new FlatButton("Re-read disc", Icons.Refresh) { Subtle = true, IconSize = 14, Font = Theme.Small, Height = Theme.S(30) };
            reloadButton.Width = reloadButton.PreferredWidth;
            reloadButton.Location = new Point(Theme.S(8), Theme.S(146));
            reloadButton.Click += (s, e) => state.Reload();
            drivePanel.Controls.AddRange(new Control[] { driveLabel, driveBox, ejectButton, driveStatus, reloadButton });

            sidebar.Controls.Add(navPanel);
            sidebar.Controls.Add(logo);
            sidebar.Controls.Add(drivePanel);

            // ---- content
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.S(32), Theme.S(18), Theme.S(32), Theme.S(24)) };
            var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(78), BackColor = Theme.Bg };
            title = new Caption("", Theme.Title, Theme.Text) { Bounds = new Rectangle(0, Theme.S(6), Theme.S(900), Theme.S(40)), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            subtitle = new Caption("", Theme.Body, Theme.Muted) { Bounds = new Rectangle(Theme.S(2), Theme.S(46), Theme.S(900), Theme.S(24)), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Resize += (s, e) => { title.Width = header.Width; subtitle.Width = header.Width; };
            host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            content.Controls.Add(host);
            content.Controls.Add(header);

            Controls.Add(content);
            Controls.Add(sidebar);

            AddTool(navPanel, "Visualize", Icons.Disc, new VisualizerView(state));
            AddTool(navPanel, "Burn", Icons.Flame, new BurnerView(state));
            AddTool(navPanel, "Rip", Icons.Rip, new RipperView(state));
            AddTool(navPanel, "Play", Icons.Note, new PlayerView(state));

            state.Changed += OnStateChanged;
            state.LabelsChanged += () => driveBox.Invalidate();
            deviceDebounce.Tick += (s, e) => { deviceDebounce.Stop(); if (!state.Busy) { RefreshDriveList(); state.DrivesChanged(); } };

            int saved = Settings.GetInt("tool", 0);
            Show(tools[Math.Max(0, Math.Min(tools.Count - 1, saved))].view);
        }

        void AddTool(Panel navPanel, string name, Icons.Painter icon, ToolView view)
        {
            var nav = new NavItem(name, icon)
            {
                Bounds = new Rectangle(Theme.S(12), Theme.S(8) + tools.Count * Theme.S(46), Theme.S(240 - 24), Theme.S(42)),
            };
            nav.Click += (s, e) => Show(view);
            navPanel.Controls.Add(nav);
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            host.Controls.Add(view);
            tools.Add((nav, view));
        }

        void Show(ToolView view)
        {
            if (current == view) return;
            current = view;
            foreach (var (nav, v) in tools)
            {
                nav.Selected = v == view;
                v.Visible = v == view;
            }
            Settings.Set("tool", tools.FindIndex(t => t.view == view));
            view.OnActivated();
            UpdateHeader();
        }

        public void ShowTool<T>() where T : ToolView
        {
            var t = tools.FirstOrDefault(x => x.view is T);
            if (t.view != null) Show(t.view);
        }

        public T Tool<T>() where T : ToolView => (T)tools.First(x => x.view is T).view;

        public void UpdateHeader()
        {
            if (current == null) return;
            title.Text = current.Title;
            subtitle.Text = current.Subtitle;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshDriveList();
            state.PickInitialDrive();
        }

        void RefreshDriveList()
        {
            var drives = state.Drives;
            driveBox.SetItems(drives.Cast<object>(), Math.Max(0, Array.IndexOf(drives, state.Drive)));
        }

        void OnStateChanged()
        {
            int idx = driveBox.Items.IndexOf(state.Drive);
            if (idx < 0 && state.Drive != '\0') { RefreshDriveList(); idx = driveBox.Items.IndexOf(state.Drive); }
            if (idx >= 0 && idx != driveBox.SelectedIndex) driveBox.SetItems(driveBox.Items.ToList(), idx);

            var d = state.Disc;
            string status;
            if (state.Busy) status = state.BusyReason;
            else if (state.Loading) status = "Reading disc…";
            else if (d == null) status = state.Message;
            else
            {
                var bits = new List<string>();
                var mediaName = d.Media != null && d.Media.Kind != MediaKind.Unknown ? d.Media.Name : d.IsDvd ? "DVD" : d.Tracks.Count > 0 ? "CD" : "Disc";
                bits.Add(d.HasAudio && !d.HasData ? $"Audio {mediaName}" : d.HasAudio ? $"Mixed {mediaName}" : d.HasData ? $"Data {mediaName}" : state.Message.Length > 0 ? state.Message : mediaName);
                if (d.HasAudio) bits.Add($"{d.AudioTracks.Count()} tracks · {Fmt.Time(d.AudioDuration)}");
                if (d.FileSystem != null) bits.Add($"{d.FileSystem.FileCount:N0} files · {Fmt.Bytes(d.FileSystem.TotalFileBytes)}");
                if (d.Media?.RecorderName.Length > 0) bits.Add(d.Media.RecorderName);
                else if (state.MediaLoading) bits.Add("Checking drive…");
                status = string.Join("\n", bits);
            }
            driveStatus.Text = status;
            driveBox.Enabled = ejectButton.Enabled = reloadButton.Enabled = !state.Busy;

            foreach (var (_, v) in tools) v.OnDiscChanged();
            UpdateHeader();
        }

        void Eject()
        {
            if (state.Busy || state.Drive == '\0') return;
            var letter = state.Drive;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { CdDrive.Eject(letter); }
                catch (Exception ex) { BeginInvoke(new Action(() => Dialogs.Error(this, "Couldn't eject", ex.Message))); }
            });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!(ActiveControl is TextBoxBase) && !(FocusedEditing()) && current != null && current.HandleKey(e.KeyData))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        bool FocusedEditing()
        {
            Control c = ActiveControl;
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            return c is TextBoxBase || c is DataGridView dg && dg.IsCurrentCellInEditMode;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_DEVICECHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == Win32.DBT_DEVICEARRIVAL || ev == Win32.DBT_DEVICEREMOVECOMPLETE)
                {
                    deviceDebounce.Stop();
                    deviceDebounce.Start();
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (state.Busy && !Dialogs.Confirm(this, "Still working", state.BusyReason + "\n\nQuit anyway? An unfinished burn can leave the disc unusable.", "Quit", true))
            {
                e.Cancel = true;
                return;
            }
            foreach (var (_, v) in tools) v.Dispose();
            base.OnFormClosing(e);
        }
    }
}
