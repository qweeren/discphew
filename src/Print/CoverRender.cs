using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using Discphew.Disc;

namespace Discphew.Print
{
    public enum CoverPart { Front, Tray, Disc }

    /// <summary>How artwork sits in its panel: a zoom over the fit that just covers it, an offset in mm, a turn in degrees.</summary>
    public sealed class Placement
    {
        public double Zoom = 1, X, Y, Angle;
        public bool IsDefault => Zoom == 1 && X == 0 && Y == 0 && Angle == 0;
        public void Reset() { Zoom = 1; X = Y = Angle = 0; }
    }

    /// <summary>Jewel case print sizes, in millimetres.</summary>
    public static class Templates
    {
        public const double Bleed = 3;          // artwork carries on this far past every cut
        public const double Spine = 6.5;        // each side of the tray inlay
        public const double DiscOuter = 118, DiscHole = 41;
        public const double Dpi = 300;
        public static readonly double PrintScale = Dpi / 25.4;     // pixels per mm in the PDF

        public static SizeF Trim(CoverPart p) => p switch
        {
            CoverPart.Front => new SizeF(120, 120),
            CoverPart.Tray => new SizeF(151, 118),
            _ => new SizeF((float)DiscOuter, (float)DiscOuter),
        };

        public static SizeF WithBleed(CoverPart p) { var t = Trim(p); return new SizeF(t.Width + 2 * (float)Bleed, t.Height + 2 * (float)Bleed); }

        public const double BookletPage = 120;
    }

    /// <summary>
    /// Draws each part of a jewel case insert at any scale (the preview and the 300 DPI print use the same code),
    /// generates a back inlay from the track list when there's no scan of one, and lays the parts out as a PDF.
    /// </summary>
    public static class CoverRender
    {
        static FontFamily family;
        static FontFamily Family => family ??= FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe UI") ?? FontFamily.GenericSansSerif;

        static Font F(double pt, double k, bool bold = false) => new Font(Family, (float)Math.Max(1, pt * 25.4 / 72 * k), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

        static Bitmap Canvas(SizeF mm, double k, out Graphics g)
        {
            var b = new Bitmap(Math.Max(1, (int)Math.Round(mm.Width * k)), Math.Max(1, (int)Math.Round(mm.Height * k)), PixelFormat.Format24bppRgb);
            g = Graphics.FromImage(b);
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return b;
        }

        /// <summary>
        /// One part with its bleed, at <paramref name="k"/> pixels per mm. For the tray, <paramref name="art"/> may be a
        /// scan of the whole inlay, of the back panel only (the spines are then made to match), or null to generate one
        /// over a blurred <paramref name="backdrop"/> (the front cover).
        /// </summary>
        public static Bitmap Render(CoverPart part, Image art, Placement place, CoverAlbum album, Image backdrop, double k)
        {
            var size = Templates.WithBleed(part);
            var bmp = Canvas(size, k, out var g);
            using (g)
            {
                var full = new RectangleF(0, 0, bmp.Width, bmp.Height);
                float b = (float)(Templates.Bleed * k);
                switch (part)
                {
                    case CoverPart.Front:
                        DrawArt(g, art, place, full, k);
                        break;

                    case CoverPart.Disc:
                        DrawArt(g, art ?? backdrop, place, full, k);
                        // White outside the label and in the hole; the art runs a bleed past both cuts.
                        float cx = bmp.Width / 2f, cy = bmp.Height / 2f;
                        float outer = (float)((Templates.DiscOuter / 2 + Templates.Bleed) * k), hole = (float)((Templates.DiscHole / 2 - Templates.Bleed) * k);
                        using (var mask = new GraphicsPath(FillMode.Alternate))
                        {
                            mask.AddRectangle(new RectangleF(-1, -1, bmp.Width + 2, bmp.Height + 2));
                            mask.AddEllipse(cx - outer, cy - outer, outer * 2, outer * 2);
                            g.FillPath(Brushes.White, mask);
                        }
                        g.FillEllipse(Brushes.White, cx - hole, cy - hole, hole * 2, hole * 2);
                        break;

                    case CoverPart.Tray:
                        float spine = (float)(Templates.Spine * k);
                        var back = new RectangleF(b + spine, 0, bmp.Width - 2 * (b + spine), bmp.Height);
                        if (art != null && !IsBackOnly(art))
                        {
                            DrawArt(g, art, place, full, k);         // a full inlay scan has its own spines
                            break;
                        }
                        Backdrop(g, art ?? backdrop, full);
                        if (art != null) DrawArt(g, art, place, back, k);
                        else TrackList(g, album, back, k);
                        Spines(g, album, full, k);
                        break;
                }
            }
            return bmp;
        }

        /// <summary>A scan of just the back panel (138 × 118) rather than the whole inlay with its spines (151 × 118).</summary>
        public static bool IsBackOnly(Image art) => art.Width / (double)art.Height < (138.0 / 118 + 151.0 / 118) / 2;

        /// <summary>The art centred in <paramref name="box"/>, scaled to just cover it, then moved, zoomed and turned as placed.</summary>
        static void DrawArt(Graphics g, Image img, Placement place, RectangleF box, double k)
        {
            if (img == null) return;
            place ??= new Placement();
            var state = g.Save();
            g.SetClip(box);
            float fit = (float)(Math.Max(box.Width / img.Width, box.Height / img.Height) * place.Zoom);
            float w = img.Width, h = img.Height;
            var corners = new[] { new PointF(-w / 2, -h / 2), new PointF(w / 2, -h / 2), new PointF(-w / 2, h / 2) };
            using (var m = new Matrix())
            {
                m.Translate(box.X + box.Width / 2 + (float)(place.X * k), box.Y + box.Height / 2 + (float)(place.Y * k));
                m.Rotate((float)place.Angle);
                m.Scale(fit, fit);
                m.TransformPoints(corners);
            }
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);      // no faint border where the bicubic filter reads past the edge
            g.DrawImage(img, corners, new RectangleF(0, 0, w, h), GraphicsUnit.Pixel, attrs);
            g.Restore(state);
        }

