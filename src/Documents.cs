// Conversión de documentos sin dependencias externas:
//  - Markdown, TXT y DOCX se leen a un modelo común (lista de bloques) y se escriben en MD, TXT, HTML, DOCX o PDF.
//  - El PDF se genera imprimiendo HTML con Microsoft Edge en modo headless (viene con Windows).
//  - El texto de los PDF se lee del propio archivo (PdfText) y su maquetación se convierte en títulos, listas y
//    tablas (Layout). Los PDF escaneados pasan por el OCR de Windows (Windows.Media.Ocr), con la misma maquetación.

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
            // Un navegador respeta el CSS del HTML; sin ninguno instalado, se maqueta su estructura.
            string browser = Browser.Find();
            if (browser != null) Browser.PrintToPdf(browser, input, output);
            else DocumentPdf.Write(HtmlReader.Read(File.ReadAllText(input)), output, Path.GetDirectoryName(input));
            return output;
        }

        Write(Read(input), output, target, Path.GetFileNameWithoutExtension(input), Path.GetDirectoryName(input));
        return output;
    }

    // Extrae el contenido de un PDF (con su estructura) y lo guarda como `target`.
    public static string FromPdf(string input, string target)
    {
        var blocks = PdfBlocks(input);
        string output = Paths.Beside(input, "", target);
        Write(blocks, output, target, Path.GetFileNameWithoutExtension(input), Path.GetDirectoryName(input));
        return output;
    }

    // Del texto que lleva el PDF; si no lleva (escaneado) o no se puede leer, con OCR.
    public static List<Block> PdfBlocks(string input)
    {
        List<Block> blocks = null;
        try
        {
            var pages = PdfText.Read(input);
            if (pages != null) blocks = Layout.ToBlocks(Layout.FromPdf(pages));
        }
        catch { blocks = null; }
        if (blocks == null || blocks.Count == 0) blocks = Layout.ToBlocks(Pdf.OcrPages(input));
        return blocks;
    }

    // Extrae el texto de una imagen con OCR a un .txt (con títulos, listas y tablas si los hay).
    public static string ImageToText(string input)
    {
        string output = Paths.Beside(input, "", ".txt");
        File.WriteAllText(output, TextWriterPlain.Write(Layout.ToBlocks(new List<LayoutPage> { Ocr.PageFromImageFile(input) })), new UTF8Encoding(false));
        return output;
    }

    // Bloques de cualquier documento (para imprimirlo). El HTML se lee sin CSS.
    public static List<Block> LoadBlocks(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".html" || ext == ".htm") return HtmlReader.Read(File.ReadAllText(path));
        return Read(path);
    }

    static List<Block> Read(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".md":
            case ".markdown": return MarkdownReader.Parse(ReadText(path));
            case ".txt": return PlainText.ToBlocks(ReadText(path));
            case ".docx": return DocxReader.Read(path);
            default: throw new NotSupportedException("No sé leer " + Path.GetExtension(path));
        }
    }

    // Texto en UTF-8 (con o sin BOM) o, si no lo es, en la codificación clásica de Windows (ANSI).
    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return new StreamReader(new MemoryStream(bytes), true).ReadToEnd();
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
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
                // Maquetación propia + impresora PDF de Windows: no hace falta ningún navegador.
                DocumentPdf.Write(blocks, output, baseDir);
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

