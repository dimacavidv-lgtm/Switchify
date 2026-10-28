// PDF de documentos sin navegador.
//  - DocumentLayout coloca los bloques (títulos, párrafos, listas, citas, código, tablas, imágenes) en páginas A4,
//    con el mismo estilo que la hoja CSS de HtmlWriter. Cada página es una lista de operaciones de dibujo.
//  - DocumentPdf las imprime en la impresora «Microsoft Print to PDF» de Windows (texto real, seleccionable).
//    Si esa impresora no existe, dibuja cada página como imagen y las une en un PDF.
//  - Browser imprime HTML con Edge, Chrome o Brave si hay alguno instalado; HtmlReader convierte HTML a bloques
//    para cuando no hay ninguno.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

// ---------------------------------------------------------------------------------------------
// Maquetación
// ---------------------------------------------------------------------------------------------

class DocumentLayout : IDisposable
{
    // A4 en puntos (1/72 de pulgada) y márgenes de 2 cm, como el @page de la hoja CSS.
    public const float PageWidth = 595.28f, PageHeight = 841.89f;
    const float Margin = 56.7f;
    const float Left = Margin, Right = PageWidth - Margin, Top = Margin, Bottom = PageHeight - Margin;
    const float Width = Right - Left;

    static readonly Color TextColor = Color.FromArgb(34, 34, 34);
    static readonly Color HeadingColor = Color.FromArgb(31, 42, 68);
    static readonly Color LinkColor = Color.FromArgb(5, 99, 193);
    static readonly Color Muted = Color.FromArgb(85, 85, 85);
    static readonly Color CodeBack = Color.FromArgb(243, 243, 243);
    static readonly Color CodeBlockBack = Color.FromArgb(246, 248, 250);
    static readonly Color QuoteBar = Color.FromArgb(232, 67, 28);
    static readonly Color QuoteBack = Color.FromArgb(255, 247, 242);
    static readonly Color Border = Color.FromArgb(204, 204, 204);
    static readonly Color HeaderBack = Color.FromArgb(243, 243, 243);
    static readonly Color RuleColor = Color.FromArgb(221, 221, 221);

    const string BodyFont = "Segoe UI", CodeFont = "Consolas";
    const float BodySize = 11f, CodeSize = 9.5f, LineFactor = 1.45f;

    public readonly List<List<Action<Graphics>>> Pages = new List<List<Action<Graphics>>>();
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    readonly List<Image> images = new List<Image>();
    readonly Graphics measure;
    readonly Bitmap measureBitmap;
    readonly StringFormat typo;
    readonly string baseDir;
    List<Action<Graphics>> page;
    float y;

    public DocumentLayout(List<Block> blocks, string baseDir)
    {
        this.baseDir = baseDir;
        measureBitmap = new Bitmap(1, 1);
        measure = Graphics.FromImage(measureBitmap);
        measure.PageUnit = GraphicsUnit.Point;
        measure.TextRenderingHint = TextRenderingHint.AntiAlias;
        typo = (StringFormat)StringFormat.GenericTypographic.Clone();
        typo.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
        NewPage();
        Lay(blocks);
    }

    // --- Recursos ------------------------------------------------------------------------------

    Font F(string family, float size, FontStyle style)
    {
        string key = family + "|" + size + "|" + (int)style;
        Font f;
        if (!fonts.TryGetValue(key, out f))
        {
            f = new Font(family, size, style, GraphicsUnit.Point);
            fonts[key] = f;
        }
        return f;
    }

    float TextWidth(string s, Font f)
    {
        if (s.Length == 0) return 0;
        return measure.MeasureString(s, f, PointF.Empty, typo).Width;
    }

    // Altura de línea y distancia de la parte alta de la línea a la línea base, en puntos.
    static float Ascent(Font f)
    {
        var fam = f.FontFamily;
        return f.Size * fam.GetCellAscent(f.Style) / fam.GetEmHeight(f.Style);
    }

