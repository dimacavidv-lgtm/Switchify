// Estructura de un documento a partir de su maquetación: líneas de texto con posición, tamaño y estilo
// (del texto del PDF o de las palabras del OCR) -> títulos, párrafos, listas, tablas y código.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Trozo de texto con un mismo estilo.
class LayoutRun
{
    public string Text;
    public bool Bold, Italic, Mono;
}

// Parte de una línea separada de la siguiente por un hueco grande (columnas de una tabla).
class LayoutSegment
{
    public double X0, X1;
    public List<LayoutRun> Runs = new List<LayoutRun>();
    public string Text { get { return string.Concat(Runs.Select(r => r.Text)).Trim(); } }
}

class LayoutLine
{
    public int Page;
    public double Top, Bottom, Baseline, Size;      // Size: tamaño de letra
    public List<LayoutSegment> Segments = new List<LayoutSegment>();
    public double X0 { get { return Segments.Count == 0 ? 0 : Segments[0].X0; } }
    public double X1 { get { return Segments.Count == 0 ? 0 : Segments[Segments.Count - 1].X1; } }
    public string Text { get { return string.Join(" ", Segments.Select(s => s.Text)).Trim(); } }
    public IEnumerable<LayoutRun> Runs { get { return Segments.SelectMany(s => s.Runs); } }
    public bool AllBold { get { var r = Runs.Where(x => x.Text.Trim().Length > 0).ToList(); return r.Count > 0 && r.All(x => x.Bold); } }
    public bool AllMono { get { var r = Runs.Where(x => x.Text.Trim().Length > 0).ToList(); return r.Count > 0 && r.All(x => x.Mono); } }
}

class LayoutPage
{
    public double Width, Height;
    public bool Ocr;                                    // del OCR: tamaños y posiciones menos precisos
    public List<LayoutLine> Lines = new List<LayoutLine>();
}

static class Layout
{
    // --- Del PDF a líneas ---------------------------------------------------------------------

    public static List<LayoutPage> FromPdf(List<PdfTextPage> pages)
    {
        var result = new List<LayoutPage>();
        for (int p = 0; p < pages.Count; p++)
        {
            var page = new LayoutPage { Width = pages[p].Width, Height = pages[p].Height };
            var glyphs = pages[p].Glyphs.Where(g => g.Text.Length > 0).OrderBy(g => g.Y).ThenBy(g => g.X).ToList();

            // Agrupar por línea base.
            var groups = new List<List<PdfGlyph>>();
            foreach (var g in glyphs)
            {
                var last = groups.Count == 0 ? null : groups[groups.Count - 1];
                if (last != null)
                {
                    double y = last.Average(x => x.Y), size = Math.Max(last.Max(x => x.Size), g.Size);
                    if (Math.Abs(g.Y - y) < size * 0.4) { last.Add(g); continue; }
                }
                groups.Add(new List<PdfGlyph> { g });
            }

            foreach (var group in groups)
            {
                var line = BuildLine(group.OrderBy(g => g.X).ToList(), p);
                if (line != null) page.Lines.Add(line);
            }
            result.Add(page);
        }
        return result;
    }

