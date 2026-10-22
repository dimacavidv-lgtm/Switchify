// Conversión de documentos sin dependencias externas:
//  - Markdown, TXT y DOCX se leen a un modelo común (lista de bloques) y se escriben en MD, TXT, HTML, DOCX o PDF.
//  - El PDF se genera imprimiendo HTML con Microsoft Edge en modo headless (viene con Windows).
//  - Los PDF se leen con Windows.Data.Pdf y el texto se extrae con el OCR de Windows (Windows.Media.Ocr).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

// ---------------------------------------------------------------------------------------------
// Modelo
// ---------------------------------------------------------------------------------------------

enum BlockKind { Heading, Paragraph, Bullet, Numbered, Quote, Code, Rule, Table }

class Inline
{
    public string Text;
    public bool Bold, Italic, Code, Image;
    public string Link;    // URL del enlace o de la imagen

    public Inline Clone(string text)
    {
        return new Inline { Text = text, Bold = Bold, Italic = Italic, Code = Code, Image = Image, Link = Link };
    }

    public bool SameFormat(Inline o)
    {
        return Bold == o.Bold && Italic == o.Italic && Code == o.Code && !Image && !o.Image && Link == o.Link;
    }
}

class Block
{
    public BlockKind Kind;
    public int Level;                                   // nivel de título (1-6) o de sangría de lista (0..)
    public List<Inline> Inlines = new List<Inline>();
    public string CodeText;
    public List<List<List<Inline>>> Rows;               // tabla: filas -> celdas -> texto con formato

    public static Block Text(BlockKind kind, string text)
    {
        var b = new Block { Kind = kind };
        b.Inlines.Add(new Inline { Text = text });
        return b;
    }
}

static class Documents
{
    public static readonly string[] Extensions = { ".md", ".markdown", ".txt", ".docx", ".html", ".htm" };

    // Convierte un documento al formato `target` (".pdf", ".docx", ".md", ".html", ".txt").
    public static string Convert(string input, string target)
    {
        string ext = Path.GetExtension(input).ToLowerInvariant();
        string output = Paths.Beside(input, "", target);

        if (ext == ".html" || ext == ".htm")
        {
            if (target != ".pdf") throw new NotSupportedException("HTML solo se puede convertir a PDF.");
            Edge.PrintToPdf(input, output);
            return output;
        }

        Write(Read(input), output, target, Path.GetFileNameWithoutExtension(input), Path.GetDirectoryName(input));
        return output;
    }

    // Extrae el texto de un PDF con OCR y lo guarda como `target`.
    public static string FromPdf(string input, string target)
    {
        var blocks = PlainText.ToBlocks(Pdf.ExtractText(input));
        string output = Paths.Beside(input, "", target);
        Write(blocks, output, target, Path.GetFileNameWithoutExtension(input), Path.GetDirectoryName(input));
        return output;
    }

    // Extrae el texto de una imagen con OCR a un .txt.
    public static string ImageToText(string input)
    {
        string output = Paths.Beside(input, "", ".txt");
        File.WriteAllText(output, Ocr.FromImageFile(input), new UTF8Encoding(false));
        return output;
    }