    static float CellHeight(Font f)
    {
        var fam = f.FontFamily;
        return f.Size * (fam.GetCellAscent(f.Style) + fam.GetCellDescent(f.Style)) / fam.GetEmHeight(f.Style);
    }

    void NewPage()
    {
        page = new List<Action<Graphics>>();
        Pages.Add(page);
        y = Top;
    }

    // Si no cabe `h` en lo que queda de página, salta a la siguiente.
    void Ensure(float h)
    {
        if (y + h > Bottom && y > Top) NewPage();
    }

    void Draw(Action<Graphics> op) { page.Add(op); }

    // --- Texto con formato ---------------------------------------------------------------------

    class Word
    {
        public string Text;
        public Font Font;
        public Color Color;
        public bool Code, Space;
        public float Width;
    }

    class Line
    {
        public List<Word> Words = new List<Word>();
        public float Height, Ascent;
    }

    // Parte el texto con formato en líneas que caben en `width`.
    List<Line> Wrap(List<Inline> inlines, float width, float size, FontStyle baseStyle, Color baseColor, string family)
    {
        var lines = new List<Line>();
        var line = new Line();
        float x = 0;
        Action newLine = () =>
        {
            // Sin espacios al final de la línea. Ojo: un trozo puede ser varias palabras unidas que acaban en
            // espacio; solo se le quita el espacio (antes se borraba el trozo entero y desaparecía texto).
            while (line.Words.Count > 0 && line.Words[line.Words.Count - 1].Space)
            {
                var last = line.Words[line.Words.Count - 1];
                string trimmed = last.Text.TrimEnd();
                if (trimmed.Length == 0) { line.Words.RemoveAt(line.Words.Count - 1); continue; }
                last.Text = trimmed;
                last.Width = TextWidth(trimmed, last.Font);
                last.Space = false;
            }
            lines.Add(line);
            line = new Line();
            x = 0;
        };

        foreach (var inl in inlines)
        {
            string text = inl.Image ? "[imagen" + (string.IsNullOrEmpty(inl.Text) ? "" : ": " + inl.Text) + "]" : inl.Text ?? "";
            var style = baseStyle;
            if (inl.Bold) style |= FontStyle.Bold;
            if (inl.Italic) style |= FontStyle.Italic;
            if (inl.Link != null && !inl.Image) style |= FontStyle.Underline;
            var font = inl.Code ? F(CodeFont, size * 0.9f, style & ~FontStyle.Underline) : F(family, size, style);
            var color = inl.Link != null && !inl.Image ? LinkColor : baseColor;

            foreach (Match m in Regex.Matches(text, @"\n|[^\S\n]+|[^\s]+"))
            {
                string token = m.Value;
                if (token == "\n") { newLine(); continue; }
                bool space = char.IsWhiteSpace(token[0]);
                if (space && x == 0) continue;              // sin espacios al principio de línea
                if (space) token = " ";
                float w = TextWidth(token, font);

                if (!space && x + w > width && x > 0) newLine();
                // Palabra más larga que la línea entera (una URL, por ejemplo): se corta por caracteres.
                while (!space && w > width && token.Length > 1)
                {
                    int n = token.Length - 1;
                    while (n > 1 && TextWidth(token.Substring(0, n), font) > width - x) n--;
                    AddWord(line, token.Substring(0, n), font, color, inl.Code, false, TextWidth(token.Substring(0, n), font));
                    newLine();
                    token = token.Substring(n);
                    w = TextWidth(token, font);
                }
                AddWord(line, token, font, color, inl.Code, space, w);
                x += w;
            }
        }
        if (line.Words.Count > 0 || lines.Count == 0) newLine();

        foreach (var l in lines)
        {
            var bodyFont = F(family, size, baseStyle);
            float asc = Ascent(bodyFont), h = CellHeight(bodyFont);
            foreach (var w in l.Words) { asc = Math.Max(asc, Ascent(w.Font)); h = Math.Max(h, CellHeight(w.Font)); }
            l.Ascent = asc + (h * (LineFactor - 1)) / 2;
            l.Height = h * LineFactor;
        }
        return lines;
    }