    static LayoutLine BuildLine(List<PdfGlyph> glyphs, int page)
    {
        // Letras repetidas en el mismo sitio (negrita "falsa" dibujada dos veces): se quedan una vez.
        var unique = new List<PdfGlyph>();
        foreach (var g in glyphs)
        {
            bool dup = unique.Skip(Math.Max(0, unique.Count - 4)).Any(u => u.Text == g.Text && Math.Abs(u.X - g.X) < g.Size * 0.2);
            if (!dup) unique.Add(g);
        }
        var visible = unique.Where(g => g.Text.Trim().Length > 0).ToList();
        if (visible.Count == 0) return null;

        double size = Mode(visible.Select(g => g.Size), visible.Select(g => (double)g.Text.Length));
        var line = new LayoutLine
        {
            Page = page,
            Size = size,
            Baseline = visible.Average(g => g.Y),
        };
        line.Top = line.Baseline - size * 0.8;
        line.Bottom = line.Baseline + size * 0.25;

        LayoutSegment seg = null;
        double lastEnd = double.NaN;
        bool pendingSpace = false;
        foreach (var g in unique)
        {
            if (g.Text.Trim().Length == 0)
            {
                pendingSpace = true;
                if (!double.IsNaN(lastEnd)) lastEnd = Math.Max(lastEnd, g.X + g.Width);
                continue;
            }
            double gap = double.IsNaN(lastEnd) ? 0 : g.X - lastEnd;
            if (seg == null || gap > size * 1.0)
            {
                seg = new LayoutSegment { X0 = g.X };
                line.Segments.Add(seg);
                pendingSpace = false;
            }
            else if (pendingSpace || gap > g.Size * 0.18)
            {
                Append(seg, " ", g);
            }
            pendingSpace = false;
            Append(seg, g.Text, g);
            seg.X1 = Math.Max(seg.X1, g.X + g.Width);
            lastEnd = g.X + g.Width;
        }
        return line;
    }

    static void Append(LayoutSegment seg, string text, PdfGlyph g)
    {
        var last = seg.Runs.Count == 0 ? null : seg.Runs[seg.Runs.Count - 1];
        // Los espacios no cambian de estilo: se pegan al trozo anterior.
        if (last != null && (text == " " || (last.Bold == g.Bold && last.Italic == g.Italic && last.Mono == g.Mono))) { last.Text += text; return; }
        seg.Runs.Add(new LayoutRun { Text = text, Bold = g.Bold, Italic = g.Italic, Mono = g.Mono });
    }

    // --- Del OCR a líneas ---------------------------------------------------------------------

    public class OcrWordBox { public string Text; public double X, Y, W, H; }

    // `lines`: las líneas del OCR, cada una con sus palabras. Las que están a la misma altura se unen (el OCR
    // separa a veces las columnas de una tabla en líneas distintas).
    public static LayoutPage FromOcr(List<List<OcrWordBox>> lines, double width, double height, int pageIndex)
    {
        var page = new LayoutPage { Width = width, Height = height, Ocr = true };
        var words = lines.Where(l => l.Count > 0).ToList();
        // Tamaño de letra aproximado: la altura de las palabras incluye ascendentes y descendentes.
        var merged = new List<List<OcrWordBox>>();
        foreach (var l in words.OrderBy(l => l.Min(w => w.Y)))
        {
            double top = l.Min(w => w.Y), bottom = l.Max(w => w.Y + w.H);
            var target = merged.FirstOrDefault(m =>
            {
                double t = m.Min(w => w.Y), b = m.Max(w => w.Y + w.H);
                double overlap = Math.Min(b, bottom) - Math.Max(t, top);
                return overlap > 0.6 * Math.Min(b - t, bottom - top);
            });
            if (target != null) target.AddRange(l); else merged.Add(new List<OcrWordBox>(l));
        }
        foreach (var m in merged)
        {
            var sorted = m.OrderBy(w => w.X).ToList();
            // Tamaño de letra: cada palabra se mide según sus letras (con mayúsculas o letras que suben o bajan la
            // caja es más alta que con solo minúsculas cortas), y se toma la mediana de la línea.
            double size = Median(sorted.Select(w => w.H / EmFraction(w.Text)));
            double h = size * 0.75;
            var line = new LayoutLine { Page = pageIndex, Size = size };
            line.Top = sorted.Min(w => w.Y);
            line.Bottom = sorted.Max(w => w.Y + w.H);
            line.Baseline = line.Bottom - h * 0.2;
            LayoutSegment seg = null;
            double lastEnd = 0;
            foreach (var w in sorted)
            {
                if (seg == null || w.X - lastEnd > size * 1.2)
                {
                    seg = new LayoutSegment { X0 = w.X };
                    line.Segments.Add(seg);
                    seg.Runs.Add(new LayoutRun { Text = w.Text });
                }
                else seg.Runs[0].Text += " " + w.Text;
                seg.X1 = w.X + w.W;
                lastEnd = seg.X1;
            }
            page.Lines.Add(line);
        }
        page.Lines = page.Lines.OrderBy(l => l.Top).ToList();
        return page;
    }