    static List<Block> Read(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".md":
            case ".markdown": return MarkdownReader.Parse(File.ReadAllText(path));
            case ".txt": return PlainText.ToBlocks(File.ReadAllText(path));
            case ".docx": return DocxReader.Read(path);
            default: throw new NotSupportedException("No sé leer " + Path.GetExtension(path));
        }
    }

    static void Write(List<Block> blocks, string output, string target, string title, string baseDir)
    {
        var utf8 = new UTF8Encoding(false);
        switch (target)
        {
            case ".md": File.WriteAllText(output, MarkdownWriter.Write(blocks), utf8); break;
            case ".txt": File.WriteAllText(output, TextWriterPlain.Write(blocks), utf8); break;
            case ".html": File.WriteAllText(output, HtmlWriter.Write(blocks, title, null), utf8); break;
            case ".docx": DocxWriter.Write(blocks, output); break;
            case ".pdf":
                string tempDir = Path.Combine(Path.GetTempPath(), "Switchify");
                Directory.CreateDirectory(tempDir);
                string html = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".html");
                try
                {
                    // <base> apunta a la carpeta original para que las imágenes relativas se vean en el PDF.
                    File.WriteAllText(html, HtmlWriter.Write(blocks, title, baseDir), utf8);
                    Edge.PrintToPdf(html, output);
                }
                finally
                {
                    try { File.Delete(html); } catch { }
                }
                break;
            default: throw new NotSupportedException("Formato no soportado: " + target);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Lectores
// ---------------------------------------------------------------------------------------------

static class MarkdownReader
{
    static readonly Regex HeadingRx = new Regex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex RuleRx = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$");
    static readonly Regex BulletRx = new Regex(@"^(\s*)[-*+]\s+(.*)$");
    static readonly Regex NumberedRx = new Regex(@"^(\s*)\d+[.)]\s+(.*)$");
    static readonly Regex QuoteRx = new Regex(@"^\s{0,3}>\s?(.*)$");
    static readonly Regex TableSepRx = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");
    static readonly Regex InlineRx = new Regex(
        @"`([^`]+)`" +                                   // 1 código
        @"|!\[([^\]]*)\]\(([^)\s]+)[^)]*\)" +            // 2,3 imagen
        @"|\[([^\]]+)\]\(([^)\s]+)[^)]*\)" +             // 4,5 enlace
        @"|\*\*(.+?)\*\*|__(.+?)__" +                    // 6,7 negrita
        @"|\*(?!\s)(.+?)\*|(?<!\w)_(?!\s)(.+?)_(?!\w)"); // 8,9 cursiva

    public static List<Block> Parse(string md)
    {
        var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var para = new List<string>();
        var quote = new List<string>();

        Action flush = () =>
        {
            if (para.Count > 0)
            {
                var b = new Block { Kind = BlockKind.Paragraph };
                ParseInline(string.Join(" ", para.Select(l => l.Trim())), b.Inlines);
                blocks.Add(b);
                para.Clear();
            }
            if (quote.Count > 0)
            {
                var b = new Block { Kind = BlockKind.Quote };
                ParseInline(string.Join(" ", quote.Select(l => l.Trim())), b.Inlines);
                blocks.Add(b);
                quote.Clear();
            }
        };

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match m;

            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                flush();
                string fence = line.TrimStart().Substring(0, 3);
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence); i++) code.Add(lines[i]);
                blocks.Add(new Block { Kind = BlockKind.Code, CodeText = string.Join("\n", code) });
                continue;
            }
            if (line.Trim().Length == 0) { flush(); continue; }

            if ((m = HeadingRx.Match(line)).Success)
            {
                flush();
                var b = new Block { Kind = BlockKind.Heading, Level = m.Groups[1].Length };
                ParseInline(m.Groups[2].Value, b.Inlines);
                blocks.Add(b);
            }
            else if (RuleRx.IsMatch(line))
            {
                flush();
                blocks.Add(new Block { Kind = BlockKind.Rule });
            }
            else if ((m = BulletRx.Match(line)).Success || (m = NumberedRx.Match(line)).Success)
            {
                flush();
                var b = new Block
                {
                    Kind = BulletRx.IsMatch(line) ? BlockKind.Bullet : BlockKind.Numbered,
                    Level = Math.Min(5, m.Groups[1].Value.Replace("\t", "    ").Length / 2)
                };
                ParseInline(m.Groups[2].Value, b.Inlines);
                blocks.Add(b);
            }
            else if ((m = QuoteRx.Match(line)).Success)
            {
                if (para.Count > 0) flush();
                quote.Add(m.Groups[1].Value);
            }
            else if (line.Contains('|') && i + 1 < lines.Length && TableSepRx.IsMatch(lines[i + 1]))
            {
                flush();
                var b = new Block { Kind = BlockKind.Table, Rows = new List<List<List<Inline>>>() };
                b.Rows.Add(SplitRow(line));
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++)
                    b.Rows.Add(SplitRow(lines[i]));
                i--;
                blocks.Add(b);
            }
            else
            {
                if (quote.Count > 0) flush();
                para.Add(line);
            }
        }
        flush();
        return blocks;
    }

    static List<List<Inline>> SplitRow(string line)
    {
        string t = line.Trim();
        if (t.StartsWith("|")) t = t.Substring(1);
        if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
        return t.Split('|').Select(cell =>
        {
            var inl = new List<Inline>();
            ParseInline(cell.Trim(), inl);
            return inl;
        }).ToList();
    }

    public static void ParseInline(string text, List<Inline> output)
    {
        ParseInline(text, new Inline(), output);
    }

    static void ParseInline(string text, Inline style, List<Inline> output)
    {
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos) output.Add(style.Clone(text.Substring(pos, m.Index - pos)));
            if (m.Groups[1].Success)
            {
                var c = style.Clone(m.Groups[1].Value); c.Code = true; output.Add(c);
            }
            else if (m.Groups[3].Success)
            {
                var c = style.Clone(m.Groups[2].Value); c.Image = true; c.Link = m.Groups[3].Value; output.Add(c);
            }
            else if (m.Groups[5].Success)
            {
                var s = style.Clone(""); s.Link = m.Groups[5].Value; ParseInline(m.Groups[4].Value, s, output);
            }
            else if (m.Groups[6].Success || m.Groups[7].Success)
            {
                var s = style.Clone(""); s.Bold = true; ParseInline(m.Groups[6].Success ? m.Groups[6].Value : m.Groups[7].Value, s, output);
            }
            else
            {
                var s = style.Clone(""); s.Italic = true; ParseInline(m.Groups[8].Success ? m.Groups[8].Value : m.Groups[9].Value, s, output);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) output.Add(style.Clone(text.Substring(pos)));
    }
}

static class PlainText
{
    // Texto plano: cada línea con contenido es un párrafo.
    public static List<Block> ToBlocks(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Select(l => Block.Text(BlockKind.Paragraph, l.TrimEnd()))
            .ToList();
    }
}