    static void AddWord(Line line, string text, Font font, Color color, bool code, bool space, float width)
    {
        // Palabras seguidas con el mismo estilo se unen: menos operaciones y el texto se copia mejor del PDF.
        var last = line.Words.Count > 0 ? line.Words[line.Words.Count - 1] : null;
        if (last != null && last.Font == font && last.Color == color && last.Code == code && !code)
        {
            last.Text += text;
            last.Width += width;
            last.Space = space;
            return;
        }
        line.Words.Add(new Word { Text = text, Font = font, Color = color, Code = code, Space = space, Width = width });
    }

    // Dibuja una línea con su base en `top + line.Ascent`.
    void DrawLine(Line line, float x, float top)
    {
        float baseline = top + line.Ascent;
        foreach (var w in line.Words)
        {
            var word = w;
            float wx = x, wy = baseline - Ascent(w.Font);
            if (w.Code && !w.Space)
            {
                float ch = CellHeight(w.Font);
                Draw(g => { using (var b = new SolidBrush(CodeBack)) g.FillRectangle(b, wx - 1.5f, wy - 0.5f, word.Width + 3, ch + 1); });
            }
            Draw(g => { using (var b = new SolidBrush(word.Color)) g.DrawString(word.Text, word.Font, b, wx, wy, typo); });
            x += w.Width;
        }
    }

    // Párrafo genérico: líneas a partir de `x`, con salto de página entre líneas.
    void Paragraph(List<Inline> inlines, float x, float width, float size, FontStyle style, Color color, string family, Action<float, float> background)
    {
        foreach (var line in Wrap(inlines, width, size, style, color, family))
        {
            Ensure(line.Height);
            if (background != null) background(y, line.Height);
            DrawLine(line, x, y);
            y += line.Height;
        }
    }

    // --- Bloques -------------------------------------------------------------------------------

