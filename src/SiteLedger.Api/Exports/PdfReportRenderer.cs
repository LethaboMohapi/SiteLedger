using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SiteLedger.Core;

namespace SiteLedger.Api.Exports;

public static class PdfReportRenderer
{
    private sealed class FontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) => new("SiteLedgerRoboto");
        public byte[] GetFont(string faceName)
        {
            using var stream = typeof(PdfReportRenderer).Assembly.GetManifestResourceStream("SiteLedger.Api.Fonts.Roboto-Regular.ttf")
                ?? throw new InvalidOperationException("Embedded PDF font is unavailable.");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
    }
    private static readonly object FontLock = new();
    private static void ConfigureFont()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new FontResolver();
        }
    }

    public static byte[] Render(Project project, SiteReport report)
    {
        ConfigureFont();
        using var document = new PdfDocument();
        document.Info.Title = "SiteLedger site report";
        var regular = new XFont("SiteLedgerRoboto", 10, XFontStyleEx.Regular);
        var title = new XFont("SiteLedgerRoboto", 20, XFontStyleEx.Regular);
        var page = document.AddPage();
        var graphics = XGraphics.FromPdfPage(page);
        double y = 48;
        try
        {
            void NewPage()
            {
                graphics.Dispose(); page = document.AddPage(); graphics = XGraphics.FromPdfPage(page); y = 48;
            }
            void Line(string text, XFont font)
            {
                if (y + font.Size + 7 > page.Height.Point - 48) NewPage();
                graphics.DrawString(text, font, XBrushes.Black, new XRect(48, y, page.Width.Point - 96, font.Size + 7), XStringFormats.TopLeft);
                y += font.Size + 7;
            }
            void Paragraph(string text)
            {
                var clean = new string(text.Select(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t' ? ' ' : c).ToArray()).Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
                var width = page.Width.Point - 96;
                foreach (var para in clean.Split('\n'))
                {
                    var line = new StringBuilder();
                    foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var candidate = line.Length == 0 ? word : line + " " + word;
                        if (graphics.MeasureString(candidate, regular).Width <= width) { line.Clear().Append(candidate); continue; }
                        if (line.Length > 0) { Line(line.ToString(), regular); line.Clear(); }
                        foreach (var rune in word.EnumerateRunes())
                        {
                            var next = line.ToString() + rune.ToString();
                            if (line.Length > 0 && graphics.MeasureString(next, regular).Width > width) { Line(line.ToString(), regular); line.Clear(); }
                            line.Append(rune.ToString());
                        }
                    }
                    Line(line.ToString(), regular);
                }
                y += 8;
            }
            Line("Site report", title); y += 10;
            Paragraph($"Project: {project.Name}\nClient: {project.ClientName}\nSite: {project.SiteAddress}");
            Paragraph($"Date: {report.ReportDate:yyyy-MM-dd}\nWeather: {report.Weather}\nWorkers on site: {report.WorkersOnSite}\nStatus: {report.Status}");
            Line("Work summary", regular); Paragraph(report.WorkSummary);
            Line("Issues", regular); Paragraph(report.Issues);
            if (report.ReviewComment is not null) { Line("Review comment", regular); Paragraph(report.ReviewComment); }
        }
        finally { graphics.Dispose(); }
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }
}
