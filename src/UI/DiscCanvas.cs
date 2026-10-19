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

        // View: the point on the disc (in mm from the hub) shown at the middle of the control, and a zoom factor.
        // Keeping the view in disc millimetres means resizing the window never loses your place.
        public const double MaxZoom = 1000;
        double zoom = 1;
        PointF look;
        readonly Timer viewTimer = new Timer { Interval = 15 };
        DateTime viewStart;
        double zoomFrom, zoomTo = 1;
        PointF anchor, worldFrom, worldTo;      // anchor: screen point that tracks the world point during an animation
        Point dragStart;
        PointF lookAtDragStart;
        bool dragging, suppressClick, fitHot;

        public event Action<int> SegmentHovered;
        public event Action<int> SegmentClicked;
        public string EmptyText = "Insert a disc";

        public DiscCanvas()
        {
            SetStyle(ControlStyles.Selectable, true);
            BackColor = Theme.Bg;       // opaque, so the cached layer can be copied straight in without painting the parent first
            revealTimer.Tick += (s, e) =>
            {
                double t = (DateTime.Now - revealStart).TotalMilliseconds / 700.0;
                reveal = (float)Math.Min(1, 1 - Math.Pow(1 - Math.Min(1, t), 3));
                if (t >= 1) revealTimer.Stop();
                Invalidate();
            };
            viewTimer.Tick += (s, e) => StepView();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { revealTimer.Dispose(); viewTimer.Dispose(); layer?.Dispose(); sheen?.Dispose(); }
            base.Dispose(disposing);
        }

        public void SetDisc(DiscInfo d, List<DiscSegment> segs, long used, bool animate)
        {
            if (!ReferenceEquals(d, disc)) { viewTimer.Stop(); zoom = zoomTo = 1; look = PointF.Empty; }
            disc = d;
            geo = DiscGeometry.For(d);
            segments = segs ?? new List<DiscSegment>();
            usedSectors = used;
            layerKey = default;         // the same disc can come back with new media info
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

        PointF ViewCenter => new PointF(Width / 2f, Height / 2f);
        float BaseScale => Math.Max(1, Math.Min(Width, Height) * 0.47f / (float)DiscGeometry.OuterMm);
        float PxPerMm => (float)(BaseScale * zoom);
        /// <summary>Screen position of the hub.</summary>
        PointF Center => new PointF(ViewCenter.X - look.X * PxPerMm, ViewCenter.Y - look.Y * PxPerMm);
        PointF ToWorld(PointF screen) => new PointF((screen.X - Center.X) / PxPerMm, (screen.Y - Center.Y) / PxPerMm);

        public double Zoom => zoom;

        /// <summary>Puts <paramref name="world"/> (mm) under <paramref name="screen"/> at the current zoom, keeping the disc in view.</summary>
        void Place(PointF world, PointF screen)
        {
            float s = PxPerMm;
            var vc = ViewCenter;
            float x = world.X - (screen.X - vc.X) / s, y = world.Y - (screen.Y - vc.Y) / s;
            float lim = (float)DiscGeometry.OuterMm;
            look = new PointF(Math.Max(-lim, Math.Min(lim, x)), Math.Max(-lim, Math.Min(lim, y)));
        }

        void AnimateTo(double targetZoom, PointF screenAnchor, PointF targetWorld)
        {
            targetZoom = Math.Max(1, Math.Min(MaxZoom, targetZoom));
            // Zooming all the way out always re-centres the disc.
            if (targetZoom <= 1.0001)
                targetWorld = new PointF((screenAnchor.X - ViewCenter.X) / BaseScale, (screenAnchor.Y - ViewCenter.Y) / BaseScale);
            zoomFrom = zoom;
            zoomTo = targetZoom;
            anchor = screenAnchor;
            worldFrom = ToWorld(screenAnchor);
            worldTo = targetWorld;
            viewStart = DateTime.Now;
            viewTimer.Start();
        }

        void StepView()
        {
            double t = Math.Min(1, (DateTime.Now - viewStart).TotalMilliseconds / 220.0);
            double e = 1 - Math.Pow(1 - t, 3);                       // ease-out
            zoom = zoomFrom * Math.Pow(zoomTo / zoomFrom, e);       // interpolate in log space so every step feels the same
            var w = new PointF((float)(worldFrom.X + (worldTo.X - worldFrom.X) * e), (float)(worldFrom.Y + (worldTo.Y - worldFrom.Y) * e));
            Place(w, anchor);
            if (t >= 1) viewTimer.Stop();
            UpdateHover();
            Invalidate();
        }

        /// <summary>Zooms by <paramref name="factor"/> around a screen point (the middle when omitted).</summary>
        public void ZoomBy(double factor, Point? at = null)
        {
            if (disc == null) return;
            var p = at.HasValue ? (PointF)at.Value : ViewCenter;
            // Chain quick wheel notches onto the running animation's target instead of the current frame.
            bool chaining = viewTimer.Enabled && anchor == p;
            double from = viewTimer.Enabled ? zoomTo : zoom;
            var world = chaining ? worldTo : ToWorld(p);
            AnimateTo(from * factor, p, world);
        }

        public void ResetView()
        {
            if (zoom == 1 && look == PointF.Empty) return;
            AnimateTo(1, ViewCenter, PointF.Empty);
        }

        /// <summary>Flies to a ring so it fills a good part of the view.</summary>
        public void FocusSegment(int index)
        {
            if (disc == null || index < 0 || index >= segments.Count) return;
            var seg = segments[index];
            double r0 = geo.Radius(seg.Start), r1 = geo.Radius(seg.End);
            double band = Math.Max(1e-4, r1 - r0);
            double target = Math.Min(Width, Height) * 0.22 / (band * BaseScale);
            target = Math.Max(2.5, Math.Min(MaxZoom, target));
            selected = index;
            AnimateTo(target, ViewCenter, new PointF(0, (float)-((r0 + r1) / 2)));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomBy(Math.Pow(1.35, e.Delta / 120.0), e.Location);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                dragStart = e.Location;
                lookAtDragStart = look;
                dragging = false;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) { dragging = false; suppressClick = true; }
            else if (e.Button == MouseButtons.Left && zoom > 1.001 && FitButton.Contains(e.Location)) { suppressClick = true; ResetView(); }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !(zoom > 1.001 && FitButton.Contains(e.Location)))
            {
                if (ModifierKeys.HasFlag(Keys.Shift) || zoom >= MaxZoom * 0.99) ResetView();
                else ZoomBy(3, e.Location);
            }
            base.OnMouseDoubleClick(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            float step = Theme.S(60) / PxPerMm;
            PointF? to = e.KeyCode switch
            {
                Keys.Left => new PointF(look.X - step, look.Y),
                Keys.Right => new PointF(look.X + step, look.Y),
                Keys.Up => new PointF(look.X, look.Y - step),
                Keys.Down => new PointF(look.X, look.Y + step),
                _ => (PointF?)null,
            };
            if (to.HasValue && disc != null)
            {
                viewTimer.Stop();
                Place(to.Value, ViewCenter);
                UpdateHover();
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        Rectangle FitButton
        {
            get
            {
                int h = Theme.S(30), w = TextRenderer.MeasureText("Fit disc", Theme.Small).Width + Theme.S(24);
                return new Rectangle(Width - w - Theme.S(8), Theme.S(8), w, h);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle) && disc != null)
            {
                int dx = e.X - dragStart.X, dy = e.Y - dragStart.Y;
                if (!dragging && dx * dx + dy * dy > Theme.S(4) * Theme.S(4)) { dragging = true; viewTimer.Stop(); }
                if (dragging)
                {
                    float s = PxPerMm;
                    Place(new PointF(lookAtDragStart.X - dx / s, lookAtDragStart.Y - dy / s), ViewCenter);
                    Cursor = Cursors.SizeAll;
                    hover = -1; hoverRegion = null;
                    Invalidate();
                    base.OnMouseMove(e);
                    return;
                }
            }
            bool tipBefore = hover >= 0 || hoverRegion != null, fitBefore = fitHot;
            UpdateHover();
            // The tooltip follows the mouse, but over empty space there's nothing to redraw.
            if (tipBefore || hover >= 0 || hoverRegion != null || fitHot != fitBefore) Invalidate();
            base.OnMouseMove(e);
        }

        void UpdateHover()
        {
            if (dragging || !IsHandleCreated) return;
            mouse = PointToClient(MousePosition);
            if (!ClientRectangle.Contains(mouse))
            {
                if (hover >= 0 || hoverRegion != null) { hover = -1; hoverRegion = null; SegmentHovered?.Invoke(-1); }
                fitHot = false;
                return;
            }
            var e = mouse;
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
            bool onFit = fitHot = zoom > 1.001 && FitButton.Contains(e);
            Cursor = onFit || hit >= 0 ? Cursors.Hand : zoom > 1.001 ? Cursors.SizeAll : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; hoverRegion = null;
            SegmentHovered?.Invoke(-1);
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (suppressClick) { suppressClick = false; base.OnMouseClick(e); return; }
            if (e.Button == MouseButtons.Left && hover >= 0) { selected = hover; SegmentClicked?.Invoke(hover); Invalidate(); }
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

        // Visible window for the current paint: radii (px) that cross the control, and the angular slice they span.
        double visNear, visFar;
        float arcStart, arcSweep = 360;

        void ComputeWindow(PointF c)
        {
            visNear = Math.Sqrt(Math.Pow(Math.Max(0, Math.Max(-c.X, c.X - Width)), 2) + Math.Pow(Math.Max(0, Math.Max(-c.Y, c.Y - Height)), 2));
            visFar = Math.Sqrt(Math.Pow(Math.Max(c.X, Width - c.X), 2) + Math.Pow(Math.Max(c.Y, Height - c.Y), 2));
            arcStart = 0; arcSweep = 360;
            if (visNear < 8) return;                     // hub on (or next to) the screen: draw whole circles
            double refAngle = Math.Atan2(Height / 2.0 - c.Y, Width / 2.0 - c.X) * 180 / Math.PI;
            double lo = 0, hi = 0;
            foreach (var p in new[] { new PointF(0, 0), new PointF(Width, 0), new PointF(0, Height), new PointF(Width, Height) })
            {
                double d = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI - refAngle;
                while (d > 180) d -= 360;
                while (d <= -180) d += 360;
                lo = Math.Min(lo, d); hi = Math.Max(hi, d);
            }
            arcStart = (float)(refAngle + lo - 2);
            arcSweep = (float)Math.Min(360, hi - lo + 4);
        }

        /// <summary>Fills the ring between two radii (px), but only the part that can be on screen.</summary>
        void FillRing(Graphics g, Brush b, PointF c, float r0, float r1)
        {
            using var path = RingPath(c, r0, r1);
            if (path != null) g.FillPath(b, path);
        }

        /// <summary>The ring between two radii (px), cut down to the part that can be on screen; null when none of it is.</summary>
        GraphicsPath RingPath(PointF c, float r0, float r1)
        {
            r0 = (float)Math.Max(r0, visNear - 4);
            r1 = (float)Math.Min(r1, visFar + 4);
            if (r1 <= r0) return null;
            var path = new GraphicsPath(FillMode.Alternate);
            if (arcSweep >= 360)
            {
                path.AddEllipse(c.X - r1, c.Y - r1, r1 * 2, r1 * 2);
                if (r0 > 0.5f) path.AddEllipse(c.X - r0, c.Y - r0, r0 * 2, r0 * 2);
            }
            else
            {
                path.AddArc(c.X - r1, c.Y - r1, r1 * 2, r1 * 2, arcStart, arcSweep);
                path.AddArc(c.X - r0, c.Y - r0, r0 * 2, r0 * 2, arcStart + arcSweep, -arcSweep);
                path.CloseFigure();
            }
            return path;
        }

        void FillRing(Graphics g, Color color, PointF c, float r0, float r1)
        {
            using var b = new SolidBrush(color);
            FillRing(g, b, c, r0, r1);
        }

        void StrokeRing(Graphics g, Pen pen, PointF c, float r)
        {
            if (r <= 0 || r + pen.Width / 2 < visNear - 4 || r - pen.Width / 2 > visFar + 4) return;
            if (arcSweep >= 360) g.DrawEllipse(pen, c.X - r, c.Y - r, r * 2, r * 2);
            else g.DrawArc(pen, c.X - r, c.Y - r, r * 2, r * 2, arcStart, arcSweep);
        }

        // The disc itself is cached: it only changes with the view, the size, the data or the reveal, while hovering
        // (which repaints on every mouse move) only needs the highlights and the tooltip drawn over it again.
        Bitmap layer;
        (double zoom, PointF look, float reveal, DiscInfo disc, List<DiscSegment> segments, long used) layerKey;
        Bitmap sheen;       // the rainbow sheen at fit-to-view size; a PathGradientBrush over the whole disc costs 25-50 ms a frame

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (disc == null || Width <= 0 || Height <= 0) base.OnPaintBackground(e);    // otherwise the cached layer covers everything
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var c = Center;
            float s = PxPerMm;
            ComputeWindow(c);

            if (disc == null)
            {
                Theme.Hq(g);
                float R(double mm) => (float)(mm * s);
                DrawShadow(g, c, s);
                using (var pen = new Pen(Theme.Border, Theme.S(2f)) { DashStyle = DashStyle.Dash })
                {
                    g.DrawEllipse(pen, c.X - R(60), c.Y - R(60), R(120), R(120));
                    g.DrawEllipse(pen, c.X - R(DiscGeometry.HoleMm), c.Y - R(DiscGeometry.HoleMm), R(DiscGeometry.HoleMm * 2), R(DiscGeometry.HoleMm * 2));
                }
                TextRenderer.DrawText(g, EmptyText, Theme.Heading, new Rectangle(0, (int)(c.Y - R(30)), Width, Theme.S(30)), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (Width <= 0 || Height <= 0) return;

            var key = (zoom, look, reveal, disc, segments, usedSectors);
            if (layer == null || layer.Width != Width || layer.Height != Height || !key.Equals(layerKey))
            {
                if (layer == null || layer.Width != Width || layer.Height != Height)
                {
                    layer?.Dispose();
                    layer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                }
                using var lg = Graphics.FromImage(layer);
                lg.Clear(BackColor);
                Theme.Hq(lg);
                DrawDisc(lg, c, s);
                layerKey = key;
            }
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(layer, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Theme.Hq(g);

            // Highlight: selected and hovered segments.
            DrawHighlight(g, c, s, selected, Color.FromArgb(70, 255, 255, 255), Theme.Text);
            if (hover != selected) DrawHighlight(g, c, s, hover, Color.FromArgb(45, 255, 255, 255), Theme.WithAlpha(Theme.Text, 180));

            DrawTrackNumbers(g, c, s);
            DrawViewOverlay(g, s);
            DrawTooltip(g);
        }

        void DrawShadow(Graphics g, PointF c, float s)
        {
            float R(double mm) => (float)(mm * s);
            if (R(DiscGeometry.OuterMm + 2) >= visNear - 4 && R(DiscGeometry.OuterMm - 1) <= visFar + 4)
                for (int i = 6; i >= 1; i--)
                    FillRing(g, Color.FromArgb(10, 0, 0, 0), new PointF(c.X, c.Y + R(1.2)), R(DiscGeometry.OuterMm) - Theme.S(20), R(DiscGeometry.OuterMm) + i * 2.5f);
        }

        /// <summary>Draws the part of the cached sheen (radius <paramref name="r"/> px) that's on screen.</summary>
        void DrawSheen(Graphics g, PointF c, float r)
        {
            // Cached at fit-to-view size, drawn a few pixels past the edge so scaling up never shows the bitmap's own edge.
            int radius = (int)Math.Ceiling(DiscGeometry.DataEndMm * BaseScale), margin = 3, size = (radius + margin) * 2;
            if (sheen == null || sheen.Width != size)
            {
                sheen?.Dispose();
                sheen = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var sg = Graphics.FromImage(sheen);
                Theme.Hq(sg);
                DiscArt.DrawSheen(sg, new PointF(size / 2f, size / 2f), radius + margin, 34);
            }
            float k = radius / r;                       // bitmap pixels per screen pixel
            float half = size / 2f / k;
            var whole = new RectangleF(c.X - half, c.Y - half, half * 2, half * 2);
            var dest = RectangleF.Intersect(whole, ClientRectangle);
            if (dest.Width <= 0 || dest.Height <= 0) return;
            var src = new RectangleF((dest.X - whole.X) * k, (dest.Y - whole.Y) * k, dest.Width * k, dest.Height * k);
            // GDI+'s bilinear upscaling costs ~40 ms a frame; on a gradient this faint nearest-neighbour looks the same,
            // as long as the edge comes from a vector clip rather than from the bitmap.
            using var edge = RingPath(c, 0, r);
            if (edge == null) return;
            var state = g.Save();
            g.SetClip(edge, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(sheen, dest, src, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        /// <summary>Everything that doesn't depend on the mouse: shadow, body, rings, grooves, sheen and the inner structures.</summary>
        void DrawDisc(Graphics g, PointF c, float s)
        {
            float R(double mm) => (float)(mm * s);

            // Radii (px) that cross the visible area. Anything outside is skipped: when zoomed in that's nearly everything.
            double nearest = visNear, farthest = visFar;
            bool Vis(double mm0, double mm1) => R(mm1) >= nearest - 4 && R(mm0) <= farthest + 4;

            // Soft shadow under the disc.
            DrawShadow(g, c, s);

            // Body of the disc.
            var baseColor = BaseColor();
            using (var b = new LinearGradientBrush(new RectangleF(c.X - R(60), c.Y - R(60), R(120), R(120)), Theme.Mix(baseColor, Color.White, 0.10), Theme.Mix(baseColor, Color.Black, 0.25), 45f))
                FillRing(g, b, c, 0, R(DiscGeometry.OuterMm));

            // Recorded area tint, then each track/file as rings, revealed outward on load.
            double endMm = usedSectors > 0 ? geo.Radius(usedSectors) : geo.ProgramStartMm;
            double revealMm = geo.ProgramStartMm + (DiscGeometry.DataEndMm - geo.ProgramStartMm) * reveal;
            double drawEnd = Math.Min(endMm, revealMm);
            if (usedSectors > 0 && Vis(geo.ProgramStartMm, drawEnd))
                FillRing(g, Theme.Mix(baseColor, Color.Black, 0.18), c, R(geo.ProgramStartMm), R(drawEnd));

            int startPx = (int)Math.Max(Math.Floor(R(geo.ProgramStartMm)), Math.Floor(nearest) - 1);
            int endPx = (int)Math.Min(Math.Ceiling(R(drawEnd)), Math.Ceiling(farthest) + 1);
            int seg = 0;
            DiscSegment runSeg = null;
            int runStart = 0;
            void FlushRun(int runEnd)
            {
                if (runSeg == null) return;
                // One stroke centred in the band and slightly wider than it, so neighbouring rings never show a seam.
                float mid = (runStart + runEnd) / 2f;
                using var pen = new Pen(Theme.Mix(runSeg.Color, baseColor, 0.18), runEnd - runStart + 0.8f);
                StrokeRing(g, pen, c, mid);
            }
            for (int px = startPx; px < endPx; px++)
            {
                long a = geo.LbaAt(px / (double)s), b = Math.Max(a + 1, geo.LbaAt((px + 1) / (double)s));
                while (seg < segments.Count && segments[seg].End <= a) seg++;
                DiscSegment best = null;
                long bestCover = 0;
                for (int j = seg; j < segments.Count && segments[j].Start < b; j++)
                {
                    long cover = Math.Min(b, segments[j].End) - Math.Max(a, segments[j].Start);
                    if (cover > bestCover) { bestCover = cover; best = segments[j]; }
                }
                if (best != runSeg) { FlushRun(px); runSeg = best; runStart = px; }
            }
            FlushRun(endPx);

            // Fine grooves and the rainbow sheen you see when light hits the spiral.
            using (var groove = new Pen(Color.FromArgb(16, 0, 0, 0), 1f))
                for (double mm = geo.ProgramStartMm + 1.5; mm < DiscGeometry.DataEndMm; mm += 1.5)
                    StrokeRing(g, groove, c, R(mm));
            // The sheen reads as a whole-disc effect; zoomed far in it would only add faceting.
            if (zoom < 30 && Vis(geo.ProgramStartMm, DiscGeometry.DataEndMm)) DrawSheen(g, c, R(DiscGeometry.DataEndMm));

            // Lead-out ring after the recorded area.
            if (usedSectors > 0 && revealMm >= endMm && Vis(endMm, endMm + 1))
            {
                double leadOutEnd = Math.Min(DiscGeometry.DataEndMm, endMm + (geo.Dvd ? 0.6 : 1.0));
                FillRing(g, Color.FromArgb(150, 12, 13, 16), c, R(endMm), R(leadOutEnd));
            }

            // Inner structures: lead-in, mirror band, clamping area, hole.
            if (Vis(geo.LeadInStartMm, geo.ProgramStartMm))
                FillRing(g, Color.FromArgb(0x1C, 0x1E, 0x23), c, R(geo.LeadInStartMm), R(geo.ProgramStartMm));
            if (Vis(DiscGeometry.ClampMm, DiscGeometry.MirrorMm))
                using (var b = new LinearGradientBrush(new RectangleF(c.X - R(23), c.Y - R(23), R(46), R(46)), Color.FromArgb(0x9A, 0xA1, 0xAB), Color.FromArgb(0x50, 0x56, 0x5F), 60f))
                    FillRing(g, b, c, R(DiscGeometry.ClampMm), R(DiscGeometry.MirrorMm));
            if (Vis(0, DiscGeometry.ClampMm))
            {
                FillRing(g, Color.FromArgb(0x2A, 0x2D, 0x33), c, R(DiscGeometry.HoleMm), R(DiscGeometry.ClampMm));
                FillRing(g, Color.FromArgb(0x33, 0x37, 0x3E), c, R(10.8), R(11.6));          // stacking ring
                FillRing(g, Theme.Bg, c, 0, R(DiscGeometry.HoleMm));
            }
            if (Vis(DiscGeometry.OuterMm, DiscGeometry.OuterMm))
                using (var rim = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    StrokeRing(g, rim, c, R(60));
        }

        void DrawViewOverlay(Graphics g, float s)
        {
            int pad = Theme.S(10);
            // Scale bar: the largest "nice" length that fits in ~120 px, so physical size stays readable at any zoom.
            double[] steps = { 20, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001 };
            double mm = steps.FirstOrDefault(x => x * s <= Theme.S(120));
            if (mm == 0) mm = steps[steps.Length - 1];
            float barW = (float)(mm * s);
            string label = mm >= 1 ? $"{mm:0} mm" : mm >= 0.1 ? $"{mm:0.0} mm" : $"{mm * 1000:0} µm";
            string zoomText = zoom >= 10 ? $"{zoom:0}×" : $"{zoom:0.0}×";
            int y = Height - pad - Theme.S(4);
            using (var pen = new Pen(Theme.Muted, Theme.S(1.5f)))
            {
                g.DrawLine(pen, pad, y, pad + barW, y);
                g.DrawLine(pen, pad, y - Theme.S(5), pad, y);
                g.DrawLine(pen, pad + barW, y - Theme.S(5), pad + barW, y);
            }
            int textH = TextRenderer.MeasureText("0", Theme.Small).Height;
            string scaleText = $"{label}   ·   {zoomText}";
            var textRow = new Rectangle(pad, y - Theme.S(8) - textH, TextRenderer.MeasureText(scaleText, Theme.Small).Width + 2, textH);
            if (zoom > 1.001)
            {
                // Zoomed in, the overlay sits on top of coloured rings: give it a backing so it stays legible.
                var back = Rectangle.FromLTRB(pad - Theme.S(6), textRow.Y - Theme.S(4), Math.Max(textRow.Right, pad + (int)barW) + Theme.S(6), y + Theme.S(6));
                Theme.FillRound(g, Color.FromArgb(200, 0x16, 0x17, 0x1A), back, Theme.S(6f));
            }
            Theme.DrawText(g, scaleText, Theme.Small, Theme.Muted, textRow);
            if (zoom <= 1.001)
                Theme.DrawText(g, "Scroll to zoom · drag to pan · double-click to zoom in", Theme.Small, Theme.Faint,
                    new Rectangle(0, textRow.Y, Width - pad, textH), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            else
            {
                var r = FitButton;
                bool hot = r.Contains(mouse) && ClientRectangle.Contains(PointToClient(MousePosition));
                Theme.FillRound(g, hot ? Theme.SurfaceHover : Theme.WithAlpha(Theme.Surface, 230), r, Theme.S(8f));
                TextRenderer.DrawText(g, "Fit disc", Theme.Small, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        void DrawHighlight(Graphics g, PointF c, float s, int index, Color fill, Color line)
        {
            if (index < 0 || index >= segments.Count) return;
            var seg = segments[index];
            float r0 = (float)(geo.Radius(seg.Start) * s), r1 = (float)(geo.Radius(seg.End) * s);
            if (r1 - r0 < 2.5f) { float mid = (r0 + r1) / 2; r0 = mid - 1.25f; r1 = mid + 1.25f; }
            FillRing(g, fill, c, r0, r1);
            using var pen = new Pen(line, 1.2f);
            StrokeRing(g, pen, c, r0);
            StrokeRing(g, pen, c, r1);
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