    void Lay(List<Block> blocks)
    {
        var counters = new int[8];
        BlockKind? prev = null;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            bool listItem = b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;
            if (!listItem) Array.Clear(counters, 0, counters.Length);
            if (listItem && prev != BlockKind.Bullet && prev != BlockKind.Numbered) y += 2;

            switch (b.Kind)
            {
                case BlockKind.Heading: Heading(b, i == 0); break;
                case BlockKind.Paragraph:
                    if (b.Inlines.Count > 0 && b.Inlines.All(n => n.Image || string.IsNullOrWhiteSpace(n.Text)) && b.Inlines.Any(n => n.Image))
                        foreach (var img in b.Inlines.Where(n => n.Image)) Picture(img);
                    else
                    {
                        Paragraph(b.Inlines, Left, Width, BodySize, FontStyle.Regular, TextColor, BodyFont, null);
                        y += 8;
                    }
                    break;
                case BlockKind.Bullet:
                case BlockKind.Numbered:
                    ListItem(b, counters);
                    bool nextIsList = i + 1 < blocks.Count && (blocks[i + 1].Kind == BlockKind.Bullet || blocks[i + 1].Kind == BlockKind.Numbered);
                    y += nextIsList ? 1 : 9;
                    break;
                case BlockKind.Quote: Quote(b); break;
                case BlockKind.Code: CodeBlock(b); break;
                case BlockKind.Rule:
                    Ensure(20);
                    y += 9;
                    float ry = y;
                    Draw(g => { using (var p = new Pen(RuleColor, 0.75f)) g.DrawLine(p, Left, ry, Right, ry); });
                    y += 12;
                    break;
                case BlockKind.Table: Table(b); break;
            }
            prev = b.Kind;
        }
    }

    void Heading(Block b, bool first)
    {
        float[] sizes = { 22f, 16.5f, 13.75f, 12f, 11f, 11f };
        float size = sizes[Math.Max(1, Math.Min(6, b.Level)) - 1];
        if (!first) y += b.Level <= 2 ? 16 : 11;
        // Un título no se queda solo al final de la página: se pide sitio para él y un par de líneas más.
        Ensure(size * 1.3f + BodySize * 3.2f);
        Paragraph(b.Inlines, Left, Width, size, FontStyle.Bold, HeadingColor, BodyFont, null);
        if (b.Level == 1)
        {
            y += 3;
            float ly = y;
            Draw(g => { using (var p = new Pen(RuleColor, 0.75f)) g.DrawLine(p, Left, ly, Right, ly); });
            y += 4;
        }
        y += 6;
    }

    void ListItem(Block b, int[] counters)
    {
        int level = Math.Max(0, Math.Min(counters.Length - 1, b.Level));
        for (int l = level + 1; l < counters.Length; l++) counters[l] = 0;
        float indent = 18 + level * 18;
        string marker;
        if (b.Kind == BlockKind.Numbered) marker = (++counters[level]) + ".";
        else marker = level % 3 == 0 ? "•" : level % 3 == 1 ? "◦" : "▪";
        var font = F(BodyFont, BodySize, FontStyle.Regular);
        float markerW = TextWidth(marker, font);
        bool firstLine = true;
        foreach (var line in Wrap(b.Inlines, Width - indent, BodySize, FontStyle.Regular, TextColor, BodyFont))
        {
            Ensure(line.Height);
            if (firstLine)
            {
                float mx = Left + indent - 6 - markerW, my = y + line.Ascent - Ascent(font);
                string m = marker;
                Draw(g => { using (var br = new SolidBrush(TextColor)) g.DrawString(m, font, br, mx, my, typo); });
                firstLine = false;
            }
            DrawLine(line, Left + indent, y);
            y += line.Height;
        }
    }

    void Quote(Block b)
    {
        y += 4;
        float pad = 12;
        Paragraph(b.Inlines, Left + pad + 4, Width - pad - 8, BodySize, FontStyle.Regular, Muted, BodyFont, (top, h) =>
        {
            Draw(g =>
            {
                using (var back = new SolidBrush(QuoteBack)) g.FillRectangle(back, Left, top - 2, Width, h + 4);
                using (var bar = new SolidBrush(QuoteBar)) g.FillRectangle(bar, Left, top - 2, 3, h + 4);
            });
        });
        y += 12;
    }

    void CodeBlock(Block b)
    {
        var font = F(CodeFont, CodeSize, FontStyle.Regular);
        float lineH = CellHeight(font) * 1.35f, pad = 8, inner = Width - pad * 2;
        y += 2;
        var rows = new List<string>();
        foreach (var raw in (b.CodeText ?? "").Replace("\r", "").Split('\n'))
        {
            string rest = raw.Replace("\t", "    ");
            // Líneas largas: se parten por el último espacio que cabe (como pre-wrap); si no hay, donde llegue.
            while (rest.Length > 0 && TextWidth(rest, font) > inner)
            {
                int n = rest.Length - 1;
                while (n > 1 && TextWidth(rest.Substring(0, n), font) > inner) n--;
                int space = rest.LastIndexOf(' ', n - 1, n);
                if (space > n / 2) n = space + 1;
                rows.Add(rest.Substring(0, n));
                rest = rest.Substring(n);
            }
            rows.Add(rest);
        }
        for (int i = 0; i < rows.Count; i++)
        {
            bool first = i == 0, last = i == rows.Count - 1;
            float h = lineH + (first ? pad : 0) + (last ? pad : 0);
            Ensure(h);
            float top = y, ty = y + (first ? pad : 0) + (lineH - CellHeight(font)) / 2;
            string text = rows[i];
            Draw(g =>
            {
                using (var back = new SolidBrush(CodeBlockBack)) g.FillRectangle(back, Left, top, Width, h);
                using (var br = new SolidBrush(TextColor)) g.DrawString(text, font, br, Left + pad, ty, typo);
            });
            y += h;
        }
        y += 10;
    }

    void Table(Block b)
    {
        if (b.Rows == null || b.Rows.Count == 0) return;
        int cols = b.Rows.Max(r => r.Count);
        if (cols == 0) return;
        const float pad = 6;
        var font = F(BodyFont, BodySize, FontStyle.Regular);

        // Ancho natural (todo en una línea) y mínimo (la palabra más larga) de cada columna.
        var natural = new float[cols];
        var minimum = new float[cols];
        var bold = F(BodyFont, BodySize, FontStyle.Bold);
        for (int r = 0; r < b.Rows.Count; r++)
        {
            var cellsOfRow = b.Rows[r];
            var measureFont = r == 0 ? bold : font;   // la cabecera va en negrita: ocupa más
            for (int c = 0; c < cellsOfRow.Count; c++)
            {
                string text = string.Concat(cellsOfRow[c].Select(n => n.Text));
                natural[c] = Math.Max(natural[c], TextWidth(text, measureFont) + pad * 2 + 2);
                foreach (var word in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    minimum[c] = Math.Max(minimum[c], Math.Min(Width / cols, TextWidth(word, measureFont) + pad * 2 + 2));
            }
        }
        var widths = natural.ToArray();
        float total = widths.Sum();
        if (total > Width)
        {
            // Se reparte el ancho sobrante en proporción a lo que cada columna querría por encima de su mínimo.
            float minTotal = minimum.Sum(), extra = Math.Max(0, Width - minTotal), wanted = 0;
            for (int c = 0; c < cols; c++) wanted += natural[c] - minimum[c];
            for (int c = 0; c < cols; c++)
                widths[c] = minimum[c] + (wanted > 0 ? extra * (natural[c] - minimum[c]) / wanted : extra / cols);
            total = widths.Sum();
        }

        Action<List<List<Inline>>, bool> row = null;
        row = (cells, header) =>
        {
            var laid = new List<List<Line>>();
            float h = 0;
            for (int c = 0; c < cols; c++)
            {
                var content = c < cells.Count ? cells[c] : new List<Inline>();
                var lines = Wrap(content, widths[c] - pad * 2, BodySize, header ? FontStyle.Bold : FontStyle.Regular, TextColor, BodyFont);
                laid.Add(lines);
                h = Math.Max(h, lines.Sum(l => l.Height) + pad * 1.2f);
            }
            float top = y;
            float x = Left;
            for (int c = 0; c < cols; c++)
            {
                float cx = x, cw = widths[c], ch = h;
                Draw(g =>
                {
                    if (header) using (var back = new SolidBrush(HeaderBack)) g.FillRectangle(back, cx, top, cw, ch);
                    using (var p = new Pen(Border, 0.75f)) g.DrawRectangle(p, cx, top, cw, ch);
                });
                float ly = top + pad * 0.6f;
                foreach (var line in laid[c]) { DrawLine(line, cx + pad, ly); ly += line.Height; }
                x += widths[c];
            }
            y += h;
        };

        y += 2;
        for (int r = 0; r < b.Rows.Count; r++)
        {
            float estimate = BodySize * LineFactor * 1.6f;
            if (y + estimate > Bottom && y > Top)
            {
                NewPage();
                if (r > 0) row(b.Rows[0], true);   // la cabecera se repite en la página nueva
            }
            row(b.Rows[r], r == 0);
        }
        y += 12;
    }

    void Picture(Inline inl)
    {
        Image img = null;
        try
        {
            string path = inl.Link ?? "";
            if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            if (!Regex.IsMatch(path, "^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase))
            {
                if (!Path.IsPathRooted(path) && baseDir != null) path = Path.Combine(baseDir, WebUtility.UrlDecode(path));
                if (File.Exists(path))
                {
                    using (var full = ImageOps.Load(path))
                    {
                        // Las fotos grandes se reducen: en el PDF no hace falta más de ~200 ppp.
                        float k = Math.Min(1f, 1600f / Math.Max(full.Width, full.Height));
                        var small = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(full, 0, 0, small.Width, small.Height);
                        }
                        img = small;
                        images.Add(img);
                    }
                }
            }
        }
        catch { img = null; }

        if (img == null)
        {
            // Sin imagen (no existe o es de internet): queda el texto alternativo.
            Paragraph(new List<Inline> { new Inline { Text = "[imagen" + (string.IsNullOrEmpty(inl.Text) ? "" : ": " + inl.Text) + "]", Italic = true } },
                      Left, Width, BodySize, FontStyle.Regular, Muted, BodyFont, null);
            y += 8;
            return;
        }

        // Tamaño natural a 96 ppp, sin pasar del ancho ni del alto de la página.
        float w = img.Width * 0.75f, h = img.Height * 0.75f;
        float scale = Math.Min(1f, Math.Min(Width / w, (Bottom - Top) / h));
        w *= scale; h *= scale;
        Ensure(h);
        float top = y;
        var image = img;
        Draw(g =>
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, Left, top, w, h);
        });
        y += h + 10;
    }

    public void Dispose()
    {
        foreach (var f in fonts.Values) f.Dispose();
        foreach (var i in images) i.Dispose();
        measure.Dispose();
        measureBitmap.Dispose();
        typo.Dispose();
    }
}