        /// <summary>The image blurred right out and darkened, so light text reads on it and it still matches the cover.</summary>
        static void Backdrop(Graphics g, Image img, RectangleF box)
        {
            if (img == null)
            {
                using var plain = new SolidBrush(Color.FromArgb(0x1E, 0x20, 0x24));
                g.FillRectangle(plain, box);
                return;
            }
            // Shrinking to a few pixels averages the picture; stretching that back up is a blur that costs nothing.
            int sw = 18, sh = Math.Max(2, (int)Math.Round(sw * box.Height / box.Width));
            using var mid = new Bitmap(sw * 6, sh * 6, PixelFormat.Format24bppRgb);
            using (var mg = Graphics.FromImage(mid))
            {
                mg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawArt(mg, img, null, new RectangleF(0, 0, mid.Width, mid.Height), 1);
            }
            using var small = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.DrawImage(mid, new Rectangle(0, 0, sw, sh));
            }
            DrawArt(g, small, null, box, 1);
            using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(shade, box);
        }

        static readonly Color Ink = Color.FromArgb(0xF4, 0xF2, 0xEE);

        static void TrackList(Graphics g, CoverAlbum album, RectangleF area, double k)
        {
            if (album == null) return;
            float mm = (float)k;
            float x0 = area.X + 9 * mm, x1 = area.Right - 9 * mm, y = area.Y + (float)(Templates.Bleed + 9) * mm, bottom = area.Bottom - (float)(Templates.Bleed + 8) * mm;
            using var left = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            using var right = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            using var ink = new SolidBrush(Ink);
            using var soft = new SolidBrush(Color.FromArgb(185, Ink));
            using var faint = new SolidBrush(Color.FromArgb(130, Ink));

            using (var artistFont = F(12, k, true))
            using (var titleFont = F(9.5, k))
            {
                float h = artistFont.GetHeight(g);
                g.DrawString(album.Artist, artistFont, ink, new RectangleF(x0, y, x1 - x0, h * 1.2f), left);
                y += h * 1.2f;
                h = titleFont.GetHeight(g);
                g.DrawString(album.Title, titleFont, soft, new RectangleF(x0, y, x1 - x0, h * 1.2f), left);
                y += h * 1.2f + 5 * mm;
            }

            var bits = new List<string>();
            if (album.Year.Length > 0) bits.Add(album.Year);
            if (album.Tracks.Count > 0) bits.Add(album.Tracks.Count == 1 ? "1 track" : $"{album.Tracks.Count} tracks");
            if (album.Total > TimeSpan.Zero) bits.Add(Fmt.Time(album.Total));
            using (var footFont = F(7.5, k))
            {
                float h = footFont.GetHeight(g) * 1.2f;
                g.DrawString(string.Join("   ·   ", bits), footFont, faint, new RectangleF(x0, bottom - h, x1 - x0, h), left);
                bottom -= h + 4 * mm;
            }

            // The biggest type that fits every track, in one column or two.
            int n = album.Tracks.Count;
            if (n == 0) return;
            float gap = 7 * mm;
            double size = 8.5;
            int cols = 1, rows = n;
            for (; size >= 5; size -= 0.25)
            {
                float lineH = (float)(size * 1.55 * 25.4 / 72 * k);
                int fit = (int)Math.Floor((bottom - y) / lineH);
                if (fit >= n) { cols = 1; rows = n; break; }
                if (fit * 2 >= n) { cols = 2; rows = (n + 1) / 2; break; }
            }
            if (size < 5) { size = 5; cols = 2; rows = (n + 1) / 2; }
            float line = (float)(size * 1.55 * 25.4 / 72 * k);
            float colW = (x1 - x0 - gap * (cols - 1)) / cols;
            using var font = F(size, k);
            using var numFont = F(size, k, true);
            float numW = g.MeasureString("00", numFont).Width + 1.5f * mm;
            float durW = g.MeasureString("00:00", font).Width + 1 * mm;
            for (int i = 0; i < n; i++)
            {
                var t = album.Tracks[i];
                float cx = x0 + (i / rows) * (colW + gap), cy = y + (i % rows) * line;
                if (cy + line > bottom + 0.5f) continue;        // more tracks than will ever fit at 5 pt
                g.DrawString(t.Number.ToString(), numFont, faint, new RectangleF(cx, cy, numW - 1.5f * mm, line), right);
                string title = t.Title.Length > 0 ? t.Title : $"Track {t.Number}";
                if (t.Artist.Length > 0 && !t.Artist.Equals(album.Artist, StringComparison.OrdinalIgnoreCase)) title += "  ·  " + t.Artist;
                g.DrawString(title, font, ink, new RectangleF(cx + numW, cy, colW - numW - durW, line), left);
                if (t.Length > TimeSpan.Zero) g.DrawString(Fmt.Time(t.Length), font, soft, new RectangleF(cx + colW - durW, cy, durW, line), right);
            }
        }