    // Qué parte del tamaño de letra ocupa la caja de una palabra.
    static double EmFraction(string word)
    {
        bool up = Regex.IsMatch(word, @"[A-ZÁÉÍÓÚÑÜ0-9bdfhklti¿¡áéíóúüñ'""()/%&@#]");
        bool down = Regex.IsMatch(word, @"[gjpqyQç,;()]");
        return up && down ? 0.95 : up ? 0.72 : down ? 0.7 : 0.48;
    }

    // --- De líneas a bloques -----------------------------------------------------------------

    static readonly Regex BulletRx = new Regex(@"^([•·▪◦‣○●■□►▸➢➤✓✔–—\-\*])\s*(.*)$");
    static readonly Regex NumberRx = new Regex(@"^(\(?(\d{1,3}|[a-z]|[ivx]{1,4})[.)])\s+(.*)$");
    static readonly Regex PageNumberRx = new Regex(@"^(p(á|a)g(ina)?\.?\s*)?[-–—]?\s*\d{1,4}\s*([-–—]|(de|of|/)\s*\d{1,4})?\s*$", RegexOptions.IgnoreCase);

    public static List<Block> ToBlocks(List<LayoutPage> pages)
    {
        RemoveHeadersAndFooters(pages);
        var all = pages.SelectMany(p => p.Lines).ToList();
        if (all.Count == 0) return new List<Block>();

        bool ocr = pages.Any(p => p.Ocr);
        var t = new Tolerances
        {
            Size = ocr ? 0.25 : 0.12,
            Heading = ocr ? 1.18 : 1.15,
            SameLevel = ocr ? 0.9 : 0.93,
            // El OCR mide la letra por la caja de las palabras, más pequeña que el tamaño real: el interlineado
            // relativo sale mayor.
            MaxSpacing = ocr ? 4.5 : 2.2
        };
        // El tamaño del cuerpo: el más frecuente. Con OCR los tamaños varían de una línea a otra: se agrupan antes.
        double body = ocr ? Median(all.SelectMany(l => Enumerable.Repeat(l.Size, Math.Max(1, l.Text.Length / 10))))
                          : Mode(all.Select(l => l.Size), all.Select(l => (double)l.Text.Length));
        // Niveles de título: tamaños claramente mayores que el cuerpo, de mayor a menor; los parecidos son el mismo nivel.
        var headingSizes = new List<double>();
        foreach (var size in all.Where(l => l.Size >= body * t.Heading && l.Text.Length <= 150).Select(l => l.Size).OrderByDescending(x => x))
            if (headingSizes.Count == 0 || size < headingSizes[headingSizes.Count - 1] * t.SameLevel) headingSizes.Add(size);
        int boldLevel = Math.Min(4, Math.Max(2, headingSizes.Count + 1));

        var blocks = new List<Block>();
        foreach (var page in pages)
        {
            var pageBlocks = PageBlocks(page, body, headingSizes, boldLevel, t);
            // Párrafo partido entre páginas: el anterior no acaba en punto y el siguiente empieza en minúscula.
            if (blocks.Count > 0 && pageBlocks.Count > 0)
            {
                var prev = blocks[blocks.Count - 1];
                var next = pageBlocks[0];
                if (prev.Kind == BlockKind.Paragraph && next.Kind == BlockKind.Paragraph)
                {
                    string pt = TextWriterPlain.Text(prev.Inlines).TrimEnd(), nt = TextWriterPlain.Text(next.Inlines).TrimStart();
                    if (pt.Length > 0 && nt.Length > 0 && !".:!?".Contains(pt[pt.Length - 1]) && char.IsLower(nt[0]))
                    {
                        JoinInlines(prev.Inlines, next.Inlines);
                        pageBlocks.RemoveAt(0);
                    }
                }
            }
            blocks.AddRange(pageBlocks);
        }
        return blocks;
    }