// Texto plano a bloques: se reconoce la estructura que la gente escribe en un .txt sin formato.
//  - Títulos: subrayados con === o ---, con # delante, en MAYÚSCULAS, numerados ("2.1 Objetivos"), la primera
//    línea si es corta, o una línea corta sin punto final seguida de texto.
//  - Listas: -, *, •, 1., 1), a) (el nivel sale de la sangría); las líneas sangradas que siguen son del mismo punto.
//  - Tablas: columnas separadas por tabuladores, por |, por ; o , de forma regular, o alineadas con espacios.
//  - Párrafos: las líneas cortadas por el ancho se unen; citas con >, separadores (---, ***) y código sangrado.
static class PlainText
{
    static readonly Regex UnderlineRx = new Regex(@"^\s*(=+|-+)\s*$");
    static readonly Regex RuleRx = new Regex(@"^\s*([-_*=~·•])(\s*\1){2,}\s*$");
    static readonly Regex HashRx = new Regex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
    static readonly Regex BulletRx = new Regex(@"^(\s*)([-*+•·▪◦‣○●■□►▸➢➤–—])\s+(.*)$");
    static readonly Regex NumberedRx = new Regex(@"^(\s*)(\(?(?:\d{1,3}|[a-zA-Z])[.)])\s+(.*)$");
    static readonly Regex SectionRx = new Regex(@"^(\d{1,2}(\.\d{1,2}){0,3})\.?\s+\S");
    static readonly Regex TableSepRx = new Regex(@"^\s*[|+]?\s*:?[-=]{2,}:?\s*([|+]\s*:?[-=]{2,}:?\s*)*[|+]?\s*$|^\s*-{2,}(\s{2,}-{2,})+\s*$");