        /// <summary>Artist and title along both spines: reading up the left one and down the right, as cases are shelved.</summary>
        static void Spines(Graphics g, CoverAlbum album, RectangleF full, double k)
        {
            if (album == null || album.Artist.Length + album.Title.Length == 0) return;
            float b = (float)(Templates.Bleed * k), spine = (float)(Templates.Spine * k);
            float length = full.Height - 2 * b - (float)(10 * k);
            using var bold = F(7, k, true);
            using var regular = F(7, k);
            using var ink = new SolidBrush(Ink);
            using var soft = new SolidBrush(Color.FromArgb(185, Ink));
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            string artist = album.Artist.ToUpperInvariant(), sep = album.Artist.Length > 0 && album.Title.Length > 0 ? "   ·   " : "";
            float artistW = Math.Min(length, g.MeasureString(artist + sep, bold, PointF.Empty, StringFormat.GenericTypographic).Width);
            float titleW = Math.Min(length - artistW, g.MeasureString(album.Title, regular, PointF.Empty, StringFormat.GenericTypographic).Width + (float)k);
            float h = bold.GetHeight(g) * 1.3f;
            foreach (var (x, angle) in new[] { (b + spine / 2, -90f), (full.Width - b - spine / 2, 90f) })
            {
                var state = g.Save();
                g.TranslateTransform(x, full.Height / 2);
                g.RotateTransform(angle);
                float start = -(artistW + titleW) / 2;
                g.DrawString(artist + sep, bold, ink, new RectangleF(start, -h / 2, artistW + (float)k, h), fmt);
                if (titleW > 0) g.DrawString(album.Title, regular, soft, new RectangleF(start + artistW, -h / 2, titleW, h), fmt);
                g.Restore(state);
            }
        }

        // ------------------------------------------------------------------ booklet