static class DocxReader
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<Block> Read(string path)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            var doc = Load(zip, "word/document.xml");
            if (doc == null) throw new InvalidDataException("El archivo no es un DOCX válido.");
            var styles = Load(zip, "word/styles.xml");
            var numbering = Load(zip, "word/numbering.xml");
            var rels = Load(zip, "word/_rels/document.xml.rels");

            var links = rels == null ? new Dictionary<string, string>() : rels.Root.Elements(PR + "Relationship")
                .Where(e => ((string)e.Attribute("Type") ?? "").EndsWith("/hyperlink"))
                .ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));

            var headingByStyle = new Dictionary<string, int>();
            var quoteStyles = new HashSet<string>();
            if (styles != null)
            {
                foreach (var s in styles.Root.Elements(W + "style"))
                {
                    string id = (string)s.Attribute(W + "styleId");
                    string name = ((string)s.Elements(W + "name").Select(n => n.Attribute(W + "val")).FirstOrDefault() ?? "").ToLowerInvariant();
                    var outline = s.Descendants(W + "outlineLvl").Select(o => (string)o.Attribute(W + "val")).FirstOrDefault();
                    var hm = Regex.Match(name, @"^heading (\d)$");
                    if (id == null) continue;
                    if (hm.Success) headingByStyle[id] = int.Parse(hm.Groups[1].Value);
                    else if (name == "title") headingByStyle[id] = 1;
                    else if (name == "subtitle") headingByStyle[id] = 2;
                    else if (outline != null) headingByStyle[id] = Math.Min(6, int.Parse(outline) + 1);
                    if (name.Contains("quote")) quoteStyles.Add(id);
                }
            }

            // numId -> abstractNumId -> nivel -> formato ("bullet", "decimal", ...)
            var formats = new Dictionary<string, Dictionary<string, string>>();
            if (numbering != null)
            {
                var abstracts = numbering.Root.Elements(W + "abstractNum").ToDictionary(
                    a => (string)a.Attribute(W + "abstractNumId"),
                    a => a.Elements(W + "lvl").ToDictionary(
                        l => (string)l.Attribute(W + "ilvl"),
                        l => l.Elements(W + "numFmt").Select(f => (string)f.Attribute(W + "val")).FirstOrDefault() ?? "decimal"));
                foreach (var n in numbering.Root.Elements(W + "num"))
                {
                    string abs = n.Elements(W + "abstractNumId").Select(a => (string)a.Attribute(W + "val")).FirstOrDefault();
                    if (abs != null && abstracts.ContainsKey(abs)) formats[(string)n.Attribute(W + "numId")] = abstracts[abs];
                }
            }

            var blocks = new List<Block>();
            var body = doc.Root.Element(W + "body");
            foreach (var el in body.Elements())
            {
                if (el.Name == W + "p")
                {
                    var b = ReadParagraph(el, links, headingByStyle, quoteStyles, formats);
                    if (b != null) blocks.Add(b);
                }
                else if (el.Name == W + "tbl")
                {
                    var t = new Block { Kind = BlockKind.Table, Rows = new List<List<List<Inline>>>() };
                    foreach (var tr in el.Elements(W + "tr"))
                    {
                        t.Rows.Add(tr.Elements(W + "tc").Select(tc =>
                        {
                            var cell = new List<Inline>();
                            foreach (var p in tc.Elements(W + "p"))
                            {
                                if (cell.Count > 0) cell.Add(new Inline { Text = " " });
                                cell.AddRange(ReadRuns(p, links));
                            }
                            return cell;
                        }).ToList());
                    }
                    // La cabecera ya se distingue como tal; su negrita es solo de estilo.
                    if (t.Rows.Count > 0) foreach (var cell in t.Rows[0]) foreach (var i in cell) i.Bold = false;
                    if (t.Rows.Count > 0) blocks.Add(t);
                }
            }
            return blocks;
        }
    }

    static Block ReadParagraph(XElement p, Dictionary<string, string> links, Dictionary<string, int> headings,
                               HashSet<string> quotes, Dictionary<string, Dictionary<string, string>> formats)
    {
        var inlines = ReadRuns(p, links);
        if (inlines.All(i => string.IsNullOrWhiteSpace(i.Text) && !i.Image)) return null;

        var pPr = p.Element(W + "pPr");
        string style = pPr == null ? null : pPr.Elements(W + "pStyle").Select(s => (string)s.Attribute(W + "val")).FirstOrDefault();
        var numPr = pPr == null ? null : pPr.Element(W + "numPr");
        var b = new Block { Kind = BlockKind.Paragraph, Inlines = inlines };

        if (style != null && style.ToLowerInvariant().Contains("code"))
        {
            b.Kind = BlockKind.Code;
            b.CodeText = string.Concat(inlines.Select(i => i.Text));
        }
        else if (style != null && headings.ContainsKey(style))
        {
            b.Kind = BlockKind.Heading;
            b.Level = headings[style];
            foreach (var i in b.Inlines) i.Bold = false; // el título ya es negrita
        }
        else if (numPr != null)
        {
            string numId = numPr.Elements(W + "numId").Select(n => (string)n.Attribute(W + "val")).FirstOrDefault();
            string ilvl = numPr.Elements(W + "ilvl").Select(n => (string)n.Attribute(W + "val")).FirstOrDefault() ?? "0";
            if (numId != null && numId != "0")
            {
                string fmt = formats.ContainsKey(numId) && formats[numId].ContainsKey(ilvl) ? formats[numId][ilvl] : "bullet";
                b.Kind = fmt == "bullet" ? BlockKind.Bullet : BlockKind.Numbered;
                b.Level = Math.Min(5, int.Parse(ilvl));
            }
        }
        else if (style != null && quotes.Contains(style))
        {
            b.Kind = BlockKind.Quote;
        }
        return b;
    }

    static List<Inline> ReadRuns(XElement p, Dictionary<string, string> links)
    {
        var result = new List<Inline>();
        foreach (var r in p.Descendants(W + "r"))
        {
            // Ignora texto eliminado en control de cambios.
            if (r.Ancestors(W + "del").Any()) continue;

            var rPr = r.Element(W + "rPr");
            var hyper = r.Ancestors(W + "hyperlink").FirstOrDefault();
            string link = null;
            if (hyper != null)
            {
                string id = (string)hyper.Attribute(R + "id");
                if (id != null && links.ContainsKey(id)) link = links[id];
            }
            var style = new Inline
            {
                Bold = IsOn(rPr, "b"),
                Italic = IsOn(rPr, "i"),
                Link = link
            };

            var sb = new StringBuilder();
            foreach (var c in r.Elements())
            {
                if (c.Name == W + "t") sb.Append(c.Value);
                else if (c.Name == W + "tab") sb.Append(' ');
                else if (c.Name == W + "br" || c.Name == W + "cr") sb.Append('\n');
            }
            if (sb.Length == 0) continue;

            var inl = style.Clone(sb.ToString());
            var fonts = rPr == null ? null : rPr.Element(W + "rFonts");
            string font = fonts == null ? "" : ((string)fonts.Attribute(W + "ascii") ?? "");
            inl.Code = Regex.IsMatch(font, "Consolas|Courier|Mono", RegexOptions.IgnoreCase);
            if (result.Count > 0 && result[result.Count - 1].SameFormat(inl)) result[result.Count - 1].Text += inl.Text;
            else result.Add(inl);
        }
        return result;
    }

    static bool IsOn(XElement rPr, string name)
    {
        if (rPr == null) return false;
        var e = rPr.Element(W + name);
        if (e == null) return false;
        string v = (string)e.Attribute(W + "val");
        return v == null || (v != "0" && v != "false" && v != "none");
    }

    static XDocument Load(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using (var s = entry.Open()) return XDocument.Load(s);
    }
}

