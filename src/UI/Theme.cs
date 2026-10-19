using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Discphew.Native;

namespace Discphew.UI
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x12, 0x13, 0x16);
        public static readonly Color Sidebar = Color.FromArgb(0x18, 0x19, 0x1D);
        public static readonly Color Panel = Color.FromArgb(0x1C, 0x1E, 0x23);
        public static readonly Color Surface = Color.FromArgb(0x25, 0x28, 0x2E);
        public static readonly Color SurfaceHover = Color.FromArgb(0x2E, 0x32, 0x39);
        public static readonly Color SurfacePressed = Color.FromArgb(0x36, 0x3B, 0x43);
        public static readonly Color Border = Color.FromArgb(0x32, 0x36, 0x3D);
        public static readonly Color Text = Color.FromArgb(0xEC, 0xEA, 0xE6);
        public static readonly Color Muted = Color.FromArgb(0xA0, 0xA5, 0xAD);
        public static readonly Color Faint = Color.FromArgb(0x6C, 0x71, 0x7A);
        public static readonly Color Accent = Color.FromArgb(0x6C, 0xC8, 0xF0);
        public static readonly Color AccentText = Color.FromArgb(0x0C, 0x1A, 0x22);
        public static readonly Color Accent2 = Color.FromArgb(0xB7, 0x94, 0xF6);
        public static readonly Color Danger = Color.FromArgb(0xF0, 0x7A, 0x7A);
        public static readonly Color Success = Color.FromArgb(0x7B, 0xD8, 0x8F);
        public static readonly Color Warning = Color.FromArgb(0xF0, 0xC6, 0x74);

        /// <summary>Hand-tuned categorical palette; reads well on the dark disc and against each other.</summary>
        public static readonly Color[] Palette =
        {
            Color.FromArgb(0x6C, 0xC8, 0xF0), Color.FromArgb(0xB7, 0x94, 0xF6), Color.FromArgb(0xF0, 0x9E, 0xC2),
            Color.FromArgb(0xF0, 0xC6, 0x74), Color.FromArgb(0x7B, 0xD8, 0x8F), Color.FromArgb(0xF2, 0x95, 0x6B),
            Color.FromArgb(0x5F, 0xA8, 0xE8), Color.FromArgb(0x9C, 0xDB, 0xD0), Color.FromArgb(0xD8, 0x86, 0xE0),
            Color.FromArgb(0xC9, 0xD8, 0x6E), Color.FromArgb(0xE8, 0x7C, 0x8C), Color.FromArgb(0x8E, 0x9C, 0xF0),
        };

        public static float Scale { get; private set; } = 1f;
        public static Font Body, Small, Strong, Title, Heading, Huge, Mono;

        public static void Init()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            string family = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            string display = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            Body = new Font(family, 9.75f);
            Small = new Font(family, 8.5f);
            Strong = new Font(family, 9.75f, FontStyle.Bold);
            Heading = new Font(display, 12f, FontStyle.Bold);
            Title = new Font(display, 17f, FontStyle.Bold);
            Huge = new Font(display, 26f, FontStyle.Bold);
            Mono = new Font(FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
        }

        public static int S(int px) => (int)Math.Round(px * Scale);
        public static float S(float px) => px * Scale;

        public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static Color WithAlpha(Color c, int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), c);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using var p = Round(r, radius);
            using var b = new SolidBrush(c);
            g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
        {
            using var p = Round(r, radius);
            using var pen = new Pen(c, width);
            g.DrawPath(pen, p);
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        public static void DarkTitleBar(Form f)
        {
            int on = 1;
            Win32.DwmSetWindowAttribute(f.Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            int caption = Sidebar.R | (Sidebar.G << 8) | (Sidebar.B << 16);
            Win32.DwmSetWindowAttribute(f.Handle, Win32.DWMWA_CAPTION_COLOR, ref caption, 4);
        }

        public static void DarkScrollbars(Control c)
        {
            if (c.IsHandleCreated) Win32.SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += (s, e) => Win32.SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r,
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix)
            => TextRenderer.DrawText(g, text, font, r, color, flags);
    }

    /// <summary>Vector icons drawn with GDI+, so they stay crisp at any DPI and need no image files.</summary>
    static class Icons
    {
        public delegate void Painter(Graphics g, RectangleF r, Color c);

        static Pen P(Color c, RectangleF r, float w = 0.09f) =>
            new Pen(c, Math.Max(1.2f, r.Width * w)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        public static void Disc(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r);
            var o = Inset(r, 0.08f);
            g.DrawEllipse(pen, o);
            g.DrawEllipse(pen, Inset(r, 0.38f));
            using var thin = new Pen(Theme.WithAlpha(c, 140), Math.Max(1f, r.Width * 0.05f));
            g.DrawArc(thin, Inset(r, 0.2f), 200, 70);
        }

        public static void Flame(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var path = new GraphicsPath();
            path.AddBezier(x + w * 0.5f, y + h * 0.92f, x + w * 0.18f, y + h * 0.88f, x + w * 0.12f, y + h * 0.55f, x + w * 0.32f, y + h * 0.36f);
            path.AddBezier(x + w * 0.32f, y + h * 0.36f, x + w * 0.38f, y + h * 0.5f, x + w * 0.44f, y + h * 0.5f, x + w * 0.46f, y + h * 0.46f);
            path.AddBezier(x + w * 0.46f, y + h * 0.46f, x + w * 0.4f, y + h * 0.25f, x + w * 0.52f, y + h * 0.12f, x + w * 0.6f, y + h * 0.06f);
            path.AddBezier(x + w * 0.6f, y + h * 0.06f, x + w * 0.62f, y + h * 0.3f, x + w * 0.88f, y + h * 0.42f, x + w * 0.84f, y + h * 0.66f);
            path.AddBezier(x + w * 0.84f, y + h * 0.66f, x + w * 0.82f, y + h * 0.84f, x + w * 0.68f, y + h * 0.92f, x + w * 0.5f, y + h * 0.92f);
            g.DrawPath(pen, path);
        }

        public static void Rip(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.DrawLine(pen, x + w * 0.5f, y + h * 0.1f, x + w * 0.5f, y + h * 0.62f);
            g.DrawLines(pen, new[] { new PointF(x + w * 0.28f, y + h * 0.42f), new PointF(x + w * 0.5f, y + h * 0.64f), new PointF(x + w * 0.72f, y + h * 0.42f) });
            g.DrawLines(pen, new[] { new PointF(x + w * 0.12f, y + h * 0.66f), new PointF(x + w * 0.12f, y + h * 0.88f), new PointF(x + w * 0.88f, y + h * 0.88f), new PointF(x + w * 0.88f, y + h * 0.66f) });
        }

        public static void Note(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.DrawLine(pen, x + w * 0.38f, y + h * 0.74f, x + w * 0.38f, y + h * 0.16f);
            g.DrawLine(pen, x + w * 0.38f, y + h * 0.16f, x + w * 0.82f, y + h * 0.08f);
            g.DrawLine(pen, x + w * 0.82f, y + h * 0.08f, x + w * 0.82f, y + h * 0.64f);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, x + w * 0.14f, y + h * 0.64f, w * 0.26f, h * 0.22f);
            g.FillEllipse(b, x + w * 0.58f, y + h * 0.55f, w * 0.26f, h * 0.22f);
        }

        public static void Play(Graphics g, RectangleF r, Color c)
        {
            using var b = new SolidBrush(c);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var path = new GraphicsPath();
            path.AddPolygon(new[] { new PointF(x + w * 0.28f, y + h * 0.16f), new PointF(x + w * 0.86f, y + h * 0.5f), new PointF(x + w * 0.28f, y + h * 0.84f) });
            g.FillPath(b, path);
            using var pen = new Pen(c, w * 0.08f) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }

        public static void Pause(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            Theme.FillRound(g, c, new RectangleF(x + w * 0.24f, y + h * 0.16f, w * 0.18f, h * 0.68f), w * 0.05f);
            Theme.FillRound(g, c, new RectangleF(x + w * 0.58f, y + h * 0.16f, w * 0.18f, h * 0.68f), w * 0.05f);
        }

        public static void Prev(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[] { new PointF(x + w * 0.78f, y + h * 0.2f), new PointF(x + w * 0.34f, y + h * 0.5f), new PointF(x + w * 0.78f, y + h * 0.8f) });
            Theme.FillRound(g, c, new RectangleF(x + w * 0.2f, y + h * 0.2f, w * 0.12f, h * 0.6f), w * 0.04f);
        }

        public static void Next(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[] { new PointF(x + w * 0.22f, y + h * 0.2f), new PointF(x + w * 0.66f, y + h * 0.5f), new PointF(x + w * 0.22f, y + h * 0.8f) });
            Theme.FillRound(g, c, new RectangleF(x + w * 0.68f, y + h * 0.2f, w * 0.12f, h * 0.6f), w * 0.04f);
        }

        public static void Stop(Graphics g, RectangleF r, Color c) => Theme.FillRound(g, c, Inset(r, 0.24f), r.Width * 0.08f);

        public static void Eject(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[] { new PointF(x + w * 0.5f, y + h * 0.18f), new PointF(x + w * 0.84f, y + h * 0.56f), new PointF(x + w * 0.16f, y + h * 0.56f) });
            Theme.FillRound(g, c, new RectangleF(x + w * 0.16f, y + h * 0.68f, w * 0.68f, h * 0.13f), w * 0.04f);
        }

        public static void Plus(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, d = r.Width * 0.3f;
            g.DrawLine(pen, cx - d, cy, cx + d, cy);
            g.DrawLine(pen, cx, cy - d, cx, cy + d);
        }

        public static void Minus(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, d = r.Width * 0.3f;
            g.DrawLine(pen, cx - d, cy, cx + d, cy);
        }

        public static void Up(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.62f), new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.36f), new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.62f) });
        }

        public static void Down(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.38f), new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.64f), new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.38f) });
        }

        public static void Folder(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.08f);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.DrawLines(pen, new[]
            {
                new PointF(x + w * 0.1f, y + h * 0.78f), new PointF(x + w * 0.1f, y + h * 0.22f), new PointF(x + w * 0.38f, y + h * 0.22f),
                new PointF(x + w * 0.47f, y + h * 0.32f), new PointF(x + w * 0.9f, y + h * 0.32f), new PointF(x + w * 0.9f, y + h * 0.78f),
                new PointF(x + w * 0.1f, y + h * 0.78f),
            });
        }

        public static void File(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.08f);
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.DrawLines(pen, new[]
            {
                new PointF(x + w * 0.22f, y + h * 0.1f), new PointF(x + w * 0.58f, y + h * 0.1f), new PointF(x + w * 0.8f, y + h * 0.32f),
                new PointF(x + w * 0.8f, y + h * 0.9f), new PointF(x + w * 0.22f, y + h * 0.9f), new PointF(x + w * 0.22f, y + h * 0.1f),
            });
            g.DrawLines(pen, new[] { new PointF(x + w * 0.58f, y + h * 0.1f), new PointF(x + w * 0.58f, y + h * 0.32f), new PointF(x + w * 0.8f, y + h * 0.32f) });
        }

        public static void Search(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            g.DrawEllipse(pen, r.X + r.Width * 0.14f, r.Y + r.Height * 0.14f, r.Width * 0.5f, r.Height * 0.5f);
            g.DrawLine(pen, r.X + r.Width * 0.58f, r.Y + r.Height * 0.58f, r.X + r.Width * 0.86f, r.Y + r.Height * 0.86f);
        }

        public static void Refresh(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.09f);
            var o = Inset(r, 0.2f);
            g.DrawArc(pen, o, -60, 290);
            float cx = o.X + o.Width / 2, cy = o.Y + o.Height / 2, rad = o.Width / 2;
            double a = -60 * Math.PI / 180;
            var tip = new PointF(cx + rad * (float)Math.Cos(a), cy + rad * (float)Math.Sin(a));
            g.DrawLines(pen, new[] { new PointF(tip.X - r.Width * 0.2f, tip.Y - r.Height * 0.02f), tip, new PointF(tip.X - r.Width * 0.02f, tip.Y + r.Height * 0.2f) });
        }

        public static void Close(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.1f);
            float d = r.Width * 0.26f, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
        }

        public static void Volume(Graphics g, RectangleF r, Color c)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[]
            {
                new PointF(x + w * 0.12f, y + h * 0.38f), new PointF(x + w * 0.3f, y + h * 0.38f), new PointF(x + w * 0.52f, y + h * 0.18f),
                new PointF(x + w * 0.52f, y + h * 0.82f), new PointF(x + w * 0.3f, y + h * 0.62f), new PointF(x + w * 0.12f, y + h * 0.62f),
            });
            using var pen = P(c, r, 0.07f);
            g.DrawArc(pen, x + w * 0.42f, y + h * 0.3f, w * 0.3f, h * 0.4f, -50, 100);
            g.DrawArc(pen, x + w * 0.38f, y + h * 0.16f, w * 0.5f, h * 0.68f, -50, 100);
        }

        public static void Check(Graphics g, RectangleF r, Color c)
        {
            using var pen = P(c, r, 0.12f);
            g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f), new PointF(r.X + r.Width * 0.8f, r.Y + r.Height * 0.3f) });
        }

        static RectangleF Inset(RectangleF r, float f) => new RectangleF(r.X + r.Width * f, r.Y + r.Height * f, r.Width * (1 - 2 * f), r.Height * (1 - 2 * f));
    }
}