        /// <summary>
        /// The booklet's pages in reading order: the front cover, then the scanned pages, with two-page spreads
        /// (scanned open, twice as wide as tall) cut into their pages. The caller disposes them.
        /// </summary>
        public static List<Image> BookletPages(Image front, IEnumerable<Image> scans)
        {
            var pages = new List<Image>();
            if (front != null) pages.Add(new Bitmap(front));
            foreach (var s in scans)
            {
                if (s.Width / (double)s.Height < 1.5) { pages.Add(new Bitmap(s)); continue; }
                for (int half = 0; half < 2; half++)
                {
                    int w = s.Width / 2;
                    var page = new Bitmap(w, s.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(page)) g.DrawImage(s, new Rectangle(0, 0, w, s.Height), new Rectangle(half * w, 0, w, s.Height), GraphicsUnit.Pixel);
                    pages.Add(page);
                }
            }
            return pages;
        }

        /// <summary>
        /// Saddle-stitch imposition: which pages (0-based, -1 for a blank) go on the left and right of each printed side,
        /// for <paramref name="pages"/> pages padded to a multiple of four. The blanks go just before the last page, so
        /// the back cover stays on the back. Sides come in pairs, outside then inside of each sheet, outermost sheet
        /// first; nest the folded sheets in that order.
        /// </summary>
        public static List<(int left, int right)> Impose(int pages)
        {
            int n = (pages + 3) / 4 * 4;
            int P(int i) => pages < 2 ? (i < pages ? i : -1) : i < pages - 1 ? i : i == n - 1 ? pages - 1 : -1;
            var sides = new List<(int, int)>();
            for (int s = 0; s < n / 4; s++)
            {
                sides.Add((P(n - 1 - 2 * s), P(2 * s)));
                sides.Add((P(2 * s + 1), P(n - 2 - 2 * s)));
            }
            return sides;
        }

        /// <summary>One printed side of a booklet sheet: two pages either side of the fold, with bleed round the outside.</summary>
        public static Bitmap BookletSide(Image left, Image right, double k)
        {
            double b = Templates.Bleed, p = Templates.BookletPage;
            var bmp = Canvas(new SizeF((float)(2 * p + 2 * b), (float)(p + 2 * b)), k, out var g);
            using (g)
            {
                float half = (float)((p + b) * k);
                DrawArt(g, left, null, new RectangleF(0, 0, half, bmp.Height), k);
                DrawArt(g, right, null, new RectangleF(half, 0, bmp.Width - half, bmp.Height), k);
            }
            return bmp;
        }

        /// <summary>The front as rendered, less the bleed on its left: it becomes the right-hand page of the booklet's outside.</summary>
        public static Bitmap BookletFront(Bitmap renderedFront)
        {
            int cut = (int)Math.Round(renderedFront.Width * Templates.Bleed / Templates.WithBleed(CoverPart.Front).Width);
            return renderedFront.Clone(new Rectangle(cut, 0, renderedFront.Width - cut, renderedFront.Height), PixelFormat.Format24bppRgb);
        }

        // ------------------------------------------------------------------ PDF

        public sealed class Sheet
        {
            public double PaperWidth = 210, PaperHeight = 297;      // portrait; booklet sides go on landscape pages
            public Bitmap Front, Tray, Disc;                        // rendered at Templates.PrintScale; Front is null in booklet mode
            public readonly List<Bitmap> BookletSides = new List<Bitmap>();
            public string Caption = "";
        }

        const double Gap = 12, Mark = 5;        // between parts on a page (clear of each other's marks); crop mark length

        enum Piece { Square, Tray, Disc, Booklet }

        /// <summary>
        /// The print-ready PDF: the front booklet over the tray inlay on one page (or, in booklet mode, the tray over the
        /// disc label), the disc label on the next, then one landscape page per booklet side. Every part has crop marks
        /// a bleed outside its cut, and dashed fold marks at its spines or centre.
        /// </summary>
        public static byte[] Export(Sheet s)
        {
            var pdf = new PdfWriter();
            double pw = s.PaperWidth, ph = s.PaperHeight;
            const string actualSize = "print at actual size (100 %), not “fit to page”";
            var first = new List<(Bitmap, Piece)>();
            if (s.Front != null) first.Add((s.Front, Piece.Square));
            if (s.Tray != null) first.Add((s.Tray, Piece.Tray));
            if (s.Front == null && s.Disc != null) first.Add((s.Disc, Piece.Disc));
            if (first.Count > 0) Stack(pdf, pw, ph, first, (s.Caption.Length > 0 ? s.Caption + "   ·   " : "") + actualSize);
            if (s.Front != null && s.Disc != null) Stack(pdf, pw, ph, new List<(Bitmap, Piece)> { (s.Disc, Piece.Disc) }, "Disc label   ·   " + actualSize);
            for (int i = 0; i < s.BookletSides.Count; i++)
                Stack(pdf, ph, pw, new List<(Bitmap, Piece)> { (s.BookletSides[i], Piece.Booklet) },
                    $"Booklet sheet {i / 2 + 1} of {(s.BookletSides.Count + 1) / 2}, {(i % 2 == 0 ? "outside" : "inside")}   ·   print double-sided, flipping on the short edge, at actual size");
            return pdf.ToArray();
        }

