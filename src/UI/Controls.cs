using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Discphew.Native;

namespace Discphew.UI
{
    /// <summary>Base for owner-drawn controls: double buffered, transparent-friendly.</summary>
    class Painted : Control
    {
        protected bool Hover, Down;

        public Painted()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    sealed class FlatButton : Painted
    {
        public Icons.Painter Icon;
        public bool Primary;
        public bool Danger;
        public bool Subtle;          // no background until hovered
        public bool Circle;
        public int IconSize = 16;

        public FlatButton(string text = "", Icons.Painter icon = null)
        {
            Text = text;
            Icon = icon;
            Cursor = Cursors.Hand;
            Height = Theme.S(34);
            TabStop = true;
            Width = PreferredWidth;
        }

        public int PreferredWidth
        {
            get
            {
                int w = Theme.S(28);
                if (!string.IsNullOrEmpty(Text)) w += TextRenderer.MeasureText(Text, Font).Width;
                if (Icon != null) w += Theme.S(IconSize) + (string.IsNullOrEmpty(Text) ? 0 : Theme.S(8));
                if (string.IsNullOrEmpty(Text) && Icon != null) w = Height;
                return w;
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color bg, fg;
            if (Primary || Danger)
            {
                var baseColor = Danger ? Theme.Danger : Theme.Accent;
                bg = !Enabled ? Theme.Surface : Down ? Theme.Mix(baseColor, Color.Black, 0.15) : Hover ? Theme.Mix(baseColor, Color.White, 0.15) : baseColor;
                fg = !Enabled ? Theme.Faint : Theme.AccentText;
            }
            else
            {
                bg = !Enabled ? (Subtle ? Color.Transparent : Theme.Surface) : Down ? Theme.SurfacePressed : Hover ? Theme.SurfaceHover : Subtle ? Color.Transparent : Theme.Surface;
                fg = Enabled ? ForeColor : Theme.Faint;
            }
            float radius = Circle ? Height / 2f : Theme.S(8f);
            if (bg.A > 0)
            {
                if (Circle) using (var b = new SolidBrush(bg)) g.FillEllipse(b, r);
                else Theme.FillRound(g, bg, r, radius);
            }
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, Theme.WithAlpha(Theme.Accent, 160), new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), radius, 1.5f);

            int iconPx = Theme.S(IconSize);
            int textW = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int gap = Icon != null && textW > 0 ? Theme.S(8) : 0;
            int total = (Icon != null ? iconPx : 0) + gap + textW;
            int x = (Width - total) / 2;
            if (Icon != null)
            {
                Icon(g, new RectangleF(x, (Height - iconPx) / 2f, iconPx, iconPx), fg);
                x += iconPx + gap;
            }
            if (textW > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textW + 2, Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    sealed class Segmented : Painted
    {
        string[] items = new string[0];
        int selected;
        int hoverIndex = -1;
        public event EventHandler SelectedIndexChanged;

        public Segmented(params string[] items)
        {
            this.items = items;
            Height = Theme.S(34);
            Cursor = Cursors.Hand;
            Width = items.Sum(i => TextRenderer.MeasureText(i, Theme.Body).Width + Theme.S(36)) + Theme.S(8);
        }

        public int SelectedIndex
        {
            get => selected;
            set
            {
                if (value == selected) return;
                selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        RectangleF Item(int i)
        {
            float pad = Theme.S(4f);
            float w = (Width - pad * 2) / items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - pad * 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < items.Length; i++) if (Item(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int i = 0; i < items.Length; i++)
                if (Item(i).Contains(e.Location)) SelectedIndex = i;
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            Theme.FillRound(g, Theme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(9f));
            for (int i = 0; i < items.Length; i++)
            {
                var r = Item(i);
                if (i == selected) Theme.FillRound(g, Theme.SurfacePressed, r, Theme.S(7f));
                else if (i == hoverIndex) Theme.FillRound(g, Theme.SurfaceHover, r, Theme.S(7f));
                var color = !Enabled ? Theme.Faint : i == selected ? Theme.Text : Theme.Muted;
                TextRenderer.DrawText(g, items[i], i == selected ? Theme.Strong : Theme.Body, Rectangle.Round(r), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    sealed class CapacityBar : Painted
    {
        public long Capacity = 1, Used, Planned;
        public string LeftText = "", RightText = "";
        public Color PlannedColor = Theme.Accent;

        public CapacityBar() { Height = Theme.S(46); }

        public bool Over => Used + Planned > Capacity;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            int labelH = Theme.S(20);
            Theme.DrawText(g, LeftText, Theme.Body, Theme.Text, new Rectangle(0, 0, Width, labelH));
            Theme.DrawText(g, RightText, Theme.Body, Over ? Theme.Danger : Theme.Muted, new Rectangle(0, 0, Width, labelH),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var bar = new RectangleF(0, labelH + Theme.S(8), Width - 1, Theme.S(10));
            Theme.FillRound(g, Theme.Surface, bar, bar.Height / 2);
            double cap = Math.Max(1, Capacity);
            float usedW = (float)(bar.Width * Math.Min(1, Used / cap));
            float planW = (float)(bar.Width * Math.Min(1, (Used + Planned) / cap)) - usedW;
            var state = g.Save();
            using (var clip = Theme.Round(bar, bar.Height / 2)) g.SetClip(clip);
            using (var b = new SolidBrush(Theme.Faint)) g.FillRectangle(b, bar.X, bar.Y, usedW, bar.Height);
            using (var b = new SolidBrush(Over ? Theme.Danger : PlannedColor)) g.FillRectangle(b, bar.X + usedW, bar.Y, planW, bar.Height);
            g.Restore(state);
        }
    }

    sealed class ProgressStrip : Painted
    {
        double value = -1;
        readonly Timer anim = new Timer { Interval = 30 };
        float phase;
        public Color BarColor = Theme.Accent;

        public ProgressStrip()
        {
            Height = Theme.S(6);
            anim.Tick += (s, e) => { phase = (phase + 0.012f) % 1.4f; Invalidate(); };
        }

        /// <summary>0..1, or negative for "working…" animation.</summary>
        public double Value
        {
            get => value;
            set
            {
                this.value = value;
                anim.Enabled = value < 0 && Visible;
                Invalidate();
            }
        }

        protected override void OnVisibleChanged(EventArgs e) { anim.Enabled = value < 0 && Visible; base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Surface, r, r.Height / 2);
            var state = g.Save();
            using (var clip = Theme.Round(r, r.Height / 2)) g.SetClip(clip);
            using var b = new SolidBrush(BarColor);
            if (value >= 0) g.FillRectangle(b, 0, 0, (float)(r.Width * Math.Min(1, value)), r.Height);
            else g.FillRectangle(b, (phase - 0.4f) * r.Width, 0, r.Width * 0.3f, r.Height);
            g.Restore(state);
        }
    }

    sealed class Slider : Painted
    {
        double value;
        public bool Dragging { get; private set; }
        public double Buffered = -1;
        public event Action<double> Scrubbed;      // while dragging
        public event Action<double> Committed;     // on release

        public Slider() { Height = Theme.S(20); Cursor = Cursors.Hand; }

        public double Value
        {
            get => value;
            set { if (!Dragging) { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        }

        double At(int x) => Math.Max(0, Math.Min(1, (x - Height / 2.0) / Math.Max(1, Width - Height)));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                Dragging = true; Capture = true;
                value = At(e.X); Scrubbed?.Invoke(value);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Dragging) { value = At(e.X); Scrubbed?.Invoke(value); Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (Dragging) { Dragging = false; Capture = false; value = At(e.X); Committed?.Invoke(value); }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            float h = Theme.S(4f), cy = Height / 2f, x0 = Height / 2f, x1 = Width - Height / 2f;
            var track = new RectangleF(x0, cy - h / 2, x1 - x0, h);
            Theme.FillRound(g, Theme.SurfaceHover, track, h / 2);
            float vx = x0 + (float)(value * (x1 - x0));
            Theme.FillRound(g, Enabled ? (Hover || Dragging ? Theme.Accent : Theme.Text) : Theme.Faint, new RectangleF(x0, cy - h / 2, vx - x0, h), h / 2);
            if (Enabled && (Hover || Dragging))
            {
                float k = Theme.S(12f);
                using var b = new SolidBrush(Theme.Text);
                g.FillEllipse(b, vx - k / 2, cy - k / 2, k, k);
            }
        }
    }

    sealed class CheckField : Painted
    {
        bool isChecked;
        public event EventHandler CheckedChanged;

        public CheckField(string text, bool initial = false)
        {
            Text = text;
            isChecked = initial;
            Height = Theme.S(28);
            Cursor = Cursors.Hand;
            TabStop = true;
            Width = TextRenderer.MeasureText(text, Theme.Body).Width + Theme.S(34);
        }

        public bool Checked
        {
            get => isChecked;
            set { if (value != isChecked) { isChecked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            float s = Theme.S(18f);
            var box = new RectangleF(1, (Height - s) / 2, s, s);
            if (isChecked)
            {
                Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Faint, box, Theme.S(5f));
                Icons.Check(g, box, Theme.AccentText);
            }
            else
            {
                Theme.FillRound(g, Hover ? Theme.SurfaceHover : Theme.Surface, box, Theme.S(5f));
                Theme.DrawRound(g, Theme.Border, box, Theme.S(5f));
            }
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, RectangleF.Inflate(box, 2, 2), Theme.S(6f));
            Theme.DrawText(g, Text, Font, Enabled ? Theme.Text : Theme.Faint, new Rectangle((int)(s + Theme.S(10)), 0, Width - (int)s - Theme.S(10), Height));
        }
    }

    /// <summary>A themed single-line text box (TextBox inside a rounded frame).</summary>
    sealed class TextField : Painted
    {
        public readonly TextBox Box;

        public TextField(string placeholder = "")
        {
            Height = Theme.S(34);
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.Body,
            };
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.TextChanged += (s, e) => OnTextChanged(e);
            Placeholder = placeholder;
            Cursor = Cursors.IBeam;
        }

        public string Placeholder
        {
            set
            {
                void Apply()
                {
                    var p = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(value ?? "");
                    Win32.SendMessage(Box.Handle, 0x1501, (IntPtr)1, p);     // EM_SETCUEBANNER (copies the text)
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
                }
                if (Box.IsHandleCreated) Apply();
                else Box.HandleCreated += (s, e) => Apply();
            }
        }

        public override string Text { get => Box.Text; set => Box.Text = value; }
        public bool ReadOnly { get => Box.ReadOnly; set => Box.ReadOnly = value; }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int pad = Theme.S(10);
            if (Box == null) { base.OnLayout(e); return; }
            Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2 + 1, Width - pad * 2, Box.PreferredHeight);
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Surface, r, Theme.S(8f));
            Theme.DrawRound(g, Box.Focused ? Theme.Accent : Theme.Border, r, Theme.S(8f), Box.Focused ? 1.5f : 1f);
        }
    }

    /// <summary>A themed drop-down: a button that opens a dark menu of choices.</summary>
    sealed class Dropdown : Painted
    {
        readonly List<object> items = new List<object>();
        int selected = -1;
        public event EventHandler SelectedIndexChanged;
        public Func<object, string> Format = o => o?.ToString() ?? "";
        public Func<object, bool> IsEnabled = o => true;

        public Dropdown() { Height = Theme.S(34); Cursor = Cursors.Hand; TabStop = true; }

        public IList<object> Items => items;

        public void SetItems(IEnumerable<object> list, int select = 0)
        {
            items.Clear();
            items.AddRange(list);
            selected = items.Count == 0 ? -1 : Math.Max(0, Math.Min(select, items.Count - 1));
            Invalidate();
        }

        public int SelectedIndex
        {
            get => selected;
            set
            {
                if (value == selected) return;
                selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object SelectedItem => selected >= 0 && selected < items.Count ? items[selected] : null;

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled || items.Count == 0) return;
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Body, BackColor = Theme.Panel };
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var mi = new ToolStripMenuItem(Format(items[i])) { ForeColor = Theme.Text, Enabled = IsEnabled(items[i]), Checked = i == selected, Padding = new Padding(Theme.S(4)) };
                mi.Click += (s, a) => SelectedIndex = index;
                menu.Items.Add(mi);
            }
            menu.MinimumSize = new Size(Width, 0);
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(this, new Point(0, Height + Theme.S(2)));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            else if (e.KeyCode == Keys.Down && selected < items.Count - 1) SelectedIndex++;
            else if (e.KeyCode == Keys.Up && selected > 0) SelectedIndex--;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, Down ? Theme.SurfacePressed : Hover ? Theme.SurfaceHover : Theme.Surface, r, Theme.S(8f));
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, r, Theme.S(8f), 1.5f);
            int chevron = Theme.S(14);
            Theme.DrawText(g, SelectedItem == null ? "" : Format(SelectedItem), Font, Enabled ? Theme.Text : Theme.Faint,
                new Rectangle(Theme.S(10), 0, Width - Theme.S(20) - chevron, Height));
            Icons.Down(g, new RectangleF(Width - Theme.S(10) - chevron, (Height - chevron) / 2f, chevron, chevron), Theme.Muted);
        }
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new RectangleF(e.ImageRectangle.X, e.ImageRectangle.Y, e.ImageRectangle.Width, e.ImageRectangle.Height);
            Theme.Hq(e.Graphics);
            Icons.Check(e.Graphics, r, Theme.Accent);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Panel;
            public override Color MenuBorder => Theme.Border;
            public override Color MenuItemBorder => Theme.SurfaceHover;
            public override Color MenuItemSelected => Theme.SurfaceHover;
            public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
            public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
            public override Color MenuItemPressedGradientBegin => Theme.SurfacePressed;
            public override Color MenuItemPressedGradientEnd => Theme.SurfacePressed;
            public override Color ImageMarginGradientBegin => Theme.Panel;
            public override Color ImageMarginGradientMiddle => Theme.Panel;
            public override Color ImageMarginGradientEnd => Theme.Panel;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
            public override Color CheckBackground => Theme.Panel;
            public override Color CheckSelectedBackground => Theme.SurfaceHover;
            public override Color CheckPressedBackground => Theme.SurfaceHover;
        }
    }