// ---------------------------------------------------------------------------------------------
// Salida a PDF
// ---------------------------------------------------------------------------------------------

static class DocumentPdf
{
    const string PdfPrinter = "Microsoft Print to PDF";
    static readonly object Gate = new object();

    public static void Write(List<Block> blocks, string output, string baseDir)
    {
        using (var layout = new DocumentLayout(blocks, baseDir))
        {
            bool printed = false;
            if (PrinterSettings.InstalledPrinters.Cast<string>().Contains(PdfPrinter))
            {
                try { Print(layout, output); printed = true; }
                catch { if (File.Exists(output)) try { File.Delete(output); } catch { } }
            }
            // Sin la impresora PDF de Windows (o si falla), cada página va como imagen: siempre hay PDF.
            if (!printed) WriteAsImages(layout, output);
        }
    }

    // Imprime en silencio en «Microsoft Print to PDF»: el PDF lleva texto de verdad y las fuentes incrustadas.
    static void Print(DocumentLayout layout, string output)
    {
        lock (Gate)
        {
            using (var doc = new PrintDocument())
            {
                doc.PrinterSettings.PrinterName = PdfPrinter;
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = output;
                doc.PrintController = new StandardPrintController();   // sin diálogos
                doc.DocumentName = Path.GetFileNameWithoutExtension(output);
                var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
                if (a4 != null) doc.DefaultPageSettings.PaperSize = a4;
                doc.DefaultPageSettings.Landscape = false;
                doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

                int index = 0;
                doc.PrintPage += (s, e) =>
                {
                    var g = e.Graphics;
                    g.PageUnit = GraphicsUnit.Point;
                    // El origen está en la zona imprimible: se lleva a la esquina del papel.
                    g.TranslateTransform(-e.PageSettings.HardMarginX * 0.72f, -e.PageSettings.HardMarginY * 0.72f);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    foreach (var op in layout.Pages[index]) op(g);
                    index++;
                    e.HasMorePages = index < layout.Pages.Count;
                };
                if (File.Exists(output)) File.Delete(output);
                doc.Print();
            }
            WaitForFile(output);
        }
    }