// ---------------------------------------------------------------------------------------------
// Escritores
// ---------------------------------------------------------------------------------------------

static class MarkdownWriter
{
    public static string Write(List<Block> blocks)
    {
        var sb = new StringBuilder();
        BlockKind? prev = null;
        foreach (var b in blocks)
        {
            bool listItem = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;
            bool prevList = prev == BlockKind.Bullet || prev == BlockKind.Numbered;
            if (sb.Length > 0 && !(listItem && prevList)) sb.Append('\n');

            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append(new string('#', b.Level)).Append(' ').Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Paragraph: sb.Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Bullet: sb.Append(new string(' ', b.Level * 2)).Append("- ").Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Numbered: sb.Append(new string(' ', b.Level * 3)).Append("1. ").Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Quote: sb.Append("> ").Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Code: sb.Append("```\n").Append(b.CodeText).Append("\n```\n"); break;
                case BlockKind.Rule: sb.Append("---\n"); break;
                case BlockKind.Table:
                    int cols = b.Rows.Max(r => r.Count);
                    for (int i = 0; i < b.Rows.Count; i++)
                    {
                        var cells = Enumerable.Range(0, cols).Select(c => c < b.Rows[i].Count ? Inlines(b.Rows[i][c]).Replace("|", "\\|") : "");
                        sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                        if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
                    }
                    break;
            }
            prev = b.Kind;
        }
        return sb.ToString();
    }

    static string Inlines(List<Inline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var i in inlines)
        {
            if (i.Image) { sb.Append("![").Append(i.Text).Append("](").Append(i.Link).Append(')'); continue; }
            string t = i.Text;
            if (t.Trim().Length == 0) { sb.Append(t); continue; }
            // Los marcadores van pegados al texto: "**hola** mundo", no "** hola **".
            string lead = t.Substring(0, t.Length - t.TrimStart().Length);
            string trail = t.Substring(t.TrimEnd().Length);
            t = t.Trim();
            if (i.Code) t = "`" + t + "`";
            if (i.Italic) t = "*" + t + "*";
            if (i.Bold) t = "**" + t + "**";
            if (i.Link != null) t = "[" + t + "](" + i.Link + ")";
            sb.Append(lead).Append(t).Append(trail);
        }
        return sb.ToString();
    }
}