    /// <summary>DataGridView pre-styled for the dark theme.</summary>
    sealed class DarkGrid : DataGridView
    {
        public DarkGrid()
        {
            DoubleBuffered = true;
            BackgroundColor = Theme.Panel;
            BorderStyle = BorderStyle.None;
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            GridColor = Theme.Border;
            EnableHeadersVisualStyles = false;
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ColumnHeadersHeight = Theme.S(32);
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Panel, ForeColor = Theme.Muted, Font = Theme.Small,
                SelectionBackColor = Theme.Panel, SelectionForeColor = Theme.Muted,
                Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0),
            };
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Body,
                SelectionBackColor = Theme.SurfacePressed, SelectionForeColor = Theme.Text,
                Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0),
            };
            RowHeadersVisible = false;
            RowTemplate.Height = Theme.S(32);
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToOrderColumns = false;
            MultiSelect = false;
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            Theme.DarkScrollbars(this);
            Theme.DarkScrollbars(VerticalScrollBar);
            Theme.DarkScrollbars(HorizontalScrollBar);
            EditingControlShowing += (s, e) =>
            {
                e.Control.BackColor = Theme.Surface;
                e.Control.ForeColor = Theme.Text;
            };
        }

        public DataGridViewTextBoxColumn AddText(string header, string name, int width = 0, bool fill = false, bool readOnly = true, bool right = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Name = name,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            };
            if (width > 0) c.Width = Theme.S(width);
            if (right)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            Columns.Add(c);
            return c;
        }
    }

    /// <summary>Rounded panel used to group related controls.</summary>
    class Card : Panel
    {
        public Color Fill = Theme.Panel;
        public int Radius = 12;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Padding = new Padding(Theme.S(16));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Hq(e.Graphics);
            Theme.FillRound(e.Graphics, Fill, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S((float)Radius));
        }
    }

    sealed class Caption : Label
    {
        public Caption(string text, Font font = null, Color? color = null)
        {
            Text = text;
            Font = font ?? Theme.Body;
            ForeColor = color ?? Theme.Muted;
            BackColor = Color.Transparent;
            AutoSize = false;
            AutoEllipsis = true;
            UseMnemonic = false;
            Height = TextRenderer.MeasureText("Ag", Font).Height + Theme.S(4);
            TextAlign = ContentAlignment.MiddleLeft;
        }
    }
}