    public static List<Block> ToBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "\t").Split('\n').Select(l => l.TrimEnd()).ToList();
        // Ancho al que está cortado el texto (si lo está): casi todas las líneas largas llegan hasta ahí.
        var lengths = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length).OrderBy(x => x).ToList();
        int wrap = lengths.Count == 0 ? 80 : lengths[(int)(lengths.Count * 0.9)];
        // ¿Está cortado a un ancho fijo (líneas que llegan casi siempre al mismo largo), o cada línea es un párrafo?
        bool wrapped = wrap >= 40 && wrap <= 110 && lengths.Count(l => l >= wrap * 0.8) >= 3;

        var blocks = new List<Block>();
        var para = new List<string>();
        Block item = null;
        int itemIndent = 0, listIndent = 0;
        bool first = true;

        Action flush = () =>
        {
            if (para.Count == 0) return;
            blocks.Add(Paragraph(para));
            para.Clear();
        };

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string t = line.Trim();
            Match m;

            if (t.Length == 0) { flush(); item = null; continue; }
            string next = i + 1 < lines.Count ? lines[i + 1] : "";

            // Título subrayado (Setext).
            if (!UnderlineRx.IsMatch(line) && UnderlineRx.IsMatch(next) && next.Trim().Length >= Math.Min(3, t.Length) && !IsTableRow(line))
            {
                flush(); item = null;
                blocks.Add(Heading(next.Trim()[0] == '=' ? 1 : 2, t));
                i++; first = false;
                continue;
            }
            if ((m = HashRx.Match(line)).Success)
            {
                flush(); item = null;
                blocks.Add(Heading(m.Groups[1].Length, m.Groups[2].Value));
                first = false;
                continue;
            }
            if (RuleRx.IsMatch(line))
            {
                flush(); item = null;
                blocks.Add(new Block { Kind = BlockKind.Rule });
                continue;
            }

            // Tabla: dos o más líneas seguidas con las mismas columnas.
            int end;
            var table = TableAt(lines, i, out end);
            if (table != null)
            {
                flush(); item = null;
                blocks.Add(table);
                i = end - 1; first = false;
                continue;
            }

            // Cita.
            if (t.StartsWith(">"))
            {
                flush(); item = null;
                var quote = new List<string>();
                for (; i < lines.Count && lines[i].Trim().StartsWith(">"); i++) quote.Add(lines[i].Trim().Substring(1).Trim());
                i--;
                var q = Paragraph(quote);
                q.Kind = BlockKind.Quote;
                blocks.Add(q);
                continue;
            }

            // Lista.
            bool bullet = (m = BulletRx.Match(line)).Success;
            if (bullet || (m = NumberedRx.Match(line)).Success)
            {
                // "1. Introducción" sola y sin punto final, entre párrafos, es un apartado, no una lista.
                if (!bullet && LooksLikeHeading(lines, i, wrapped) && SectionRx.IsMatch(t)) { }
                else
                {
                    flush();
                    int indent = Indent(m.Groups[1].Value);
                    // El nivel cuenta desde la sangría del primer punto de la lista.
                    if (item == null) listIndent = indent;
                    item = new Block { Kind = bullet ? BlockKind.Bullet : BlockKind.Numbered, Level = Math.Max(0, Math.Min(4, (indent - listIndent + 1) / 2)) };
                    MarkdownReader.ParseInline(m.Groups[3].Value, item.Inlines);
                    itemIndent = indent + m.Groups[2].Value.Length + 1;
                    blocks.Add(item);
                    first = false;
                    continue;
                }
            }
            // Continuación de un punto de la lista: sangrada al menos como su texto, o cortada por el ancho.
            if (item != null && (Indent(line) >= itemIndent - 1 || Indent(line) > 0))
            {
                item.Inlines.Add(new Inline { Text = " " });
                MarkdownReader.ParseInline(t, item.Inlines);
                continue;
            }
            item = null;

            // Código: bloque de líneas sangradas 4 espacios (o tabulador) que no siguen a un párrafo.
            if (para.Count == 0 && (line.StartsWith("    ") || line.StartsWith("\t")))
            {
                var code = new List<string>();
                for (; i < lines.Count && (lines[i].StartsWith("    ") || lines[i].StartsWith("\t") || (lines[i].Trim().Length == 0 && i + 1 < lines.Count && (lines[i + 1].StartsWith("    ") || lines[i + 1].StartsWith("\t")))); i++)
                    code.Add(lines[i].StartsWith("\t") ? lines[i].Substring(1) : lines[i].Length >= 4 ? lines[i].Substring(4) : "");
                i--;
                blocks.Add(new Block { Kind = BlockKind.Code, CodeText = string.Join("\n", code) });
                first = false;
                continue;
            }

            // Títulos sin marcas.
            if (!wrapped) flush();
            if (para.Count == 0 && LooksLikeHeading(lines, i, wrapped))
            {
                int level;
                var sec = SectionRx.Match(t);
                if (first) level = 1;
                else if (sec.Success) level = Math.Min(4, sec.Groups[1].Value.Split('.').Length + 1);
                else level = 2;
                blocks.Add(Heading(level, t));
                first = false;
                continue;
            }
            first = false;

            // Párrafo: una línea se une a la siguiente si estaba cortada por el ancho (llega cerca del final).
            if (para.Count > 0)
            {
                string prev = para[para.Count - 1];
                if (!wrapped || prev.Length < wrap * 0.6) flush();
            }
            para.Add(t);
        }
        flush();
        return blocks;
    }

    static int Indent(string s)
    {
        int n = 0;
        foreach (char c in s) { if (c == ' ') n++; else if (c == '\t') n += 4; else break; }
        return n;
    }

    static Block Heading(int level, string text)
    {
        var b = new Block { Kind = BlockKind.Heading, Level = Math.Max(1, Math.Min(6, level)) };
        MarkdownReader.ParseInline(text.Trim().TrimEnd(':').Trim(), b.Inlines);
        return b;
    }

    static Block Paragraph(List<string> lines)
    {
        var b = new Block { Kind = BlockKind.Paragraph };
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length > 0)
            {
                // Palabra partida con guion al final de línea.
                if (sb[sb.Length - 1] == '-' && sb.Length > 1 && char.IsLetter(sb[sb.Length - 2]) && l.Length > 0 && char.IsLower(l[0])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(l.Trim());
        }
        MarkdownReader.ParseInline(sb.ToString(), b.Inlines);
        return b;
    }

    // Línea corta, sin punto final, que empieza en mayúscula o número, y tras ella hay texto (o un blanco).
    // `wrapped`: el texto está cortado a un ancho fijo; si no, cada línea es un párrafo y un título no necesita
    // una línea en blanco encima.
    static bool LooksLikeHeading(List<string> lines, int i, bool wrapped)
    {
        string t = lines[i].Trim();
        if (t.Length < 2 || t.Length > 70 || !t.Any(char.IsLetter)) return false;
        if (".,;!?".Contains(t[t.Length - 1])) return false;
        if (!(char.IsUpper(t[0]) || char.IsDigit(t[0]) || "¿¡\"'«".Contains(t[0]))) return false;
        if (t.Split(' ').Length > 10) return false;
        // "Fecha: 14 de octubre" es un dato, no un título.
        int colon = t.IndexOf(':');
        bool firstLine = !lines.Take(i).Any(l => l.Trim().Length > 0);
        if (colon > 0 && colon < t.Length - 1 && !SectionRx.IsMatch(t) && !firstLine) return false;
        string prev = i > 0 ? lines[i - 1].Trim() : "";
        string next = i + 1 < lines.Count ? lines[i + 1].Trim() : "";
        // Un título siempre tiene algo debajo (una línea suelta al final, como una firma, no lo es).
        if (!lines.Skip(i + 1).Any(l => l.Trim().Length > 0)) return false;
        bool caps = t.Any(char.IsLetter) && t.Where(char.IsLetter).All(char.IsUpper) && t.Count(char.IsLetter) >= 3;
        bool section = SectionRx.IsMatch(t);
        // Separado por líneas en blanco, o seguido de un texto más largo.
        bool standsAlone = (prev.Length == 0 || !wrapped) && (next.Length == 0 || next.Length > t.Length * 1.3 || BulletRx.IsMatch(lines[i + 1]) || NumberedRx.IsMatch(lines[i + 1]));
        if (t.EndsWith(":")) return (caps || section) && standsAlone;
        return standsAlone && (caps || section || firstLine || next.Length == 0 || next.Length > t.Length * 1.3);
    }

    // --- Tablas -------------------------------------------------------------------------------

    static bool IsTableRow(string line) { return line.Contains('\t') || line.Count(c => c == '|') >= 2; }

    static Block TableAt(List<string> lines, int start, out int end)
    {
        end = start;
        foreach (var split in new Func<string, List<string>>[] { SplitTabs, SplitPipes, SplitSemicolons, SplitCommas })
        {
            var rows = new List<List<string>>();
            int j = start;
            int cols = -1;
            for (; j < lines.Count; j++)
            {
                string l = lines[j];
                if (l.Trim().Length == 0) break;
                if (TableSepRx.IsMatch(l) && rows.Count > 0) continue;
                var cells = split(l);
                if (cells == null || cells.Count < 2) break;
                if (cols < 0) cols = cells.Count;
                // Con separadores de verdad se exige el mismo número de columnas (con comas y punto y coma, más aún).
                if (cells.Count != cols && (split == SplitCommas || split == SplitSemicolons || Math.Abs(cells.Count - cols) > 1)) break;
                rows.Add(cells);
            }
            if (rows.Count >= 2 && (split != SplitCommas || (rows.Count >= 3 && cols >= 3)))
            {
                end = j;
                return TableBlock(rows);
            }
        }

        // Columnas alineadas con dos o más espacios.
        var spaced = SpacedTable(lines, start, out end);
        return spaced;
    }

    static List<string> SplitTabs(string l) { return l.Contains('\t') ? l.Split('\t').Select(c => c.Trim()).Where((c, k) => true).ToList() : null; }

    static List<string> SplitPipes(string l)
    {
        string t = l.Trim();
        if (t.Count(c => c == '|') < 1 || (!t.StartsWith("|") && t.Count(c => c == '|') < 2 && !t.Contains(" | "))) return null;
        if (t.StartsWith("|")) t = t.Substring(1);
        if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    static List<string> SplitSemicolons(string l) { return l.Count(c => c == ';') >= 1 && !l.TrimEnd().EndsWith(";") ? l.Split(';').Select(c => c.Trim().Trim('"')).ToList() : null; }

    // CSV: comas fuera de comillas, y celdas cortas (para no confundirlo con frases con comas).
    static List<string> SplitCommas(string l)
    {
        if (!l.Contains(',')) return null;
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in l)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());
        if (cells.Any(c => c.Split(' ').Length > 6)) return null;
        return cells;
    }

    // Tabla "de máquina de escribir": columnas que empiezan en las mismas posiciones, separadas por 2+ espacios.
    static Block SpacedTable(List<string> lines, int start, out int end)
    {
        end = start;
        var rows = new List<string>();
        int j = start;
        for (; j < lines.Count; j++)
        {
            string l = lines[j];
            if (l.Trim().Length == 0) break;
            if (TableSepRx.IsMatch(l) && rows.Count > 0) { rows.Add(null); continue; }
            if (!Regex.IsMatch(l.Trim(), @"\S\s{2,}\S")) break;
            if (BulletRx.IsMatch(l) || NumberedRx.IsMatch(l)) break;
            rows.Add(l);
        }
        var real = rows.Where(r => r != null).ToList();
        if (real.Count < 2) return null;

        // Columnas: posiciones donde, en todas las filas, hay un hueco de 2+ espacios (o el final de la línea).
        int width = real.Max(r => r.Length);
        var blank = new bool[width + 2];
        for (int x = 0; x < blank.Length; x++) blank[x] = real.All(r => x >= r.Length || r[x] == ' ');
        var starts = new List<int>();
        for (int x = 0; x < width; x++)
            if (!blank[x] && (x == 0 || (blank[x - 1] && (x < 2 || blank[x - 2]))))
                starts.Add(x);
        if (starts.Count < 2) return null;

        var table = new List<List<string>>();
        foreach (var r in real)
        {
            var cells = new List<string>();
            for (int k = 0; k < starts.Count; k++)
            {
                int a = starts[k], b = k + 1 < starts.Count ? starts[k + 1] : int.MaxValue;
                cells.Add(a >= r.Length ? "" : r.Substring(a, Math.Min(r.Length, b) - a).Trim());
            }
            table.Add(cells);
        }
        // Debe parecer una tabla: la mayoría de filas con varias celdas llenas.
        if (table.Count(c => c.Count(x => x.Length > 0) >= 2) < Math.Max(2, table.Count * 0.6)) return null;
        end = j;
        return TableBlock(table);
    }

    static Block TableBlock(List<List<string>> rows)
    {
        int cols = rows.Max(r => r.Count);
        var b = new Block { Kind = BlockKind.Table, Rows = new List<List<List<Inline>>>() };
        foreach (var r in rows)
        {
            var row = new List<List<Inline>>();
            for (int c = 0; c < cols; c++)
            {
                var inl = new List<Inline>();
                MarkdownReader.ParseInline(c < r.Count ? r[c] : "", inl);
                row.Add(inl);
            }
            b.Rows.Add(row);
        }
        return b;
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
        var counters = new int[8];
        foreach (var b in blocks)
        {
            bool listItem = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;
            bool prevList = prev == BlockKind.Bullet || prev == BlockKind.Numbered;
            // Entre puntos de lista no va línea en blanco, salvo si cambia el tipo de lista (viñetas / números).
            bool sameList = listItem && prevList && (b.Level > 0 || b.Kind == prev);
            if (sb.Length > 0 && !sameList) sb.Append('\n');
            if (!listItem) Array.Clear(counters, 0, counters.Length);
            int number = 1;
            if (b.Kind == BlockKind.Numbered)
            {
                int lvl = Math.Min(7, b.Level);
                number = ++counters[lvl];
                for (int k = lvl + 1; k < counters.Length; k++) counters[k] = 0;
            }

            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append(new string('#', b.Level)).Append(' ').Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Paragraph: sb.Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Bullet: sb.Append(new string(' ', b.Level * 2)).Append("- ").Append(Inlines(b.Inlines)).Append('\n'); break;
                case BlockKind.Numbered: sb.Append(new string(' ', b.Level * 3)).Append(number).Append(". ").Append(Inlines(b.Inlines)).Append('\n'); break;
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
    // Texto plano que se lee bien y conserva la estructura: títulos subrayados, listas con viñeta y tablas en
    // columnas alineadas (al volver a leerlo como TXT se reconocen).
    public static string Write(List<Block> blocks)
    {
        var sb = new StringBuilder();
        var counters = new int[8];
        bool inList = false;
        foreach (var b in blocks)
        {
            bool listItem = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;
            if (inList && !listItem) sb.Append('\n');
            if (!listItem) Array.Clear(counters, 0, counters.Length);
            inList = listItem;
            string indent = new string(' ', b.Level * 3);
            switch (b.Kind)
            {
                case BlockKind.Heading:
                    string h = Text(b.Inlines).Trim();
                    sb.Append(h).Append('\n').Append(new string(b.Level <= 1 ? '=' : '-', Math.Max(3, Width(h)))).Append("\n\n");
                    break;
                case BlockKind.Bullet:
                    sb.Append(indent).Append("• ").Append(Text(b.Inlines)).Append('\n');
                    break;
                case BlockKind.Numbered:
                    int lvl = Math.Min(7, b.Level);
                    counters[lvl]++;
                    for (int k = lvl + 1; k < counters.Length; k++) counters[k] = 0;
                    sb.Append(indent).Append(counters[lvl]).Append(". ").Append(Text(b.Inlines)).Append('\n');
                    break;
                case BlockKind.Quote: sb.Append("> ").Append(Text(b.Inlines)).Append("\n\n"); break;
                case BlockKind.Code: sb.Append(string.Join("\n", b.CodeText.Split('\n').Select(l => "    " + l))).Append("\n\n"); break;
                case BlockKind.Rule: sb.Append("----------\n\n"); break;
                case BlockKind.Table: AppendTable(sb, b.Rows); sb.Append('\n'); break;
                default: sb.Append(Text(b.Inlines)).Append("\n\n"); break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // Columnas alineadas con espacios y una raya bajo la cabecera.
    static void AppendTable(StringBuilder sb, List<List<List<Inline>>> rows)
    {
        int cols = rows.Max(r => r.Count);
        var text = rows.Select(r => Enumerable.Range(0, cols).Select(c => c < r.Count ? Text(r[c]).Replace("\n", " ").Trim() : "").ToList()).ToList();
        var widths = Enumerable.Range(0, cols).Select(c => Math.Max(3, text.Max(r => Width(r[c])))).ToList();
        for (int i = 0; i < text.Count; i++)
        {
            sb.Append(string.Join("   ", Enumerable.Range(0, cols).Select(c => Pad(text[i][c], widths[c]))).TrimEnd()).Append('\n');
            if (i == 0) sb.Append(string.Join("   ", widths.Select(w => new string('-', w)))).Append('\n');
        }
    }

    static int Width(string s) { return s.Length; }
    static string Pad(string s, int w) { return s + new string(' ', Math.Max(0, w - Width(s))); }

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

    // Para imprimir: abrir un PDF, saber cuántas páginas y de qué tamaño tiene, y dibujar una página.
    public static PdfDocument OpenDocument(string path) { return Open(path); }
    public static int PageCount(PdfDocument doc) { return (int)doc.PageCount; }

    public static SizeF PageSize(PdfDocument doc, int index)
    {
        using (var page = doc.GetPage((uint)index)) return new SizeF((float)page.Size.Width, (float)page.Size.Height);
    }

    public static Bitmap RenderPage(PdfDocument doc, int index, double scale)
    {
        using (var stream = Render(doc, (uint)index, scale))
        using (var ms = new MemoryStream(WinRt.ReadAll(stream)))
        using (var decoded = new Bitmap(ms))
            return new Bitmap(decoded);   // copia: así no depende del MemoryStream
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

    // OCR de cada página, con la posición de cada palabra.
    public static List<LayoutPage> OcrPages(string input)
    {
        var doc = Open(input);
        var engine = Ocr.Engine();
        var pages = new List<LayoutPage>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using (var stream = Render(doc, i, RenderScale))
                pages.Add(Ocr.RecognizePage(engine, stream, (int)i));
        }
        return pages;
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

    public static LayoutPage PageFromImageFile(string path)
    {
        using (var stream = WinRt.ToStream(File.ReadAllBytes(path)))
            return RecognizePage(Engine(), stream, 0);
    }

    // Las líneas del OCR con la caja de cada palabra, para reconstruir la maquetación.
    public static LayoutPage RecognizePage(OcrEngine engine, IRandomAccessStream stream, int index)
    {
        var decoder = WinRt.Wait(BitmapDecoder.CreateAsync(stream));
        using (var raw = WinRt.Wait(decoder.GetSoftwareBitmapAsync()))
        using (var bitmap = SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
        {
            var result = WinRt.Wait(engine.RecognizeAsync(bitmap));
            var lines = result.Lines.Select(l => l.Words.Select(w => new Layout.OcrWordBox
            {
                Text = w.Text, X = w.BoundingRect.X, Y = w.BoundingRect.Y, W = w.BoundingRect.Width, H = w.BoundingRect.Height
            }).ToList()).ToList();
            return Layout.FromOcr(lines, bitmap.PixelWidth, bitmap.PixelHeight, index);
        }
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

// Imágenes -> PDF (una página por imagen, JPEG incrustado).
static class PdfWriter
{
    class PdfPage
    {
        public byte[] Jpeg;
        public int Width, Height;        // píxeles de la imagen
        public double PageW, PageH;      // tamaño de la página en puntos
    }

    // Varias imágenes elegidas por el usuario -> un PDF; cada página mide lo que la imagen a 96 ppp.
    public static string FromImages(List<string> inputs)
    {
        string first = inputs[0];
        string output = inputs.Count == 1
            ? Paths.Beside(first, "", ".pdf")
            : Paths.Unique(Path.GetDirectoryName(first), "Imágenes", ".pdf");

        var pages = new List<PdfPage>();
        foreach (var input in inputs)
        {
            using (var bmp = ImageOps.Load(input))
                pages.Add(new PdfPage { Jpeg = ImageOps.ToJpeg(bmp, 90), Width = bmp.Width, Height = bmp.Height, PageW = bmp.Width * 0.75, PageH = bmp.Height * 0.75 });
        }
        Write(pages, output);
        return output;
    }

    // Páginas ya dibujadas (respaldo de los documentos sin impresora PDF), todas del mismo tamaño en puntos.
    public static void FromBitmaps(IList<Bitmap> bitmaps, float pageWidth, float pageHeight, string output)
    {
        Write(bitmaps.Select(b => new PdfPage { Jpeg = ImageOps.ToJpeg(b, 88), Width = b.Width, Height = b.Height, PageW = pageWidth, PageH = pageHeight }).ToList(), output);
    }

    static void Write(List<PdfPage> pages, string output)
    {
        var inv = CultureInfo.InvariantCulture;
        using (var fs = File.Create(output))
        {
            var offsets = new List<long>();
            Action<string> write = s => { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); };
            Action beginObj = () => { offsets.Add(fs.Position); write(offsets.Count + " 0 obj\n"); };

            write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            int n = pages.Count;
            beginObj(); write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            beginObj();
            write("<< /Type /Pages /Count " + n + " /Kids [" + string.Join(" ", Enumerable.Range(0, n).Select(i => (3 + i * 3) + " 0 R")) + "] >>\nendobj\n");

            for (int i = 0; i < n; i++)
            {
                var p = pages[i];
                string pw = p.PageW.ToString("0.##", inv), ph = p.PageH.ToString("0.##", inv);
                string content = "q " + pw + " 0 0 " + ph + " 0 0 cm /Im0 Do Q";

                beginObj();
                write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pw + " " + ph + "] /Resources << /XObject << /Im0 " + (5 + i * 3) +
                      " 0 R >> >> /Contents " + (4 + i * 3) + " 0 R >>\nendobj\n");
                beginObj();
                write("<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                beginObj();
                write("<< /Type /XObject /Subtype /Image /Width " + p.Width + " /Height " + p.Height +
                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + p.Jpeg.Length + " >>\nstream\n");
                fs.Write(p.Jpeg, 0, p.Jpeg.Length);
                write("\nendstream\nendobj\n");
            }

            long xref = fs.Position;
            write("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
            foreach (var o in offsets) write(o.ToString("D10") + " 00000 n \n");
            write("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }
    }
}