static class TextWriterPlain
{
    public static string Write(List<Block> blocks)
    {
        var sb = new StringBuilder();
        int counter = 0;
        bool inList = false;
        foreach (var b in blocks)
        {
            counter = b.Kind == BlockKind.Numbered ? counter + 1 : 0;
            bool listItem = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;
            if (inList && !listItem) sb.Append('\n');
            inList = listItem;
            string indent = new string(' ', b.Level * 2);
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append(Text(b.Inlines).ToUpper(CultureInfo.CurrentCulture)).Append("\n\n"); break;
                case BlockKind.Bullet: sb.Append(indent).Append("• ").Append(Text(b.Inlines)).Append('\n'); break;
                case BlockKind.Numbered: sb.Append(indent).Append(counter).Append(". ").Append(Text(b.Inlines)).Append('\n'); break;
                case BlockKind.Code: sb.Append(b.CodeText).Append("\n\n"); break;
                case BlockKind.Rule: sb.Append("----------\n\n"); break;
                case BlockKind.Table:
                    foreach (var row in b.Rows) sb.Append(string.Join("\t", row.Select(Text))).Append('\n');
                    sb.Append('\n');
                    break;
                default: sb.Append(Text(b.Inlines)).Append("\n\n"); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    public static string Text(List<Inline> inlines)
    {
        return string.Concat(inlines.Select(i => i.Text));
    }
}

static class HtmlWriter
{
    const string Css =
        "@page{margin:2cm}" +
        "body{font-family:'Segoe UI',Calibri,Arial,sans-serif;font-size:11pt;line-height:1.55;color:#222;max-width:800px;margin:2em auto;padding:0 1em}" +
        "h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.3em 0 .5em;color:#1f2a44}h1{font-size:2em;border-bottom:1px solid #ddd;padding-bottom:.25em}" +
        "h2{font-size:1.5em}h3{font-size:1.25em}code{font-family:Consolas,monospace;background:#f3f3f3;padding:.1em .3em;border-radius:3px}" +
        "pre{background:#f6f8fa;padding:12px 14px;border-radius:6px;overflow:auto;white-space:pre-wrap}pre code{background:none;padding:0}" +
        "blockquote{margin:1em 0;padding:.2em 1em;border-left:4px solid #e8431c;color:#555;background:#fff7f2}" +
        "table{border-collapse:collapse;margin:1em 0}th,td{border:1px solid #ccc;padding:6px 10px;text-align:left}th{background:#f3f3f3}" +
        "img{max-width:100%}hr{border:0;border-top:1px solid #ddd;margin:1.5em 0}a{color:#0563c1}";

    public static string Write(List<Block> blocks, string title, string baseDir)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>").Append(Esc(title)).Append("</title>");
        if (baseDir != null) sb.Append("<base href=\"").Append(Esc(new Uri(baseDir.TrimEnd('\\') + "\\").AbsoluteUri)).Append("\">");
        sb.Append("<style>").Append(Css).Append("</style></head><body>\n");

        var open = new List<string>(); // listas abiertas por nivel
        Action<int> closeTo = n => { while (open.Count > n) { sb.Append("</").Append(open[open.Count - 1]).Append(">\n"); open.RemoveAt(open.Count - 1); } };

        foreach (var b in blocks)
        {
            if (b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered)
            {
                string tag = b.Kind == BlockKind.Bullet ? "ul" : "ol";
                closeTo(b.Level + 1);
                if (open.Count == b.Level + 1 && open[b.Level] != tag) closeTo(b.Level);
                while (open.Count < b.Level + 1) { sb.Append('<').Append(tag).Append(">\n"); open.Add(tag); }
                sb.Append("<li>").Append(Inlines(b.Inlines)).Append("</li>\n");
                continue;
            }
            closeTo(0);
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.AppendFormat("<h{0}>{1}</h{0}>\n", b.Level, Inlines(b.Inlines)); break;
                case BlockKind.Paragraph: sb.Append("<p>").Append(Inlines(b.Inlines)).Append("</p>\n"); break;
                case BlockKind.Quote: sb.Append("<blockquote><p>").Append(Inlines(b.Inlines)).Append("</p></blockquote>\n"); break;
                case BlockKind.Code: sb.Append("<pre><code>").Append(Esc(b.CodeText)).Append("</code></pre>\n"); break;
                case BlockKind.Rule: sb.Append("<hr>\n"); break;
                case BlockKind.Table:
                    sb.Append("<table>\n");
                    for (int i = 0; i < b.Rows.Count; i++)
                    {
                        string cell = i == 0 ? "th" : "td";
                        sb.Append("<tr>");
                        foreach (var c in b.Rows[i]) sb.Append('<').Append(cell).Append('>').Append(Inlines(c)).Append("</").Append(cell).Append('>');
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</table>\n");
                    break;
            }
        }
        closeTo(0);
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    static string Inlines(List<Inline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var i in inlines)
        {
            if (i.Image) { sb.Append("<img src=\"").Append(Esc(i.Link)).Append("\" alt=\"").Append(Esc(i.Text)).Append("\">"); continue; }
            string t = Esc(i.Text);
            if (i.Code) t = "<code>" + t + "</code>";
            if (i.Italic) t = "<em>" + t + "</em>";
            if (i.Bold) t = "<strong>" + t + "</strong>";
            if (i.Link != null) t = "<a href=\"" + Esc(i.Link) + "\">" + t + "</a>";
            sb.Append(t);
        }
        return sb.ToString();
    }

    static string Esc(string s)
    {
        return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}

static class DocxWriter
{
    const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void Write(List<Block> blocks, string output)
    {
        var links = new List<string>();
        int numberedLists = 0;
        var body = new StringBuilder();
        BlockKind? prev = null;

        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    Paragraph(body, "Heading" + b.Level, null, b.Inlines, links);
                    break;
                case BlockKind.Paragraph:
                    Paragraph(body, null, null, b.Inlines, links);
                    break;
                case BlockKind.Quote:
                    Paragraph(body, "Quote", null, b.Inlines, links);
                    break;
                case BlockKind.Bullet:
                    Paragraph(body, "ListParagraph", "<w:numPr><w:ilvl w:val=\"" + b.Level + "\"/><w:numId w:val=\"1\"/></w:numPr>", b.Inlines, links);
                    break;
                case BlockKind.Numbered:
                    // Cada lista numerada nueva vuelve a empezar en 1.
                    if ((prev != BlockKind.Numbered && prev != BlockKind.Bullet) || numberedLists == 0) numberedLists++;
                    Paragraph(body, "ListParagraph", "<w:numPr><w:ilvl w:val=\"" + b.Level + "\"/><w:numId w:val=\"" + (numberedLists + 1) + "\"/></w:numPr>", b.Inlines, links);
                    break;
                case BlockKind.Code:
                    body.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr>");
                    var codeLines = (b.CodeText ?? "").Split('\n');
                    for (int i = 0; i < codeLines.Length; i++)
                    {
                        if (i > 0) body.Append("<w:r><w:br/></w:r>");
                        body.Append("<w:r><w:t xml:space=\"preserve\">").Append(X(codeLines[i])).Append("</w:t></w:r>");
                    }
                    body.Append("</w:p>");
                    break;
                case BlockKind.Rule:
                    body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr></w:pPr></w:p>");
                    break;
                case BlockKind.Table:
                    Table(body, b, links);
                    break;
            }
            prev = b.Kind;
        }

        string document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"" + WNs + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" +
            body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1418\" w:right=\"1418\" w:bottom=\"1418\" w:left=\"1418\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>";

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append("<Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        for (int i = 0; i < links.Count; i++)
            rels.Append("<Relationship Id=\"rIdLink").Append(i).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"")
                .Append(X(links[i])).Append("\" TargetMode=\"External\"/>");
        rels.Append("</Relationships>");

        if (File.Exists(output)) File.Delete(output);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");
            Add(zip, "word/_rels/document.xml.rels", rels.ToString());
            Add(zip, "word/document.xml", document);
            Add(zip, "word/styles.xml", Styles());
            Add(zip, "word/numbering.xml", Numbering(numberedLists));
        }
    }

    static void Paragraph(StringBuilder sb, string style, string numPr, List<Inline> inlines, List<string> links)
    {
        sb.Append("<w:p>");
        if (style != null || numPr != null)
        {
            sb.Append("<w:pPr>");
            if (style != null) sb.Append("<w:pStyle w:val=\"").Append(style).Append("\"/>");
            if (numPr != null) sb.Append(numPr);
            sb.Append("</w:pPr>");
        }
        Runs(sb, inlines, links);
        sb.Append("</w:p>");
    }

    static void Runs(StringBuilder sb, List<Inline> inlines, List<string> links)
    {
        foreach (var i in inlines)
        {
            string text = i.Image ? "[imagen: " + i.Text + "]" : i.Text;
            var run = new StringBuilder("<w:r>");
            var props = new StringBuilder();
            if (i.Link != null && !i.Image) props.Append("<w:rStyle w:val=\"Hyperlink\"/>");
            if (i.Code) props.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>");
            if (i.Bold) props.Append("<w:b/>");
            if (i.Italic) props.Append("<w:i/>");
            if (props.Length > 0) run.Append("<w:rPr>").Append(props).Append("</w:rPr>");
            run.Append("<w:t xml:space=\"preserve\">").Append(X(text)).Append("</w:t></w:r>");

            if (i.Link != null && !i.Image)
            {
                int idx = links.IndexOf(i.Link);
                if (idx < 0) { links.Add(i.Link); idx = links.Count - 1; }
                sb.Append("<w:hyperlink r:id=\"rIdLink").Append(idx).Append("\">").Append(run).Append("</w:hyperlink>");
            }
            else sb.Append(run);
        }
    }

    static void Table(StringBuilder sb, Block b, List<string> links)
    {
        int cols = Math.Max(1, b.Rows.Max(r => r.Count));
        int width = 9000 / cols;
        sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid>");
        for (int c = 0; c < cols; c++) sb.Append("<w:gridCol w:w=\"").Append(width).Append("\"/>");
        sb.Append("</w:tblGrid>");
        for (int r = 0; r < b.Rows.Count; r++)
        {
            sb.Append("<w:tr>");
            for (int c = 0; c < cols; c++)
            {
                var cell = c < b.Rows[r].Count ? b.Rows[r][c] : new List<Inline>();
                if (r == 0) cell = cell.Select(i => { var x = i.Clone(i.Text); x.Bold = true; return x; }).ToList();
                sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(width).Append("\" w:type=\"dxa\"/>");
                if (r == 0) sb.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>");
                sb.Append("</w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>");
                Runs(sb, cell, links);
                sb.Append("</w:p></w:tc>");
            }
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl><w:p/>");
    }

    static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"" + WNs + "\">");
        sb.Append("<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"es-ES\"/></w:rPr></w:rPrDefault>");
        sb.Append("<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>");
        sb.Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>");
        int[] sizes = { 36, 30, 26, 24, 22, 22 };
        for (int i = 1; i <= 6; i++)
        {
            sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"Heading").Append(i).Append("\"><w:name w:val=\"heading ").Append(i)
              .Append("\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"")
              .Append(i <= 2 ? 360 : 240).Append("\" w:after=\"120\"/><w:outlineLvl w:val=\"").Append(i - 1)
              .Append("\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"1F2A44\"/><w:sz w:val=\"").Append(sizes[i - 1]).Append("\"/></w:rPr></w:style>");
        }
        sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"E8431C\"/></w:pBdr><w:ind w:left=\"567\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"555555\"/></w:rPr></w:style>");
        sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F6F8FA\"/><w:spacing w:after=\"160\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>");
        sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"60\"/><w:contextualSpacing/></w:pPr></w:style>");
        sb.Append("<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>");
        sb.Append("<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblBorders>");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>");
        sb.Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"108\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>");
        sb.Append("</w:styles>");
        return sb.ToString();
    }

    static string Numbering(int numberedLists)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"" + WNs + "\">");
        string[] bullets = { "•", "◦", "▪" };
        sb.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (int l = 0; l < 6; l++)
            sb.Append("<w:lvl w:ilvl=\"").Append(l).Append("\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"")
              .Append(bullets[l % 3]).Append("\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"").Append(720 + l * 360)
              .Append("\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        sb.Append("</w:abstractNum>");
        string[] fmts = { "decimal", "lowerLetter", "lowerRoman" };
        sb.Append("<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (int l = 0; l < 6; l++)
            sb.Append("<w:lvl w:ilvl=\"").Append(l).Append("\"><w:start w:val=\"1\"/><w:numFmt w:val=\"").Append(fmts[l % 3])
              .Append("\"/><w:lvlText w:val=\"%").Append(l + 1).Append(".\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"")
              .Append(720 + l * 360).Append("\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        sb.Append("</w:abstractNum>");
        sb.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        for (int i = 1; i <= numberedLists; i++)
            sb.Append("<w:num w:numId=\"").Append(i + 1).Append("\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
        sb.Append("</w:numbering>");
        return sb.ToString();
    }

    static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(content);
    }

    // Escapa texto para XML y quita caracteres de control que Word no acepta.
    static string X(string s)
    {
        var clean = new string((s ?? "").Where(c => c == '\t' || c == '\n' || c == '\r' || c >= 0x20).ToArray());
        return SecurityElement.Escape(clean);
    }
}

// ---------------------------------------------------------------------------------------------
// PDF, OCR y Edge
// ---------------------------------------------------------------------------------------------

static class Pdf
{
    const double RenderScale = 2.0; // ~192 ppp

    static PdfDocument Open(string path)
    {
        // Se carga desde memoria: abrirlo con StorageFile dejaría el archivo bloqueado mientras dure la conversión.
        return WinRt.Wait(PdfDocument.LoadFromStreamAsync(WinRt.ToStream(File.ReadAllBytes(path))));
    }

    static InMemoryRandomAccessStream Render(PdfDocument doc, uint index, double scale)
    {
        using (var page = doc.GetPage(index))
        {
            var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Min(OcrEngine.MaxImageDimension, page.Size.Width * scale),
                DestinationHeight = (uint)Math.Min(OcrEngine.MaxImageDimension, page.Size.Height * scale)
            };
            WinRt.Wait(page.RenderToStreamAsync(stream, options));
            stream.Seek(0);
            return stream;
        }
    }

    // Guarda cada página como imagen. Devuelve la ruta de la primera.
    public static string ToImages(string input, string ext)
    {
        var doc = Open(input);
        string first = null;
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using (var stream = Render(doc, i, RenderScale))
            using (var ms = new MemoryStream())
            {
                var bytes = WinRt.ReadAll(stream);
                ms.Write(bytes, 0, bytes.Length);
                ms.Position = 0;
                using (var bmp = new Bitmap(ms))
                {
                    string suffix = doc.PageCount == 1 ? "" : " - página " + (i + 1);
                    string output = Paths.Beside(input, suffix, ext);
                    ImageOps.SaveAs(bmp, output, ext);
                    if (first == null) first = output;
                }
            }
        }
        return first;
    }

    public static string ExtractText(string input)
    {
        var doc = Open(input);
        var engine = Ocr.Engine();
        var sb = new StringBuilder();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using (var stream = Render(doc, i, RenderScale))
                sb.Append(Ocr.Recognize(engine, stream)).Append('\n');
        }
        return sb.ToString();
    }
}

// Espera síncrona de operaciones WinRT (sin depender de System.Runtime.WindowsRuntime / AsTask).
static class WinRt
{
    public static T Wait<T>(IAsyncOperation<T> op)
    {
        Block(op);
        return op.GetResults();
    }

    public static void Wait(IAsyncAction op)
    {
        Block(op);
        op.GetResults();
    }

    public static void Wait<TProgress>(IAsyncActionWithProgress<TProgress> op)
    {
        Block(op);
        op.GetResults();
    }

    static void Block(IAsyncInfo op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(5);
        if (op.Status == AsyncStatus.Error) throw op.ErrorCode;
        if (op.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
    }

    public static InMemoryRandomAccessStream ToStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            Wait(writer.StoreAsync());
            Wait(writer.FlushAsync());
            writer.DetachStream();
        }
        stream.Seek(0);
        return stream;
    }

    public static byte[] ReadAll(IRandomAccessStream stream)
    {
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            uint size = Wait(reader.LoadAsync((uint)stream.Size));
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
    }
}