        static void Stack(PdfWriter pdf, double pw, double ph, List<(Bitmap img, Piece kind)> items, string caption)
        {
            pdf.AddPage(pw, ph);
            double b = Templates.Bleed;
            var trims = items.Select(i => (w: i.img.Width / Templates.PrintScale - 2 * b, h: i.img.Height / Templates.PrintScale - 2 * b)).ToList();
            double y = (ph - trims.Sum(t => t.h) - Gap * (items.Count - 1)) / 2;
            for (int i = 0; i < items.Count; i++)
            {
                var (img, kind) = items[i];
                var (w, h) = trims[i];
                double x = (pw - w) / 2;
                pdf.Image(Jpeg(img), img.Width, img.Height, x - b, y - b, w + 2 * b, h + 2 * b);
                CropMarks(pdf, x, y, w, h);
                if (kind == Piece.Tray) { Fold(pdf, x + Templates.Spine, y, h); Fold(pdf, x + w - Templates.Spine, y, h); }
                if (kind == Piece.Booklet) Fold(pdf, x + w / 2, y, h);
                if (kind == Piece.Disc)
                {
                    // Cut lines for scissors; a die-cut label sheet ignores them.
                    pdf.Circle(x + w / 2, y + h / 2, Templates.DiscOuter / 2, 0.2, 0.55);
                    pdf.Circle(x + w / 2, y + h / 2, Templates.DiscHole / 2, 0.2, 0.55);
                }
                y += h + Gap;
            }
            pdf.TextCentered(pw / 2, ph - 4, caption, 6.5, 0.35);
        }

        static void CropMarks(PdfWriter pdf, double x, double y, double w, double h)
        {
            double o = Templates.Bleed;
            foreach (var cx in new[] { x, x + w })
                foreach (var cy in new[] { y, y + h })
                {
                    double sx = cx == x ? -1 : 1, sy = cy == y ? -1 : 1;
                    pdf.Line(cx + sx * o, cy, cx + sx * (o + Mark), cy);
                    pdf.Line(cx, cy + sy * o, cx, cy + sy * (o + Mark));
                }
        }

        static void Fold(PdfWriter pdf, double x, double y, double h)
        {
            double o = Templates.Bleed;
            pdf.Line(x, y - o - Mark, x, y - o, 0.25, 0, true);
            pdf.Line(x, y + h + o, x, y + h + o + Mark, 0.25, 0, true);
        }

        public static byte[] Jpeg(Bitmap b, long quality = 92)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            using var ms = new MemoryStream();
            b.Save(ms, codec, p);
            return ms.ToArray();
        }

        /// <summary>
        /// Decodes an image into a plain bitmap that doesn't keep the bytes open, shrunk to at most <paramref name="maxSide"/>
        /// pixels across if it's bigger (a booklet's worth of 6000-pixel scans would otherwise fill memory). Null if it isn't an image.
        /// </summary>
        public static Bitmap Decode(byte[] data, int maxSide = 0)
        {
            if (data == null) return null;
            try
            {
                using var ms = new MemoryStream(data);
                using var tmp = Image.FromStream(ms);
                double shrink = maxSide > 0 ? Math.Min(1, maxSide / (double)Math.Max(tmp.Width, tmp.Height)) : 1;
                if (shrink >= 1) return new Bitmap(tmp);
                var b = new Bitmap(Math.Max(1, (int)(tmp.Width * shrink)), Math.Max(1, (int)(tmp.Height * shrink)), PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(b);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(tmp, new Rectangle(0, 0, b.Width, b.Height));
                return b;
            }
            catch (ArgumentException) { return null; }
            catch (OutOfMemoryException) { return null; }      // GDI+'s way of saying "not an image I know"
        }
    }
}
