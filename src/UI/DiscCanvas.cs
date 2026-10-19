using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Discphew.Disc;

namespace Discphew.UI
{
    /// <summary>
    /// Maps sectors to physical radius. The spiral is written at constant linear velocity, so the
    /// area swept is proportional to the number of sectors: r² = r0² + k·sector.
    /// </summary>
    sealed class DiscGeometry
    {
        public const double OuterMm = 60, DataEndMm = 58, HoleMm = 7.5, ClampMm = 16.5, MirrorMm = 22.5;
        public double ProgramStartMm;
        public long CapacitySectors;
        double k;
        int offset;
        public bool Dvd;

        public static DiscGeometry For(DiscInfo d)
        {
            var g = new DiscGeometry { Dvd = d != null && d.IsDvd };
            g.ProgramStartMm = g.Dvd ? 24.0 : 25.0;
            g.offset = g.Dvd ? 0 : 150;           // CD: LBA 0 sits after the 2-second pregap
            long cap = 0;
            if (d?.Media != null && d.Media.TotalSectors > 0) cap = d.Media.TotalSectors;
            if (cap <= 0)
            {
                if (g.Dvd) cap = d?.Media != null && (d.Media.Kind == MediaKind.DvdPlusRDl || d.Media.Kind == MediaKind.DvdDashRDl) ? 4_171_712 : 2_295_104;
                else cap = d != null && d.LeadOutLba > 333_000 ? 360_000 : 333_000;   // 80 or 74 minutes
            }
            if (d != null && d.LeadOutLba > cap) cap = d.LeadOutLba;    // overburned
            g.CapacitySectors = cap;
            g.k = (DataEndMm * DataEndMm - g.ProgramStartMm * g.ProgramStartMm) / (cap + g.offset);
            return g;
        }

        public double Radius(long lba) => Math.Sqrt(ProgramStartMm * ProgramStartMm + Math.Max(0, lba + offset) * k);
        public long LbaAt(double mm) => (long)((mm * mm - ProgramStartMm * ProgramStartMm) / k) - offset;
        public double LeadInStartMm => Dvd ? 22.0 : 23.0;
    }

    sealed class DiscSegment
    {
        public long Start, End;
        public Color Color;
        public string Title = "";
        public string Detail = "";
        public int Index = -1;      // row in the side list
    }

    static class DiscArt
    {
        public static readonly Color[] Sheen =
        {
            Color.FromArgb(70, 108, 200, 240), Color.FromArgb(70, 183, 148, 246), Color.FromArgb(70, 240, 158, 194),
            Color.FromArgb(70, 240, 210, 140), Color.FromArgb(70, 123, 216, 160), Color.FromArgb(70, 108, 200, 240),
            Color.FromArgb(70, 183, 148, 246), Color.FromArgb(70, 240, 158, 194),
        };

        static Color SheenAt(double t, int alpha)
        {
            double x = t * Sheen.Length;
            int i = (int)x % Sheen.Length;
            var c = Theme.Mix(Sheen[i], Sheen[(i + 1) % Sheen.Length], x - Math.Floor(x));
            return Theme.WithAlpha(c, alpha);
        }

        public static GraphicsPath Circle(PointF c, float r)
        {
            var p = new GraphicsPath();
            p.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
            return p;
        }

        public static GraphicsPath Annulus(PointF c, float r0, float r1)
        {
            var p = new GraphicsPath(FillMode.Alternate);
            p.AddEllipse(c.X - r1, c.Y - r1, r1 * 2, r1 * 2);
            if (r0 > 0) p.AddEllipse(c.X - r0, c.Y - r0, r0 * 2, r0 * 2);
            return p;
        }

        public static void FillAnnulus(Graphics g, Color color, PointF c, float r0, float r1)
        {
            using var p = Annulus(c, r0, r1);
            using var b = new SolidBrush(color);
            g.FillPath(b, p);
        }

