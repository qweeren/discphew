using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Discphew.UI
{
    /// <summary>Album art with rounded corners; falls back to a drawn disc.</summary>
    sealed class CoverBox : Painted
    {
        Image image;
        public string Caption = "";

        public Image Image
        {
            get => image;
            set { if (image != value) { image?.Dispose(); image = value; Invalidate(); } }
        }

        public void LoadFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Image = null; return; }
                using var fs = File.OpenRead(path);
                using var tmp = Image.FromStream(fs);
                Image = new Bitmap(tmp);
            }
            catch { Image = null; }
        }

        public void LoadBytes(byte[] data)
        {
            try
            {
                if (data == null) { Image = null; return; }
                using var ms = new MemoryStream(data);
                using var tmp = Image.FromStream(ms);
                Image = new Bitmap(tmp);
            }
            catch { Image = null; }
        }

        protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            float radius = Theme.S(10f);
            using var clip = Theme.Round(r, radius);
            if (image != null)
            {
                var state = g.Save();
                g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float scale = Math.Max(r.Width / image.Width, r.Height / image.Height);
                float w = image.Width * scale, h = image.Height * scale;
                g.DrawImage(image, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                g.Restore(state);
                Theme.DrawRound(g, Color.FromArgb(40, 255, 255, 255), r, radius);
                return;
            }
            using (var b = new LinearGradientBrush(r, Theme.Surface, Theme.Panel, 60f)) g.FillPath(b, clip);
            float d = Math.Min(r.Width, r.Height) * 0.56f;
            DiscArt.DrawMiniDisc(g, new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2 - (Caption.Length > 0 ? Theme.S(8) : 0), d, d));
            if (Caption.Length > 0)
                TextRenderer.DrawText(g, Caption, Theme.Small, new Rectangle(0, (int)(r.Bottom - Theme.S(26)), Width, Theme.S(20)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    static class Ui
    {
        /// <summary>A caption stacked over a control, sized to the control's width.</summary>
        public static Panel Labeled(string label, Control control, int width)
        {
            var p = new Panel { BackColor = Color.Transparent, Width = width };
            var cap = new Caption(label, Theme.Small, Theme.Muted) { Location = Point.Empty, Width = width };
            control.Location = new Point(0, cap.Height);
            control.Width = width;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            cap.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            p.Height = cap.Height + control.Height;
            p.Controls.Add(control);
            p.Controls.Add(cap);
            return p;
        }

        public static string SafeName(string s, string fallback = "Unknown")
        {
            if (string.IsNullOrWhiteSpace(s)) s = fallback;
            var bad = Path.GetInvalidFileNameChars();
            var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (clean.Length > 120) clean = clean.Substring(0, 120).Trim();
            return clean.Length == 0 ? fallback : clean;
        }

        public static string PickFolder(IWin32Window owner, string description, string initial)
        {
            using var d = new FolderBrowserDialog { Description = description, SelectedPath = initial ?? "", ShowNewFolderButton = true };
            return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
        }

        public static void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        public static string DefaultMusicFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }
}