    // Cabeceras y pies que se repiten en las páginas, y números de página sueltos.
    static void RemoveHeadersAndFooters(List<LayoutPage> pages)
    {
        Func<string, string> norm = s => Regex.Replace(s.ToLowerInvariant(), @"\d+", "#").Trim();
        var counts = new Dictionary<string, int>();
        foreach (var p in pages)
            foreach (var key in p.Lines.Where(l => InMargin(l, p)).Select(l => norm(l.Text)).Distinct())
                counts[key] = (counts.ContainsKey(key) ? counts[key] : 0) + 1;
        foreach (var p in pages)
        {
            p.Lines = p.Lines.Where(l =>
            {
                if (!InMargin(l, p)) return true;
                string t = l.Text;
                if (PageNumberRx.IsMatch(t)) return false;
                return !(pages.Count >= 3 && counts[norm(t)] >= Math.Max(2, pages.Count / 2));
            }).ToList();
        }
    }

    static bool InMargin(LayoutLine l, LayoutPage p)
    {
        return l.Top < p.Height * 0.08 || l.Bottom > p.Height * 0.92;
    }

    class Tolerances { public double Size, Heading, SameLevel, MaxSpacing; }

    static List<Block> PageBlocks(LayoutPage page, double body, List<double> headingSizes, int boldLevel, Tolerances tol)
    {
        var blocks = new List<Block>();
        var lines = page.Lines.OrderBy(l => l.Top).ThenBy(l => l.X0).ToList();
        if (lines.Count == 0) return blocks;

        double left = lines.Min(l => l.X0);
        double right = lines.Max(l => l.X1);
        var spacings = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            double d = lines[i].Baseline - lines[i - 1].Baseline;
            if (d > 0 && d < body * tol.MaxSpacing * 1.3 && Math.Abs(lines[i].Size - body) < body * tol.Size && Math.Abs(lines[i - 1].Size - body) < body * tol.Size) spacings.Add(d);
        }
        double spacing = spacings.Count > 0 ? Median(spacings) : body * 1.25;
        spacing = Math.Max(body * 1.0, Math.Min(body * tol.MaxSpacing, spacing));

        Block para = null, item = null;
        LayoutLine prev = null;
        var xOf = new Dictionary<Block, double>();        // sangría de cada bloque (para los niveles de lista)
        var lineCount = new Dictionary<Block, int>();
        double itemTextX = 0;
        var code = new List<LayoutLine>();