    // La cola de impresión escribe el PDF poco después de Print(): se espera a que esté completo y libre.
    static void WaitForFile(string path)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 60)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        if (fs.Length > 16)
                        {
                            fs.Seek(-16, SeekOrigin.End);
                            var tail = new byte[16];
                            fs.Read(tail, 0, 16);
                            if (System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF")) return;
                        }
                    }
                }
            }
            catch (IOException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("La impresora PDF de Windows no terminó el archivo.");
    }

    // Respaldo: cada página dibujada a 150 ppp y metida en el PDF como imagen.
    public static void WriteAsImages(DocumentLayout layout, string output)
    {
        const float dpi = 150;
        int w = (int)Math.Round(DocumentLayout.PageWidth / 72 * dpi), h = (int)Math.Round(DocumentLayout.PageHeight / 72 * dpi);
        var pages = new List<Bitmap>();
        try
        {
            foreach (var ops in layout.Pages)
            {
                var bmp = new Bitmap(w, h);
                bmp.SetResolution(dpi, dpi);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.PageUnit = GraphicsUnit.Point;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    foreach (var op in ops) op(g);
                }
                pages.Add(bmp);
            }
            PdfWriter.FromBitmaps(pages, DocumentLayout.PageWidth, DocumentLayout.PageHeight, output);
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }
}

