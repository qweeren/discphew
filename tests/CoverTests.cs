using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Discphew.Disc;
using Discphew.Print;

namespace Discphew.Tests
{
    /// <summary>Cover printing: disc IDs for files, Cover Art Archive listings, booklet imposition and the PDF.</summary>
    static partial class TestProgram
    {
        static void CoverLookups()
        {
            // The worked example from MusicBrainz's disc ID documentation (also libdiscid's test): six tracks whose
            // offsets are 150, 15363, 32314, 46592, 63414 and 80489, lead-out at 95462. Files of those lengths,
            // laid out the way the Burner writes them, must come out with the same ID.
            var disc = MusicBrainz.LayOut(new long[] { 15213, 16951, 14278, 16822, 17075, 14973 });
            Equal(95312, disc.LeadOutLba, "lead-out after the last track");
            Equal(32164, disc.Tracks[2].StartLba, "tracks back to back from LBA 0");
            Equal("49HHV7Eb8UKF3aQiNmu1GR8vKTY-", MusicBrainz.DiscId(disc), "disc ID of files laid out as a CD");
            Equal(300, MusicBrainz.LayOut(new long[] { 100 }).Tracks[0].Sectors, "short tracks padded to 4 seconds, as burned");

            var images = CoverArt.Parse(@"{""images"":[
                {""types"":[""Booklet""],""front"":false,""back"":false,""comment"":""2-3"",""image"":""http://coverartarchive.org/release/x/3.jpg"",""thumbnails"":{""250"":""http://a/3-250.jpg"",""500"":""http://a/3-500.jpg"",""small"":""http://a/3-250.jpg""},""id"":3},
                {""types"":[""Back"",""Spine""],""front"":false,""back"":true,""comment"":"""",""image"":""http://coverartarchive.org/release/x/2.jpg"",""thumbnails"":{""1200"":""http://a/2-1200.jpg""},""id"":2},
                {""types"":[],""front"":true,""back"":false,""comment"":"""",""image"":""http://coverartarchive.org/release/x/1.png"",""thumbnails"":{""large"":""http://a/1-500.jpg""},""id"":1}],
                ""release"":""https://musicbrainz.org/release/x""}");
            Equal(3, images.Count, "every image listed");
            Equal("caa-1", images[0].Id, "front first");
            Check(images[0].Is("Front"), "an untyped image flagged front counts as the front");
            Check(images[1].Is("back") && images[1].Is("Spine"), "types, any case");
            Equal("Booklet · 2-3", images[2].ToString(), "label with the archive's comment");
            Equal("https://coverartarchive.org/release/x/1.png", images[0].Url(0), "originals over https");
            Equal("https://a/1-500.jpg", images[0].Url(400), "smallest thumbnail big enough (\"large\" is 500)");
            Equal("https://coverartarchive.org/release/x/1.png", images[0].Url(1200), "the original when no thumbnail is big enough");
            Equal("https://a/3-500.jpg", images[2].Url(300), "numbered thumbnails");

            Equal("okcomputer", CoverArt.Plain("OK Computer (Collector's Edition)"), "edition suffix and punctuation dropped");
            Equal("bjorkhomogenic", CoverArt.Plain("Björk Homogénic"), "accents folded");

            var album = new CoverAlbum { Artist = "Tagged" };
            album.Tracks.Add(new CoverTrack { Number = 1, Title = "Kept", Length = TimeSpan.FromSeconds(61) });
            album.Tracks.Add(new CoverTrack { Number = 2, Length = TimeSpan.FromSeconds(62) });
            var release = new ReleaseMatch { Title = "From MB", Artist = "MB Artist", Date = "1999-05-01" };
            release.Tracks.Add(("Theirs", "MB Artist", TimeSpan.FromSeconds(99)));
            release.Tracks.Add(("Second", "Guest", TimeSpan.FromSeconds(98)));
            album.Fill(release);
            Equal("Tagged", album.Artist, "the album's own artist kept");
            Equal("From MB", album.Title, "missing title filled in");
            Equal("1999", album.Year, "year from the release date");
            Equal("Kept", album.Tracks[0].Title, "tagged track title kept");
            Equal("Second", album.Tracks[1].Title, "untitled track named from the release");
            Equal(TimeSpan.FromSeconds(62), album.Tracks[1].Length, "the disc's own lengths kept");
            Equal(TimeSpan.FromSeconds(123), album.Total, "total running time");
        }

        static void BookletImposition()
        {
            string Show(int pages) => string.Join(" ", CoverRender.Impose(pages).Select(s => $"{s.left}|{s.right}"));
            Equal("3|0 1|2", Show(4), "4 pages: back | front outside, 2 | 3 inside");
            Equal("7|0 1|6 5|2 3|4", Show(8), "8 pages on two nested sheets");
            Equal("1|0 -1|-1", Show(2), "padded with blanks to a whole sheet, the last page kept on the back");
            Equal("5|0 1|-1 -1|2 3|4", Show(6), "6 pages: the blanks go before the back cover");
            Equal("-1|0 -1|-1", Show(1), "a front cover alone");
            foreach (int n in new[] { 4, 8, 12, 16, 20 })
            {
                var all = CoverRender.Impose(n).SelectMany(s => new[] { s.left, s.right }).OrderBy(i => i).ToList();
                Check(all.SequenceEqual(Enumerable.Range(0, n)), $"{n} pages: each printed once");
            }

            using var spread = new Bitmap(400, 200);
            using var single = new Bitmap(200, 200);
            var pages = CoverRender.BookletPages(single, new[] { spread, single });
            Equal(4, pages.Count, "front, a spread cut in two, a single page");
            Equal(200, pages[1].Width, "half a spread is one page wide");
            foreach (var p in pages) p.Dispose();
        }

        static void CoverPdf()
        {
            var album = new CoverAlbum { Artist = "Some Band", Title = "An Album (Remastered)", Year = "2001" };
            for (int i = 1; i <= 23; i++)
                album.Tracks.Add(new CoverTrack { Number = i, Title = "A song called number " + i, Length = TimeSpan.FromSeconds(150 + i * 7) });

            using var art = new Bitmap(600, 600);
            using (var g = Graphics.FromImage(art))
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 600, 600), Color.OrangeRed, Color.MidnightBlue, 45f))
                g.FillRectangle(brush, 0, 0, 600, 600);
            using var backOnly = new Bitmap(1380, 1180);
            Check(CoverRender.IsBackOnly(backOnly), "a 138 × 118 scan is a back panel");
            using (var full = new Bitmap(1510, 1180)) Check(!CoverRender.IsBackOnly(full), "a 151 × 118 scan is the whole inlay");

            double k = Templates.PrintScale;
            var sheet = new CoverRender.Sheet { PaperWidth = 215.9, PaperHeight = 279.4, Caption = "Some Band – An Album" };
            sheet.Front = CoverRender.Render(CoverPart.Front, art, new Placement { Zoom = 1.1, X = 2, Angle = 1.5 }, album, art, k);
            sheet.Tray = CoverRender.Render(CoverPart.Tray, null, null, album, art, k);
            sheet.Disc = CoverRender.Render(CoverPart.Disc, null, null, album, art, k);
            Equal((int)Math.Round(126 * k), sheet.Front.Width, "front: 120 mm plus 3 mm bleed each side, at 300 DPI");
            Equal((int)Math.Round(157 * k), sheet.Tray.Width, "tray: 151 mm plus bleed");
            Equal((int)Math.Round(124 * k), sheet.Tray.Height, "tray: 118 mm plus bleed");
            var hole = sheet.Disc.GetPixel(sheet.Disc.Width / 2, sheet.Disc.Height / 2);
            Check(hole.R == 255 && hole.G == 255 && hole.B == 255, "the disc label's hole is left blank");
            var label = sheet.Disc.GetPixel(sheet.Disc.Width / 2, (int)(sheet.Disc.Height / 2 + 40 * k));
            Check(!(label.R == 255 && label.G == 255 && label.B == 255), "the disc label is printed between hole and rim");
            sheet.BookletSides.Add(CoverRender.BookletSide(null, CoverRender.BookletFront(sheet.Front), k));
            sheet.BookletSides.Add(CoverRender.BookletSide(art, art, k));
            Equal((int)Math.Round(246 * k), sheet.BookletSides[0].Width, "booklet side: two pages plus bleed");

            var pdf = CoverRender.Export(sheet);
            var text = Encoding.GetEncoding("ISO-8859-1").GetString(pdf);
            Check(text.StartsWith("%PDF-1.4\n"), "PDF header");
            Check(text.TrimEnd().EndsWith("%%EOF"), "PDF trailer");
            Equal(4, Regex.Matches(text, @"/Type /Page\b").Count, "front + tray, disc, two booklet sides");
            Check(text.Contains("/MediaBox [0 0 612 792]"), "US Letter pages in points");
            Check(text.Contains("/MediaBox [0 0 792 612]"), "booklet sides on landscape pages");
            Equal(5, Regex.Matches(text, @"/Filter /DCTDecode").Count, "every part a JPEG");
            Check(text.Contains("(Some Band \\226 An Album"), "caption in WinAnsi");

            // The cross-reference table must point at every object exactly.
            int xref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
            Check(text.Substring(xref).StartsWith("xref\n"), "startxref points at the table");
            var entries = Regex.Matches(text.Substring(xref), @"(\d{10}) 00000 n \n").Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).ToList();
            int size = int.Parse(Regex.Match(text, @"/Size (\d+)").Groups[1].Value);
            Equal(size - 1, entries.Count, "an entry per object");
            Check(entries.Select((off, i) => text.Substring(off).StartsWith($"{i + 1} 0 obj\n")).All(ok => ok), "every offset lands on its object");

            sheet.Front.Dispose(); sheet.Tray.Dispose(); sheet.Disc.Dispose();
            foreach (var b in sheet.BookletSides) b.Dispose();
        }
    }
}