        Action flushCode = () =>
        {
            if (code.Count == 0) return;
            double minX = code.Min(l => l.X0);
            var sb = new StringBuilder();
            foreach (var l in code)
            {
                int indent = (int)Math.Round((l.X0 - minX) / (l.Size * 0.6));
                sb.Append(new string(' ', Math.Max(0, indent))).Append(string.Join("   ", l.Segments.Select(s => s.Text))).Append('\n');
            }
            blocks.Add(new Block { Kind = BlockKind.Code, CodeText = sb.ToString().TrimEnd('\n') });
            code.Clear();
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string text = line.Text;
            if (text.Length == 0) continue;
            double gap = prev == null ? double.MaxValue : line.Baseline - prev.Baseline;

            // Código: líneas en letra de ancho fijo.
            if (line.AllMono && line.Size <= body * 1.1)
            {
                para = null; item = null;
                if (code.Count > 0 && gap > spacing * 2.2) flushCode();
                code.Add(line);
                prev = line;
                continue;
            }
            flushCode();

            // Tabla.
            int end;
            var table = TableAt(lines, i, body, spacing, out end);
            if (table != null)
            {
                blocks.Add(table);
                para = null; item = null;
                prev = lines[end - 1];
                i = end - 1;
                continue;
            }

            // Título.
            int level = HeadingLevel(line, body, headingSizes, boldLevel, tol);
            if (level > 0)
            {
                var last = blocks.Count > 0 ? blocks[blocks.Count - 1] : null;
                // Título partido en dos líneas: mismo nivel y muy juntas.
                if (last != null && last.Kind == BlockKind.Heading && last.Level == level && prev != null && gap < line.Size * 1.6 && para == null && item == null)
                    JoinInlines(last.Inlines, Inlines(line));
                else
                    blocks.Add(new Block { Kind = BlockKind.Heading, Level = level, Inlines = Inlines(line, true) });
                para = null; item = null;
                prev = line;
                continue;
            }

            // Elemento de lista.
            Match m;
            string first = line.Segments[0].Text;
            if ((m = BulletRx.Match(text)).Success && (m.Groups[2].Value.Length > 0 || line.Segments.Count > 1))
            {
                bool bullet = true;
                // "-5 %" o "—" sueltos no son viñetas: tiene que seguir texto separado.
                if ((m.Groups[1].Value == "-" || m.Groups[1].Value == "*") && !Regex.IsMatch(text, @"^[-*]\s")) bullet = false;
                if (bullet)
                {
                    item = new Block { Kind = BlockKind.Bullet, Inlines = Inlines(line, false, m.Groups[1].Value.Length) };
                    blocks.Add(item);
                    xOf[item] = line.X0;
                    itemTextX = line.Segments.Count > 1 && first.Length <= 2 ? line.Segments[1].X0 : line.X0 + body;
                    para = null;
                    prev = line;
                    continue;
                }
            }
            if ((m = NumberRx.Match(text)).Success)
            {
                item = new Block { Kind = BlockKind.Numbered, Inlines = Inlines(line, false, m.Groups[1].Value.Length) };
                blocks.Add(item);
                xOf[item] = line.X0;
                itemTextX = line.Segments.Count > 1 && first.Length <= 5 ? line.Segments[1].X0 : line.X0 + body;
                para = null;
                prev = line;
                continue;
            }

            // Continuación de un elemento de lista: sangrada como su texto y pegada a él.
            if (item != null && prev != null && gap <= spacing * 1.4 && line.X0 > itemTextX + body * 0.8)
            {
                // Más sangrada que el texto del punto: un subpunto cuya viñeta no se ve (dibujada, o que el OCR no lee).
                item = new Block { Kind = BlockKind.Bullet, Inlines = Inlines(line) };
                blocks.Add(item);
                xOf[item] = line.X0 - body;
                itemTextX = line.X0;
                prev = line;
                continue;
            }
            if (item != null && prev != null && gap <= spacing * 1.4 && line.X0 >= itemTextX - body * 0.6)
            {
                // Si el punto anterior ya acabó en punto y esta empieza en mayúscula, es otro punto (el OCR a veces
                // no ve la viñeta).
                // Igual si la línea anterior se quedó corta (no estaba cortada por el ancho) y esta empieza en mayúscula.
                string it = TextWriterPlain.Text(item.Inlines).TrimEnd();
                bool ended = it.Length > 0 && ".!?".Contains(it[it.Length - 1]);
                if ((ended || prev.X1 < right - body * 6) && char.IsUpper(text[0]))
                {
                    item = new Block { Kind = item.Kind, Inlines = Inlines(line) };
                    blocks.Add(item);
                    xOf[item] = itemTextX - body;
                }
                else JoinInlines(item.Inlines, Inlines(line));
                prev = line;
                continue;
            }
            item = null;

            // Párrafo: sigue si está pegado al anterior, del mismo tamaño, y el anterior no era una línea corta
            // que acababa en punto (fin de párrafo).
            if (para != null && prev != null)
            {
                string pt = prev.Text;
                bool sameSize = Math.Abs(line.Size - prev.Size) <= prev.Size * tol.Size;
                bool close = gap <= spacing * 1.35;
                bool endedShort = pt.Length > 0 && ".:!?".Contains(pt[pt.Length - 1]) && prev.X1 < right - body * 4;
                bool indented = line.X0 > prev.X0 + body * 1.2 && pt.Length > 0 && ".:!?".Contains(pt[pt.Length - 1]);
                // Un párrafo cortado por el ancho llega casi al margen derecho; si la línea anterior se quedó muy corta,
                // o algo corta y la siguiente empieza en otra sangría, son líneas sueltas (p. ej. una lista sin viñetas).
                bool shift = Math.Abs(line.X0 - prev.X0) > body * 0.8;
                bool shortPrev = prev.X1 < right - body * 8 || (prev.X1 < right - body * 3 && shift);
                if (sameSize && close && !endedShort && !indented && !shortPrev)
                {
                    JoinInlines(para.Inlines, Inlines(line));
                    lineCount[para]++;
                    prev = line;
                    continue;
                }
            }
            para = new Block { Kind = BlockKind.Paragraph, Inlines = Inlines(line) };
            blocks.Add(para);
            xOf[para] = line.X0;
            lineCount[para] = 1;
            prev = line;
        }
        flushCode();