static class Ocr
{
    public static OcrEngine Engine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) throw new InvalidOperationException("Windows no tiene ningún idioma con OCR instalado (Configuración > Hora e idioma > Idioma).");
        return engine;
    }

    public static string FromImageFile(string path)
    {
        using (var stream = WinRt.ToStream(File.ReadAllBytes(path)))
            return Recognize(Engine(), stream);
    }

    // Reconoce el texto y reconstruye párrafos: una línea que termina en punto o es corta cierra el párrafo.
    public static string Recognize(OcrEngine engine, IRandomAccessStream stream)
    {
        var decoder = WinRt.Wait(BitmapDecoder.CreateAsync(stream));
        using (var raw = WinRt.Wait(decoder.GetSoftwareBitmapAsync()))
        using (var bitmap = SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
        {
            var result = WinRt.Wait(engine.RecognizeAsync(bitmap));
            var lines = result.Lines.Select(l => l.Text.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return "";
            int longest = lines.Max(l => l.Length);

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                sb.Append(line);
                bool ends = line.EndsWith(".") || line.EndsWith(":") || line.EndsWith("?") || line.EndsWith("!") || line.Length < longest * 0.6;
                sb.Append(ends ? "\n" : " ");
            }
            return sb.ToString().TrimEnd() + "\n";
        }
    }
}