// ---------------------------------------------------------------------------------------------
// HTML: navegador si lo hay, lector propio si no
// ---------------------------------------------------------------------------------------------

static class Browser
{
    static readonly object Gate = new object();

    // Edge, Chrome o Brave (cualquiera sirve: admiten la misma orden de imprimir a PDF).
    public static string Find()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        var exes = new[]
        {
            @"Microsoft\Edge\Application\msedge.exe",
            @"Google\Chrome\Application\chrome.exe",
            @"BraveSoftware\Brave-Browser\Application\brave.exe"
        };
        foreach (var exe in exes)
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, exe);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static void PrintToPdf(string exe, string htmlPath, string pdfPath)
    {
        // Perfil propio (uno por navegador) para no interferir con el que tenga abierto el usuario.
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Switchify",
                                      Path.GetFileNameWithoutExtension(exe));
        string args = string.Format("--headless --disable-gpu --no-first-run --no-default-browser-check --no-pdf-header-footer " +
                                    "--user-data-dir=\"{0}\" --print-to-pdf=\"{1}\" \"{2}\"",
                                    profile, pdfPath, new Uri(htmlPath).AbsoluteUri);
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        // De uno en uno: dos instancias con el mismo perfil a la vez hacen que la segunda termine sin generar nada.
        lock (Gate)
        {
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("El navegador tardó demasiado en generar el PDF.");
                }
            }
        }
        if (!File.Exists(pdfPath)) throw new IOException("El navegador no generó el PDF.");
    }
}

// HTML → bloques. No interpreta CSS: conserva la estructura (títulos, párrafos, listas, citas, código, tablas,
// enlaces, imágenes, negrita, cursiva) para maquetarla como cualquier otro documento.
static class HtmlReader
{
    static readonly Regex Token = new Regex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>|([^<]+)", RegexOptions.Singleline);
    static readonly Regex Attr = new Regex(@"([a-zA-Z-]+)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)");