        // Listas sin viñeta escrita (el navegador las dibuja): dos o más líneas sueltas seguidas y sangradas.
        for (int i = 0; i < blocks.Count; i++)
        {
            int j = i;
            while (j < blocks.Count && blocks[j].Kind == BlockKind.Paragraph && lineCount.ContainsKey(blocks[j]) && lineCount[blocks[j]] == 1 && xOf[blocks[j]] >= left + body * 1.2) j++;
            if (j - i >= 2)
                for (int k = i; k < j; k++) blocks[k].Kind = BlockKind.Bullet;
            i = Math.Max(i, j - 1);
        }

        // Nivel de cada punto según su sangría respecto al primero de su lista.
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Kind != BlockKind.Bullet && blocks[i].Kind != BlockKind.Numbered) continue;
            int j = i;
            while (j < blocks.Count && blocks[j].Kind == blocks[i].Kind) j++;
            // Sangrías distintas de la lista (agrupando las parecidas), de menor a mayor: cada una es un nivel.
            var steps = new List<double>();
            foreach (var x in Enumerable.Range(i, j - i).Where(k => xOf.ContainsKey(blocks[k])).Select(k => xOf[blocks[k]]).OrderBy(x => x))
                if (steps.Count == 0 || x > steps[steps.Count - 1] + body * 0.8) steps.Add(x);
            for (int k = i; k < j; k++)
            {
                double x = xOf.ContainsKey(blocks[k]) ? xOf[blocks[k]] : (steps.Count > 0 ? steps[0] : 0);
                blocks[k].Level = Math.Max(0, Math.Min(4, steps.Count(sx => sx <= x + body * 0.8) - 1));
            }
            i = j - 1;
        }
        return blocks;
    }

    static int HeadingLevel(LayoutLine line, double body, List<double> headingSizes, int boldLevel, Tolerances tol)
    {
        string t = line.Text;
        if (t.Length == 0 || t.Length > 150 || !t.Any(char.IsLetter)) return 0;
        if (line.Size >= body * tol.Heading)
        {
            int idx = headingSizes.FindIndex(h => line.Size >= h * tol.SameLevel);
            if (idx < 0) idx = headingSizes.Count - 1;
            return Math.Min(4, idx + 1);
        }
        // Negrita, línea corta y sin punto final: título de nivel inferior.
        if (line.AllBold && t.Length <= 100 && !t.EndsWith(".") && !t.EndsWith(",") && line.Segments.Count == 1 && Math.Abs(line.Size - body) < body * 0.15)
            return boldLevel;
        return 0;
    }

    // Una tabla: varias líneas seguidas con dos o más columnas alineadas.
    static Block TableAt(List<LayoutLine> lines, int start, double body, double spacing, out int end)
    {
        end = start;
        var first = lines[start];
        if (first.Segments.Count < 2 || IsListSplit(first)) return null;

        var rows = new List<LayoutLine> { first };
        int multi = 1;
        for (int j = start + 1; j < lines.Count; j++)
        {
            var l = lines[j];
            double gap = l.Baseline - rows[rows.Count - 1].Baseline;
            if (gap > spacing * 2.6 || gap < 0) break;
            if (l.Size > body * 1.15) break;
            if (l.Segments.Count >= 2 && !IsListSplit(l)) { rows.Add(l); multi++; continue; }
            // Una línea de una sola parte puede ser la continuación de una celda: solo si le sigue otra fila.
            if (j + 1 < lines.Count && lines[j + 1].Segments.Count >= 2 && lines[j + 1].Baseline - l.Baseline <= spacing * 2.6) { rows.Add(l); continue; }
            break;
        }
        if (multi < 2) return null;

        // Columnas: se juntan los intervalos horizontales de las partes que se solapan.
        var intervals = rows.Where(r => r.Segments.Count >= 2).SelectMany(r => r.Segments).Select(s => new[] { s.X0, s.X1 }).OrderBy(a => a[0]).ToList();
        var cols = new List<double[]>();
        foreach (var iv in intervals)
        {
            var last = cols.Count == 0 ? null : cols[cols.Count - 1];
            if (last != null && iv[0] <= last[1] + body * 0.3) last[1] = Math.Max(last[1], iv[1]);
            else cols.Add(new[] { iv[0], iv[1] });
        }
        if (cols.Count < 2) return null;

        Func<LayoutSegment, int> colOf = s =>
        {
            double c = (s.X0 + s.X1) / 2;
            for (int k = 0; k < cols.Count; k++) if (c >= cols[k][0] - body * 0.5 && c <= cols[k][1] + body * 0.5) return k;
            int best = 0;
            for (int k = 1; k < cols.Count; k++) if (Math.Abs(cols[k][0] - s.X0) < Math.Abs(cols[best][0] - s.X0)) best = k;
            return best;
        };

        // Separación típica entre filas, para distinguir filas nuevas de celdas de varias líneas.
        var gaps = new List<double>();
        for (int k = 1; k < rows.Count; k++) if (rows[k].Segments.Count >= 2) gaps.Add(rows[k].Baseline - rows[k - 1].Baseline);
        double rowGap = gaps.Count > 0 ? Median(gaps) : spacing;

        var table = new List<List<List<Inline>>>();
        LayoutLine prevRow = null;
        foreach (var r in rows)
        {
            var cells = Enumerable.Range(0, cols.Count).Select(_ => new List<Inline>()).ToList();
            foreach (var s in r.Segments) JoinInlines(cells[colOf(s)], SegmentInlines(s));
            int filled = cells.Count(c => TextWriterPlain.Text(c).Trim().Length > 0);
            bool continuation = prevRow != null && table.Count > 0 && (filled < cols.Count / 2.0 || filled == 1) &&
                                r.Baseline - prevRow.Baseline < rowGap * 0.8;
            if (continuation)
            {
                var target = table[table.Count - 1];
                for (int k = 0; k < cols.Count; k++) if (cells[k].Count > 0) JoinInlines(target[k], cells[k]);
            }
            else table.Add(cells);
            prevRow = r;
        }

        // La cabecera ya se ve como cabecera: si toda ella es negrita, no se marca.
        if (table.Count > 0 && table[0].All(c => c.All(x => x.Bold || x.Text.Trim().Length == 0)))
            foreach (var c in table[0]) foreach (var x in c) x.Bold = false;

        // Prosa en columnas (dos columnas de texto, no una tabla): celdas demasiado largas.
        double avg = table.SelectMany(row => row).Select(c => (double)TextWriterPlain.Text(c).Length).DefaultIfEmpty(0).Average();
        if (avg > 60) return null;
        if (table.Count < 2) return null;

        end = start + rows.Count;
        return new Block { Kind = BlockKind.Table, Rows = table };
    }

    // "•   texto" o "1.   texto": la viñeta separada del texto no hace una tabla.
    static bool IsListSplit(LayoutLine l)
    {
        if (l.Segments.Count != 2) return false;
        string a = l.Segments[0].Text;
        return BulletRx.IsMatch(a) && a.Length <= 2 || Regex.IsMatch(a, @"^\(?(\d{1,3}|[a-z])[.)]$");
    }

    // --- Texto con formato --------------------------------------------------------------------

    static List<Inline> Inlines(LayoutLine line, bool plain = false, int skipChars = 0)
    {
        var result = new List<Inline>();
        foreach (var seg in line.Segments)
        {
            if (result.Count > 0) result.Add(new Inline { Text = " " });
            foreach (var r in seg.Runs) result.Add(new Inline { Text = r.Text, Bold = !plain && r.Bold, Italic = !plain && r.Italic, Code = !plain && r.Mono });
        }
        // Quitar el marcador de lista del principio.
        while (skipChars > 0 && result.Count > 0)
        {
            var f = result[0];
            string t = f.Text.TrimStart();
            if (t.Length <= skipChars) { skipChars -= t.Length; result.RemoveAt(0); }
            else { f.Text = t.Substring(skipChars); skipChars = 0; }
        }
        Trim(result);
        // Si toda la línea es negrita (o cursiva) en un título, no se marca.
        return Compact(result);
    }

    static List<Inline> SegmentInlines(LayoutSegment s)
    {
        var result = s.Runs.Select(r => new Inline { Text = r.Text, Bold = r.Bold, Italic = r.Italic, Code = r.Mono }).ToList();
        Trim(result);
        return Compact(result);
    }

    static void Trim(List<Inline> list)
    {
        while (list.Count > 0 && list[0].Text.Trim().Length == 0) list.RemoveAt(0);
        while (list.Count > 0 && list[list.Count - 1].Text.Trim().Length == 0) list.RemoveAt(list.Count - 1);
        if (list.Count > 0) { list[0].Text = list[0].Text.TrimStart(); list[list.Count - 1].Text = list[list.Count - 1].Text.TrimEnd(); }
    }

    // Une trozos seguidos con el mismo formato y colapsa los espacios repetidos.
    static List<Inline> Compact(List<Inline> list)
    {
        var result = new List<Inline>();
        foreach (var i in list)
        {
            i.Text = Regex.Replace(i.Text, @"\s+", " ");
            var last = result.Count == 0 ? null : result[result.Count - 1];
            if (last != null && (last.SameFormat(i) || i.Text.Trim().Length == 0)) last.Text += i.Text;
            else result.Add(i);
        }
        foreach (var i in result) i.Text = i.Text.Replace("  ", " ");
        return result;
    }

    // Añade `more` a `target` como continuación de línea (con espacio, o sin el guion de partir palabras).
    static void JoinInlines(List<Inline> target, List<Inline> more)
    {
        if (more.Count == 0) return;
        if (target.Count == 0) { target.AddRange(more); return; }
        var last = target[target.Count - 1];
        string lt = last.Text;
        string nt = more[0].Text;
        if (lt.EndsWith("-") && lt.Length > 1 && char.IsLetter(lt[lt.Length - 2]) && nt.Length > 0 && char.IsLower(nt[0]))
            last.Text = lt.Substring(0, lt.Length - 1);
        else if (!lt.EndsWith(" ")) target.Add(new Inline { Text = " " });
        target.AddRange(more);
        var compact = Compact(target.ToList());
        target.Clear();
        target.AddRange(compact);
    }

    // --- Utilidades ---------------------------------------------------------------------------

    static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    // Valor más frecuente (redondeado a medio punto), ponderado.
    static double Mode(IEnumerable<double> values, IEnumerable<double> weights)
    {
        var acc = new Dictionary<double, double>();
        foreach (var p in values.Zip(weights, (v, w) => new { v = Math.Round(v * 2) / 2, w }))
            acc[p.v] = (acc.ContainsKey(p.v) ? acc[p.v] : 0) + p.w;
        return acc.Count == 0 ? 12 : acc.OrderByDescending(k => k.Value).First().Key;
    }
}