static class Edge
{
    static readonly object Gate = new object();

    static string FindExe()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return candidates
            .Select(dir => Path.Combine(dir, @"Microsoft\Edge\Application\msedge.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static void PrintToPdf(string htmlPath, string pdfPath)
    {
        string exe = FindExe();
        if (exe == null) throw new FileNotFoundException("Hace falta Microsoft Edge para crear PDF.");

        // Perfil propio para no interferir con el Edge que tenga abierto el usuario.
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchify", "edge");
        string args = string.Format("--headless --disable-gpu --no-first-run --no-default-browser-check --no-pdf-header-footer " +
                                    "--user-data-dir=\"{0}\" --print-to-pdf=\"{1}\" \"{2}\"",
                                    profile, pdfPath, new Uri(htmlPath).AbsoluteUri);
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        // De uno en uno: dos Edge con el mismo perfil a la vez hacen que el segundo termine sin generar nada.
        lock (Gate)
        {
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("Edge tardó demasiado en generar el PDF.");
                }
            }
        }
        if (!File.Exists(pdfPath)) throw new IOException("Edge no generó el PDF.");
    }
}

// Varias imágenes -> un PDF (una página por imagen, JPEG incrustado).
static class PdfWriter
{
    public static string FromImages(List<string> inputs)
    {
        string first = inputs[0];
        string output = inputs.Count == 1
            ? Paths.Beside(first, "", ".pdf")
            : Paths.Unique(Path.GetDirectoryName(first), "Imágenes", ".pdf");

        var inv = CultureInfo.InvariantCulture;
        using (var fs = File.Create(output))
        {
            var offsets = new List<long>();
            Action<string> write = s => { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); };
            Action beginObj = () => { offsets.Add(fs.Position); write(offsets.Count + " 0 obj\n"); };

            write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            int n = inputs.Count;
            beginObj(); write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            beginObj();
            write("<< /Type /Pages /Count " + n + " /Kids [" + string.Join(" ", Enumerable.Range(0, n).Select(i => (3 + i * 3) + " 0 R")) + "] >>\nendobj\n");

            for (int i = 0; i < n; i++)
            {
                byte[] jpeg;
                int w, h;
                using (var bmp = ImageOps.Load(inputs[i]))
                {
                    w = bmp.Width; h = bmp.Height;
                    jpeg = ImageOps.ToJpeg(bmp, 90);
                }
                // 96 ppp -> puntos PDF (72 por pulgada).
                string pw = (w * 0.75).ToString("0.##", inv), ph = (h * 0.75).ToString("0.##", inv);
                string content = "q " + pw + " 0 0 " + ph + " 0 0 cm /Im0 Do Q";

                beginObj();
                write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pw + " " + ph + "] /Resources << /XObject << /Im0 " + (5 + i * 3) +
                      " 0 R >> >> /Contents " + (4 + i * 3) + " 0 R >>\nendobj\n");
                beginObj();
                write("<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                beginObj();
                write("<< /Type /XObject /Subtype /Image /Width " + w + " /Height " + h +
                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
                fs.Write(jpeg, 0, jpeg.Length);
                write("\nendstream\nendobj\n");
            }

            long xref = fs.Position;
            write("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
            foreach (var o in offsets) write(o.ToString("D10") + " 00000 n \n");
            write("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }
        return output;
    }
}