        public static void DrawSheen(Graphics g, PointF c, float r, int alpha = 70)
        {
            using var path = new GraphicsPath();
            int n = 144;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double a = i * Math.PI * 2 / n;
                pts[i] = new PointF(c.X + (float)(Math.Cos(a) * r), c.Y + (float)(Math.Sin(a) * r));
            }
            path.AddPolygon(pts);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(0, 255, 255, 255),
                CenterPoint = c,
                SurroundColors = Enumerable.Range(0, n).Select(i => SheenAt(i / (double)n, alpha)).ToArray(),
                FocusScales = new PointF(0.35f, 0.35f),
            };
            using var clip = Circle(c, r);
            var state = g.Save();
            g.SetClip(clip);
            g.FillPath(brush, path);
            g.Restore(state);
        }

        /// <summary>A small stylized disc, used for the logo and as a placeholder cover.</summary>
        public static void DrawMiniDisc(Graphics g, RectangleF r)
        {
            var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
            float rad = r.Width / 2;
            using (var b = new SolidBrush(Color.FromArgb(0x4A, 0x50, 0x5A))) g.FillEllipse(b, r);
            DrawSheen(g, c, rad, 200);
            FillAnnulus(g, Color.FromArgb(120, 20, 22, 26), c, rad * 0.62f, rad * 0.66f);
            using (var b = new SolidBrush(Color.FromArgb(0xC8, 0xCD, 0xD4))) g.FillEllipse(b, c.X - rad * 0.36f, c.Y - rad * 0.36f, rad * 0.72f, rad * 0.72f);
            using (var b = new SolidBrush(Theme.Sidebar)) g.FillEllipse(b, c.X - rad * 0.14f, c.Y - rad * 0.14f, rad * 0.28f, rad * 0.28f);
        }
    }

    /// <summary>Draws a disc as it physically is: hub, mirror band, lead-in, program area, lead-out.</summary>
    sealed class DiscCanvas : Painted
    {
        DiscInfo disc;
        DiscGeometry geo = DiscGeometry.For(null);
        List<DiscSegment> segments = new List<DiscSegment>();
        long usedSectors;
        int hover = -1, selected = -1;
        string hoverRegion;
        Point mouse;
        float reveal = 1f;
        readonly Timer revealTimer = new Timer { Interval = 15 };
        DateTime revealStart;

        public event Action<int> SegmentHovered;
        public event Action<int> SegmentClicked;
        public string EmptyText = "Insert a disc";

        public DiscCanvas()
        {
            revealTimer.Tick += (s, e) =>
            {
                double t = (DateTime.Now - revealStart).TotalMilliseconds / 700.0;
                reveal = (float)Math.Min(1, 1 - Math.Pow(1 - Math.Min(1, t), 3));
                if (t >= 1) revealTimer.Stop();
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing) { if (disposing) revealTimer.Dispose(); base.Dispose(disposing); }

        public void SetDisc(DiscInfo d, List<DiscSegment> segs, long used, bool animate)
        {
            disc = d;
            geo = DiscGeometry.For(d);
            segments = segs ?? new List<DiscSegment>();
            usedSectors = used;
            hover = -1;
            selected = -1;
            if (animate && d != null)
            {
                reveal = 0;
                revealStart = DateTime.Now;
                revealTimer.Start();
            }
            Invalidate();
        }

        public int Selected
        {
            get => selected;
            set { selected = value; Invalidate(); }
        }

        public DiscGeometry Geometry => geo;

        PointF Center => new PointF(Width / 2f, Height / 2f);
        float PxPerMm => Math.Max(1, Math.Min(Width, Height) * 0.47f / (float)DiscGeometry.OuterMm);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            var c = Center;
            double mm = Math.Sqrt(Math.Pow(e.X - c.X, 2) + Math.Pow(e.Y - c.Y, 2)) / PxPerMm;
            int hit = -1;
            string region = null;
            if (disc != null)
            {
                if (mm < DiscGeometry.HoleMm) region = null;
                else if (mm < DiscGeometry.ClampMm) region = "Clamping area\nClear polycarbonate where the drive grips the disc.";
                else if (mm < DiscGeometry.MirrorMm) region = "Mirror band\nOften carries the stamper code or a hologram.";
                else if (mm < geo.LeadInStartMm) region = "Inner edge of the data surface";
                else if (mm < geo.ProgramStartMm) region = disc.Media != null && disc.Media.Writable && usedSectors == 0 ? "Power calibration & lead-in" : "Lead-in\nHolds the table of contents (and CD-Text).";
                else if (mm <= DiscGeometry.DataEndMm + 0.5)
                {
                    long lba = geo.LbaAt(mm);
                    hit = Find(lba);
                    if (hit < 0)
                    {
                        if (lba >= usedSectors)
                        {
                            long free = geo.CapacitySectors - Math.Max(usedSectors, 0);
                            region = lba < usedSectors + (geo.Dvd ? 4000 : 6750) && usedSectors > 0
                                ? "Lead-out\nMarks the end of the recorded area."
                                : disc.Media != null && disc.Media.Writable
                                    ? $"Unrecorded space\n{Fmt.Bytes(free * 2048)} · {Fmt.Time(TimeSpan.FromSeconds(free / 75.0))} of audio"
                                    : "Unused surface";
                        }
                        else region = $"Sector {lba:N0}\nFile system data or padding.";
                    }
                }
                else if (mm <= DiscGeometry.OuterMm) region = "Outer rim";
            }
            if (hit != hover || region != hoverRegion)
            {
                hover = hit;
                hoverRegion = region;
                SegmentHovered?.Invoke(hit);
            }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; hoverRegion = null;
            SegmentHovered?.Invoke(-1);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (hover >= 0) { selected = hover; SegmentClicked?.Invoke(hover); Invalidate(); }
            base.OnMouseClick(e);
        }

        int Find(long lba)
        {
            int lo = 0, hi = segments.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var s = segments[mid];
                if (lba < s.Start) hi = mid - 1;
                else if (lba >= s.End) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        Color BaseColor()
        {
            var kind = disc?.Media?.Kind ?? MediaKind.Unknown;
            return kind switch
            {
                MediaKind.CdR => Color.FromArgb(0x1E, 0x3B, 0x45),
                MediaKind.CdRw => Color.FromArgb(0x38, 0x3B, 0x42),
                MediaKind.DvdPlusR or MediaKind.DvdDashR or MediaKind.DvdPlusRDl or MediaKind.DvdDashRDl => Color.FromArgb(0x35, 0x2C, 0x4A),
                MediaKind.DvdPlusRw or MediaKind.DvdDashRw or MediaKind.DvdRam => Color.FromArgb(0x3A, 0x38, 0x40),
                _ => Color.FromArgb(0x3F, 0x45, 0x4E),
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Hq(g);
            var c = Center;
            float s = PxPerMm;
            float R(double mm) => (float)(mm * s);

            // Soft shadow under the disc.
            for (int i = 6; i >= 1; i--)
                DiscArt.FillAnnulus(g, Color.FromArgb(10, 0, 0, 0), new PointF(c.X, c.Y + R(1.2)), 0, R(DiscGeometry.OuterMm) + i * 2.5f);

            if (disc == null)
            {
                using (var pen = new Pen(Theme.Border, Theme.S(2f)) { DashStyle = DashStyle.Dash })
                {
                    g.DrawEllipse(pen, c.X - R(60), c.Y - R(60), R(120), R(120));
                    g.DrawEllipse(pen, c.X - R(DiscGeometry.HoleMm), c.Y - R(DiscGeometry.HoleMm), R(DiscGeometry.HoleMm * 2), R(DiscGeometry.HoleMm * 2));
                }
                TextRenderer.DrawText(g, EmptyText, Theme.Heading, new Rectangle(0, (int)(c.Y - R(30)), Width, Theme.S(30)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // Body of the disc.
            var baseColor = BaseColor();
            using (var path = DiscArt.Circle(c, R(DiscGeometry.OuterMm)))
            using (var b = new LinearGradientBrush(new RectangleF(c.X - R(60), c.Y - R(60), R(120), R(120)), Theme.Mix(baseColor, Color.White, 0.10), Theme.Mix(baseColor, Color.Black, 0.25), 45f))
                g.FillPath(b, path);

            // Recorded area tint, then each track/file as rings, revealed outward on load.
            double endMm = usedSectors > 0 ? geo.Radius(usedSectors) : geo.ProgramStartMm;
            double revealMm = geo.ProgramStartMm + (DiscGeometry.DataEndMm - geo.ProgramStartMm) * reveal;
            double drawEnd = Math.Min(endMm, revealMm);
            if (usedSectors > 0)
                DiscArt.FillAnnulus(g, Theme.Mix(baseColor, Color.Black, 0.18), c, R(geo.ProgramStartMm), R(drawEnd));

            int startPx = (int)Math.Floor(R(geo.ProgramStartMm)), endPx = (int)Math.Ceiling(R(drawEnd));
            int seg = 0;
            using (var pen = new Pen(Color.White, 1.6f))
            {
                for (int px = startPx; px < endPx; px++)
                {
                    long a = geo.LbaAt(px / (double)s), b = geo.LbaAt((px + 1) / (double)s);
                    while (seg < segments.Count && segments[seg].End <= a) seg++;
                    DiscSegment best = null;
                    long bestCover = 0;
                    for (int j = seg; j < segments.Count && segments[j].Start < b; j++)
                    {
                        long cover = Math.Min(b, segments[j].End) - Math.Max(a, segments[j].Start);
                        if (cover > bestCover) { bestCover = cover; best = segments[j]; }
                    }
                    if (best == null) continue;
                    pen.Color = Theme.Mix(best.Color, baseColor, 0.18);
                    float rr = px + 0.5f;
                    g.DrawEllipse(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2);
                }
            }

            // Fine grooves and the rainbow sheen you see when light hits the spiral.
            using (var groove = new Pen(Color.FromArgb(16, 0, 0, 0), 1f))
                for (double mm = geo.ProgramStartMm + 1.5; mm < DiscGeometry.DataEndMm; mm += 1.5)
                    g.DrawEllipse(groove, c.X - R(mm), c.Y - R(mm), R(mm * 2), R(mm * 2));
            DiscArt.DrawSheen(g, c, R(DiscGeometry.DataEndMm), 34);

            // Lead-out ring after the recorded area.
            if (usedSectors > 0 && revealMm >= endMm)
            {
                double leadOutEnd = Math.Min(DiscGeometry.DataEndMm, endMm + (geo.Dvd ? 0.6 : 1.0));
                DiscArt.FillAnnulus(g, Color.FromArgb(150, 12, 13, 16), c, R(endMm), R(leadOutEnd));
            }

            // Inner structures: lead-in, mirror band, clamping area, hole.
            DiscArt.FillAnnulus(g, Color.FromArgb(0x1C, 0x1E, 0x23), c, R(geo.LeadInStartMm), R(geo.ProgramStartMm));
            using (var path = DiscArt.Annulus(c, R(DiscGeometry.ClampMm), R(DiscGeometry.MirrorMm)))
            using (var b = new LinearGradientBrush(new RectangleF(c.X - R(23), c.Y - R(23), R(46), R(46)), Color.FromArgb(0x9A, 0xA1, 0xAB), Color.FromArgb(0x50, 0x56, 0x5F), 60f))
                g.FillPath(b, path);
            DiscArt.FillAnnulus(g, Color.FromArgb(0x2A, 0x2D, 0x33), c, R(DiscGeometry.HoleMm), R(DiscGeometry.ClampMm));
            DiscArt.FillAnnulus(g, Color.FromArgb(0x33, 0x37, 0x3E), c, R(10.8), R(11.6));          // stacking ring
            using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, c.X - R(DiscGeometry.HoleMm), c.Y - R(DiscGeometry.HoleMm), R(DiscGeometry.HoleMm * 2), R(DiscGeometry.HoleMm * 2));
            using (var rim = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                g.DrawEllipse(rim, c.X - R(60), c.Y - R(60), R(120), R(120));

            // Highlight: selected and hovered segments.
            DrawHighlight(g, c, s, selected, Color.FromArgb(70, 255, 255, 255), Theme.Text);
            if (hover != selected) DrawHighlight(g, c, s, hover, Color.FromArgb(45, 255, 255, 255), Theme.WithAlpha(Theme.Text, 180));

            DrawTrackNumbers(g, c, s);
            DrawTooltip(g);
        }

        void DrawHighlight(Graphics g, PointF c, float s, int index, Color fill, Color line)
        {
            if (index < 0 || index >= segments.Count) return;
            var seg = segments[index];
            float r0 = (float)(geo.Radius(seg.Start) * s), r1 = (float)(geo.Radius(seg.End) * s);
            if (r1 - r0 < 2.5f) { float mid = (r0 + r1) / 2; r0 = mid - 1.25f; r1 = mid + 1.25f; }
            DiscArt.FillAnnulus(g, fill, c, r0, r1);
            using var pen = new Pen(line, 1.2f);
            g.DrawEllipse(pen, c.X - r0, c.Y - r0, r0 * 2, r0 * 2);
            g.DrawEllipse(pen, c.X - r1, c.Y - r1, r1 * 2, r1 * 2);
        }

        void DrawTrackNumbers(Graphics g, PointF c, float s)
        {
            if (disc == null || !disc.HasAudio || reveal < 1 || segments.Count > 99) return;
            int fontH = TextRenderer.MeasureText("00", Theme.Small).Height;
            // Badges wind around the disc like the spiral itself, so neighbours never collide.
            int i = 0;
            foreach (var seg in segments)
            {
                double angle = -Math.PI / 2 + i++ * (Math.PI * 2 / Math.Max(8, segments.Count));
                float r0 = (float)(geo.Radius(seg.Start) * s), r1 = (float)(geo.Radius(seg.End) * s);
                if (r1 - r0 < fontH * 0.55f) continue;
                float mid = (r0 + r1) / 2;
                var label = seg.Title.Split(' ')[0];
                var p = new PointF(c.X + (float)(Math.Cos(angle) * mid), c.Y + (float)(Math.Sin(angle) * mid));
                var size = TextRenderer.MeasureText(label, Theme.Small);
                var rect = new Rectangle((int)(p.X - size.Width / 2f), (int)(p.Y - size.Height / 2f), size.Width, size.Height);
                using (var b = new SolidBrush(Color.FromArgb(170, 10, 11, 14))) g.FillEllipse(b, RectangleF.Inflate(rect, 1, 0));
                TextRenderer.DrawText(g, label, Theme.Small, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void DrawTooltip(Graphics g)
        {
            string text = hover >= 0 ? segments[hover].Title + "\n" + segments[hover].Detail : hoverRegion;
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Split('\n');
            int pad = Theme.S(10);
            var sizes = lines.Select((l, i) => TextRenderer.MeasureText(l, i == 0 ? Theme.Strong : Theme.Small)).ToArray();
            int w = Math.Min(Theme.S(420), sizes.Max(z => z.Width) + pad * 2);
            int h = sizes.Sum(z => z.Height) + pad * 2;
            int x = mouse.X + Theme.S(18), y = mouse.Y + Theme.S(14);
            if (x + w > Width - 4) x = mouse.X - w - Theme.S(12);
            if (y + h > Height - 4) y = mouse.Y - h - Theme.S(8);
            var r = new RectangleF(x, y, w, h);
            Theme.FillRound(g, Color.FromArgb(240, 0x16, 0x17, 0x1A), r, Theme.S(8f));
            Theme.DrawRound(g, Theme.Border, r, Theme.S(8f));
            int ty = y + pad;
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0 && hover >= 0)
                {
                    float sw = Theme.S(10f);
                    Theme.FillRound(g, segments[hover].Color, new RectangleF(x + pad, ty + (sizes[0].Height - sw) / 2, sw, sw), Theme.S(3f));
                    Theme.DrawText(g, lines[i], Theme.Strong, Theme.Text, new Rectangle(x + pad + (int)sw + Theme.S(6), ty, w - pad * 2 - (int)sw - Theme.S(6), sizes[i].Height));
                }
                else Theme.DrawText(g, lines[i], i == 0 ? Theme.Strong : Theme.Small, i == 0 ? Theme.Text : Theme.Muted, new Rectangle(x + pad, ty, w - pad * 2, sizes[i].Height));
                ty += sizes[i].Height;
            }
        }
    }
}
