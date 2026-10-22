using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nasaq.Core;

/// <summary>
/// Text-oriented document conversion. Documents are read into plain text and
/// written back out as PDF, DOCX, HTML, Markdown or TXT using native writers,
/// so no office suite or third-party library is required.
/// </summary>
public static class DocumentConverter
{
    public static Task<string> ConvertAsync(string source, OutputFormat target,
        ConversionOptions options, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress(0.15, "Reading document"));
            var (text, title) = Extract(source);
            if (string.IsNullOrWhiteSpace(text))
                throw new ConversionException("No readable text was found in this document.",
                    "Extracted text was empty.");

            ct.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress(0.55, "Writing " + target.Label));

            var ext = target.Extension;
            var temp = Path.Combine(options.OutputDirectory, $".nasaq-{Guid.NewGuid():N}.{ext}");
            switch (target.Id)
            {
                case "txt":
                    File.WriteAllText(temp, text, new UTF8Encoding(false));
                    break;
                case "md":
                    File.WriteAllText(temp, BuildMarkdown(text, title), new UTF8Encoding(false));
                    break;
                case "html":
                    File.WriteAllText(temp, BuildHtml(text, title), new UTF8Encoding(false));
                    break;
                case "docx":
                    BuildDocx(temp, text, title);
                    break;
                case "pdf":
                    File.WriteAllBytes(temp, PdfWriter.Build(text, title));
                    break;
                default:
                    throw new ConversionException($"Document conversion to {target.Label} is not supported.");
            }

            progress?.Report(new ConversionProgress(0.95, "Saving"));
            return temp;
        }, ct);
    }

    private static (string Text, string Title) Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(path);

        switch (ext)
        {
            case ".pdf":
                throw new ConversionException(
                    "PDF files can't be read as a source yet. Convert from DOCX, TXT, HTML, Markdown or RTF instead.",
                    "PDF text extraction is not implemented.",
                    hint: "PDF is supported as an output format.");
            case ".doc":
                throw new ConversionException(
                    "Legacy .doc files aren't supported. Re-save the file as .docx and try again.",
                    "Binary Word 97-2003 files are not supported.");
            case ".docx":
            {
                var (docText, docTitle) = ExtractDocx(path);
                if (!string.IsNullOrWhiteSpace(docTitle)) title = docTitle!;
                return (docText, title);
            }
            case ".html":
            case ".htm":
            {
                var html = ReadText(path);
                var heading = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (heading.Success)
                {
                    var h = WebUtility.HtmlDecode(Regex.Replace(heading.Groups[1].Value, @"\s+", " ")).Trim();
                    if (h.Length > 0) title = h;
                }
                return (HtmlToText(html), title);
            }
            case ".rtf":
                return (RtfToText(ReadText(path)), title);
            default:
            {
                var text = ReadText(path);
                if (text.IndexOf('\0') >= 0)
                    throw new ConversionException("This file looks like binary data, not text.",
                        "NUL characters detected in the source.");
                return (text, title);
            }
        }
    }

    private static string ReadText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 40L * 1024 * 1024)
            throw new ConversionException("This document is too large to process (over 40 MB).",
                $"File size {info.Length} bytes.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.Length > 8_000_000) text = text[..8_000_000];
        return text;
    }

    private static (string Text, string? Title) ExtractDocx(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml")
                        ?? throw new ConversionException("The Word document is missing its main content.",
                            "word/document.xml not found.");

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            var paragraphs = new List<string>();
            foreach (var paragraph in doc.Descendants(w + "p"))
            {
                var sb = new StringBuilder();
                foreach (var node in paragraph.Descendants())
                {
                    if (node.Name == w + "t") sb.Append(node.Value);
                    else if (node.Name == w + "tab") sb.Append('\t');
                    else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
                }
                paragraphs.Add(sb.ToString());
            }

            var text = string.Join("\n", paragraphs);
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

            string? title = null;
            var core = archive.GetEntry("docProps/core.xml");
            if (core is not null)
            {
                using var coreStream = core.Open();
                var coreDoc = XDocument.Load(coreStream);
                XNamespace dc = "http://purl.org/dc/elements/1.1/";
                title = coreDoc.Descendants(dc + "title").FirstOrDefault()?.Value?.Trim();
            }

            return (text, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        catch (ConversionException) { throw; }
        catch (InvalidDataException ex)
        {
            throw new ConversionException("The Word document is corrupt or isn't a valid .docx file.",
                ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new ConversionException("The Word document could not be read. It may be corrupt or password-protected.",
                ex.Message, ex);
        }
    }

    public static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<script\b[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<style\b[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<head\b[^>]*>.*?</head>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</(p|div|section|article|header|footer|h[1-6]|tr|blockquote)>", "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li\b[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</li>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<td\b[^>]*>", "\t", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        var text = WebUtility.HtmlDecode(html);
        text = text.Replace('\u00A0', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string RtfToText(string rtf)
    {
        var text = Regex.Replace(rtf, @"\\'([0-9a-fA-F]{2})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        text = Regex.Replace(text, @"\\u(-?\d+)\??",
            m => char.ConvertFromUtf32((Convert.ToInt32(m.Groups[1].Value) + 65536) % 65536));
        text = Regex.Replace(text, @"\\par[d]?\b", "\n");
        text = Regex.Replace(text, @"\\line\b", "\n");
        text = Regex.Replace(text, @"\\tab\b", "\t");
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", "");
        text = text.Replace("{", "").Replace("}", "").Replace("\\", "");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string BuildMarkdown(string text, string title)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        if (body.StartsWith("#", StringComparison.Ordinal)) return body + "\n";
        return $"# {title}\n\n{body}\n";
    }

    private static string BuildHtml(string text, string title)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var body = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var safe = WebUtility.HtmlEncode(paragraph.Trim('\n')).Replace("\n", "<br />");
            if (safe.Length == 0) continue;
            body.Append("    <p>").Append(safe).AppendLine("</p>");
        }

        var safeTitle = WebUtility.HtmlEncode(title);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>{{safeTitle}}</title>
<style>
  :root { color-scheme: light; }
  body {
    margin: 0 auto; padding: 56px 28px 96px; max-width: 760px;
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
    font-size: 16px; line-height: 1.75; color: #1f2328; background: #ffffff;
  }
  h1 { font-size: 30px; line-height: 1.25; margin: 0 0 28px; }
  p { margin: 0 0 18px; }
  body > p:first-of-type { color: #5b6470; }
  footer { margin-top: 64px; padding-top: 18px; border-top: 1px solid #e6e8ec;
           color: #8a929e; font-size: 12px; letter-spacing: .02em; }
</style>
</head>
<body>
    <h1>{{safeTitle}}</h1>
{{body}}    <footer>Converted with Nasaq</footer>
</body>
</html>
""";
    }

    private static void BuildDocx(string path, string text, string title)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var body = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Replace("\t", "    ");
            body.Append("<w:p>");
            if (line.Length > 0)
            {
                body.Append("<w:r><w:t xml:space=\"preserve\">")
                    .Append(SecurityElement.Escape(line))
                    .Append("</w:t></w:r>");
            }
            body.AppendLine("</w:p>");
        }

        var document = $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
{body}    <w:sectPr>
      <w:pgSz w:w="11906" w:h="16838" />
      <w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0" />
    </w:sectPr>
  </w:body>
</w:document>
""";

        const string styles = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:docDefaults>
    <w:rPrDefault><w:rPr>
      <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri" />
      <w:sz w:val="22" /><w:szCs w:val="22" />
    </w:rPr></w:rPrDefault>
    <w:pPrDefault><w:pPr>
      <w:spacing w:after="160" w:line="300" w:lineRule="auto" />
    </w:pPr></w:pPrDefault>
  </w:docDefaults>
  <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
    <w:name w:val="Normal" />
  </w:style>
</w:styles>
""";

        const string contentTypes = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="application/xml" />
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml" />
  <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
  <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml" />
</Types>
""";

        var core = $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
    xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <dc:title>{SecurityElement.Escape(title)}</dc:title>
  <dc:creator>Nasaq</dc:creator>
  <cp:lastModifiedBy>Nasaq</cp:lastModifiedBy>
  <dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>
</cp:coreProperties>
""";

        const string app = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
  <Application>Nasaq</Application>
</Properties>
""";

        const string rels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml" />
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml" />
</Relationships>
""";

        const string documentRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml" />
</Relationships>
""";

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", contentTypes);
        WriteEntry(archive, "_rels/.rels", rels);
        WriteEntry(archive, "docProps/core.xml", core);
        WriteEntry(archive, "docProps/app.xml", app);
        WriteEntry(archive, "word/document.xml", document);
        WriteEntry(archive, "word/styles.xml", styles);
        WriteEntry(archive, "word/_rels/document.xml.rels", documentRels);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