    public static List<Block> Read(string html)
    {
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<![^>]*>|<\?.*?\?>", "", RegexOptions.Singleline);   // <!DOCTYPE …>, <?xml …?>
        html = Regex.Replace(html, @"<(script|style|head|noscript|template)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var blocks = new List<Block>();
        Block current = null;
        var lists = new Stack<BlockKind>();
        int bold = 0, italic = 0, code = 0, quote = 0;
        var links = new Stack<string>();
        bool pre = false;
        var preText = new System.Text.StringBuilder();
        Block table = null;
        List<List<Inline>> tableRow = null;
        List<Inline> cell = null;

        Action flush = () =>
        {
            if (current != null && current.Inlines.Any(i => i.Image || !string.IsNullOrWhiteSpace(i.Text)))
            {
                // Sin espacios sobrantes al principio y al final del bloque.
                var first = current.Inlines.FirstOrDefault(i => !i.Image);
                if (first != null) first.Text = first.Text.TrimStart();
                var last = current.Inlines.LastOrDefault(i => !i.Image);
                if (last != null) last.Text = last.Text.TrimEnd();
                blocks.Add(current);
            }
            current = null;
        };
        Func<BlockKind, int, Block> start = (kind, level) =>
        {
            flush();
            current = new Block { Kind = kind, Level = level };
            return current;
        };
        Action<Inline> add = inl =>
        {
            if (cell != null) { cell.Add(inl); return; }
            if (current == null) start(quote > 0 ? BlockKind.Quote : BlockKind.Paragraph, 0);
            current.Inlines.Add(inl);
        };

        foreach (Match m in Token.Matches(html))
        {
            if (m.Groups[4].Success)
            {
                string text = WebUtility.HtmlDecode(m.Groups[4].Value);
                if (pre) { preText.Append(text); continue; }
                text = Regex.Replace(text, @"\s+", " ");
                if (text == " " && (cell != null ? cell.Count == 0 : current == null)) continue;
                add(new Inline { Text = text, Bold = bold > 0, Italic = italic > 0, Code = code > 0, Link = links.Count > 0 ? links.Peek() : null });
                continue;
            }

            bool closing = m.Groups[1].Value == "/";
            string tag = m.Groups[2].Value.ToLowerInvariant();
            var attrs = Attr.Matches(m.Groups[3].Value).Cast<Match>()
                .GroupBy(a => a.Groups[1].Value.ToLowerInvariant())
                .ToDictionary(gr => gr.Key, gr => WebUtility.HtmlDecode(gr.First().Groups[2].Value.Trim('"', '\'')));

            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    if (closing) flush(); else start(BlockKind.Heading, tag[1] - '0');
                    break;
                case "p": case "div": case "section": case "article": case "header": case "footer": case "main":
                    if (cell == null) flush();
                    break;
                case "br":
                    if (pre) preText.Append('\n'); else add(new Inline { Text = "\n" });
                    break;
                case "hr":
                    flush();
                    blocks.Add(new Block { Kind = BlockKind.Rule });
                    break;
                case "ul": case "ol":
                    flush();
                    if (closing) { if (lists.Count > 0) lists.Pop(); }
                    else lists.Push(tag == "ul" ? BlockKind.Bullet : BlockKind.Numbered);
                    break;
                case "li":
                    if (closing) flush();
                    else start(lists.Count > 0 ? lists.Peek() : BlockKind.Bullet, Math.Max(0, lists.Count - 1));
                    break;
                case "blockquote":
                    flush();
                    quote += closing ? -1 : 1;
                    break;
                case "pre":
                    flush();
                    if (!closing) { pre = true; preText.Clear(); }
                    else
                    {
                        pre = false;
                        blocks.Add(new Block { Kind = BlockKind.Code, CodeText = preText.ToString().Trim('\n', '\r') });
                    }
                    break;
                case "code": case "kbd": case "samp": case "tt":
                    if (!pre) code += closing ? -1 : 1;
                    break;
                case "b": case "strong":
                    bold += closing ? -1 : 1;
                    break;
                case "i": case "em": case "cite":
                    italic += closing ? -1 : 1;
                    break;
                case "a":
                    if (closing) { if (links.Count > 0) links.Pop(); }
                    else links.Push(attrs.ContainsKey("href") ? attrs["href"] : null);
                    break;
                case "img":
                    if (attrs.ContainsKey("src"))
                        add(new Inline { Image = true, Link = attrs["src"], Text = attrs.ContainsKey("alt") ? attrs["alt"] : "" });
                    break;
                case "table":
                    flush();
                    if (!closing) table = new Block { Kind = BlockKind.Table, Rows = new List<List<List<Inline>>>() };
                    else if (table != null) { if (table.Rows.Count > 0) blocks.Add(table); table = null; }
                    break;
                case "tr":
                    if (table != null && !closing) { tableRow = new List<List<Inline>>(); table.Rows.Add(tableRow); }
                    break;
                case "td": case "th":
                    if (closing) cell = null;
                    else if (tableRow != null) { cell = new List<Inline>(); tableRow.Add(cell); }
                    break;
            }
            if (bold < 0) bold = 0;
            if (italic < 0) italic = 0;
            if (code < 0) code = 0;
            if (quote < 0) quote = 0;
        }
        flush();
        return blocks;
    }
}
