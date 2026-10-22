using System.Globalization;
using System.IO;
using System.Text;

namespace Nasaq.Core;

/// <summary>
/// Minimal, dependency-free PDF 1.4 writer: Helvetica body text, automatic
/// pagination and word wrapping, plus document metadata.
/// </summary>
public static class PdfWriter
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 56;
    private const double BodySize = 11;
    private const double Leading = 15.5;
    private const int LinesPerPage = 46;

    public static byte[] Build(string text, string title)
    {
        var lines = WrapLines(text);
        var pages = new List<List<string>>();
        for (var i = 0; i < lines.Count; i += LinesPerPage)
            pages.Add(lines.GetRange(i, Math.Min(LinesPerPage, lines.Count - i)));
        if (pages.Count == 0) pages.Add(new List<string>());

        var objects = new List<byte[]>();
        // 1 catalog, 2 pages tree, 3/4 fonts, 5 info
        var pageObjectStart = 6;
        var pageIds = Enumerable.Range(0, pages.Count).Select(i => pageObjectStart + i * 2).ToList();

        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));

        var safeTitle = EscapeText(title);
        objects.Add(Ascii($"<< /Title ({safeTitle}) /Producer (Nasaq {AppInfo.Version}) /Creator (Nasaq) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>"));

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var contentId = pageIds[pageIndex] + 1;
            var content = BuildPageContent(pages[pageIndex], pageIndex + 1, pages.Count);
            var contentBytes = Ascii(content);
            objects.Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth.ToString("0.##", CultureInfo.InvariantCulture)} {PageHeight.ToString("0.##", CultureInfo.InvariantCulture)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
            objects.Add(Ascii($"<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream"));
        }

        var output = new MemoryStream();
        var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n");
        writer.Write("% Nasaq generated document\n");
        writer.Flush();

        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            writer.Flush();
            offsets[i + 1] = output.Position;
            writer.Write($"{i + 1} 0 obj\n");
            writer.Flush();
            output.Write(objects[i], 0, objects[i].Length);
            writer.Write("\nendobj\n");
            writer.Flush();
        }

        writer.Flush();
        var xref = output.Position;
        writer.Write($"xref\n0 {objects.Count + 1}\n");
        writer.Write("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            writer.Write($"{offsets[i]:D10} 00000 n \n");
        writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return output.ToArray();
    }

    private static string BuildPageContent(List<string> pageLines, int pageNumber, int pageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("q");
        sb.AppendLine("0.06 0.07 0.09 rg");
        sb.AppendLine("BT");
        sb.AppendLine($"/F1 {BodySize.ToString(CultureInfo.InvariantCulture)} Tf");
        sb.AppendLine($"{Leading.ToString(CultureInfo.InvariantCulture)} TL");
        sb.AppendLine($"{Margin.ToString(CultureInfo.InvariantCulture)} {(PageHeight - Margin - BodySize).ToString("0.##", CultureInfo.InvariantCulture)} Td");
        foreach (var line in pageLines)
        {
            if (line.Length == 0)
            {
                sb.AppendLine("T*");
                continue;
            }
            sb.Append('(').Append(EscapeText(line)).AppendLine(") Tj");
            sb.AppendLine("T*");
        }
        sb.AppendLine("ET");

        // Footer: page number and brand.
        sb.AppendLine("0.55 0.58 0.63 rg");
        sb.AppendLine("BT");
        sb.AppendLine("/F1 8 Tf");
        sb.AppendLine($"{Margin} 32 Td");
        sb.Append("(Nasaq) Tj");
        sb.AppendLine("ET");
        sb.AppendLine("BT");
        sb.AppendLine("/F1 8 Tf");
        sb.AppendLine($"{(PageWidth - Margin - 40).ToString("0.##", CultureInfo.InvariantCulture)} 32 Td");
        sb.Append('(').Append(pageNumber).Append(" / ").Append(pageCount).AppendLine(") Tj");
        sb.AppendLine("ET");
        sb.AppendLine("Q");
        return sb.ToString();
    }

    private static List<string> WrapLines(string text)
    {
        var result = new List<string>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

        foreach (var paragraph in normalized.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder();
            foreach (var word in words)
            {
                var w = word;
                while (w.Length > 86)
                {
                    if (line.Length > 0)
                    {
                        result.Add(line.ToString());
                        line.Clear();
                    }
                    result.Add(w[..86]);
                    w = w[86..];
                }
                if (line.Length == 0) line.Append(w);
                else if (line.Length + 1 + w.Length <= 86) line.Append(' ').Append(w);
                else
                {
                    result.Add(line.ToString());
                    line.Clear();
                    line.Append(w);
                }
            }
            result.Add(line.ToString());
        }

        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static string EscapeText(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var raw in value)
        {
            var c = raw switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2013' or '\u2014' => '-',
                '\u2026' => '.',
                '\u2022' => '-',
                '\u00A0' => ' ',
                _ => raw,
            };
            if (c is '\r' or '\n') sb.Append(' ');
            else if (c < 32) sb.Append(' ');
            else if (c > 255) sb.Append('?');
            else if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);
}
