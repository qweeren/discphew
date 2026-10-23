using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Discphew.Print
{
    /// <summary>
    /// Just enough PDF for print templates: pages of any size, JPEG images placed as-is (PDF reads JPEG natively),
    /// hairlines and circles for crop and fold marks, and a line of Helvetica (built into every reader, so
    /// nothing is embedded). Positions are in millimetres from the top-left corner of the page.
    /// </summary>
    public sealed class PdfWriter
    {
        sealed class Page
        {
            public double Width, Height;
            public readonly StringBuilder Content = new StringBuilder();
            public readonly List<int> Images = new List<int>();
        }

        const double Pt = 72 / 25.4;        // points per millimetre
        readonly List<byte[]> objects = new List<byte[]>();      // object n is objects[n - 1]; 1 and 2 are filled in last
        readonly List<Page> pages = new List<Page>();
        Page page;

        public PdfWriter() { objects.Add(null); objects.Add(null); }

        public int PageCount => pages.Count;

        public void AddPage(double widthMm, double heightMm)
        {
            page = new Page { Width = widthMm, Height = heightMm };
            pages.Add(page);
        }

        static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        double X(double mm) => mm * Pt;
        double Y(double mm) => (page.Height - mm) * Pt;

        int Add(byte[] body) { objects.Add(body); return objects.Count; }
        int Add(string body) => Add(Encoding.ASCII.GetBytes(body));

        static byte[] Stream(string dict, byte[] data)
        {
            var ms = new MemoryStream();
            var head = Encoding.ASCII.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            ms.Write(head, 0, head.Length);
            ms.Write(data, 0, data.Length);
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            ms.Write(tail, 0, tail.Length);
            return ms.ToArray();
        }

        /// <summary>A baseline JPEG (RGB) of <paramref name="pxWidth"/> × <paramref name="pxHeight"/> pixels, stretched over the given box.</summary>
        public void Image(byte[] jpeg, int pxWidth, int pxHeight, double x, double y, double width, double height)
        {
            int obj = Add(Stream($"/Type /XObject /Subtype /Image /Width {pxWidth} /Height {pxHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg));
            page.Images.Add(obj);
            page.Content.Append($"q {N(width * Pt)} 0 0 {N(height * Pt)} {N(X(x))} {N(Y(y + height))} cm /Im{obj} Do Q\n");
        }

        /// <param name="gray">0 black … 1 white.</param>
        public void Line(double x1, double y1, double x2, double y2, double widthPt = 0.25, double gray = 0, bool dashed = false)
        {
            page.Content.Append($"q {N(gray)} G {N(widthPt)} w {(dashed ? "[2 1.5] 0 d " : "")}{N(X(x1))} {N(Y(y1))} m {N(X(x2))} {N(Y(y2))} l S Q\n");
        }

        public void Circle(double cx, double cy, double r, double widthPt = 0.25, double gray = 0)
        {
            // Four Bézier quarter-arcs; 0.5523 puts the control points where they trace a circle to within 0.03 %.
            double k = r * 0.5523;
            var sb = page.Content;
            sb.Append($"q {N(gray)} G {N(widthPt)} w {N(X(cx + r))} {N(Y(cy))} m ");
            sb.Append($"{N(X(cx + r))} {N(Y(cy - k))} {N(X(cx + k))} {N(Y(cy - r))} {N(X(cx))} {N(Y(cy - r))} c ");
            sb.Append($"{N(X(cx - k))} {N(Y(cy - r))} {N(X(cx - r))} {N(Y(cy - k))} {N(X(cx - r))} {N(Y(cy))} c ");
            sb.Append($"{N(X(cx - r))} {N(Y(cy + k))} {N(X(cx - k))} {N(Y(cy + r))} {N(X(cx))} {N(Y(cy + r))} c ");
            sb.Append($"{N(X(cx + k))} {N(Y(cy + r))} {N(X(cx + r))} {N(Y(cy + k))} {N(X(cx + r))} {N(Y(cy))} c S Q\n");
        }

        /// <summary>One line of Helvetica centred on <paramref name="x"/>, its baseline at <paramref name="y"/>. Latin-1 only; anything else prints as '?'.</summary>
        public void TextCentered(double x, double y, string text, double sizePt, double gray = 0)
        {
            // Helvetica's widths aren't to hand, so centre on an average glyph of half an em.
            double width = text.Length * sizePt * 0.5 / Pt;
            page.Content.Append($"BT {N(gray)} g /F1 {N(sizePt)} Tf {N(X(x - width / 2))} {N(Y(y))} Td ({Escape(text)}) Tj ET\n");
        }

        // The punctuation WinAnsi keeps where Latin-1 has control codes.
        static readonly Dictionary<char, int> WinAnsi = new Dictionary<char, int>
        {
            ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['…'] = 0x85, ['€'] = 0x80,
        };

        static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
                else if (c >= 32 && c < 127) sb.Append(c);
                else if (c >= 0xA0 && c <= 0xFF) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));   // WinAnsi matches Latin-1 here
                else if (WinAnsi.TryGetValue(c, out int code)) sb.Append('\\').Append(Convert.ToString(code, 8));
                else sb.Append('?');
            }
            return sb.ToString();
        }

        public byte[] ToArray()
        {
            int font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            var kids = new List<int>();
            foreach (var p in pages)
            {
                int content = Add(Stream("", Encoding.ASCII.GetBytes(p.Content.ToString())));
                string xobjects = string.Concat(p.Images.Select(i => $"/Im{i} {i} 0 R "));
                kids.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(p.Width * Pt)} {N(p.Height * Pt)}] " +
                             $"/Resources << /XObject << {xobjects}>> /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>"));
            }
            objects[0] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
            objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => k + " 0 R"))}] /Count {kids.Count} >>");

            var ms = new MemoryStream();
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
            W("%PDF-1.4\n");
            ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);      // marks the file as binary for transfer tools
            var offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = ms.Position;
                W($"{i + 1} 0 obj\n");
                ms.Write(objects[i], 0, objects[i].Length);
                W("\nendobj\n");
            }
            long xref = ms.Position;
            W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) W($"{o:D10} 00000 n \n");
            W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
