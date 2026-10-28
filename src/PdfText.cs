// Texto de un PDF leído directamente del archivo (sin OCR): cada letra con su posición, su tamaño y si es
// negrita o cursiva. Con eso, Layout reconstruye títulos, párrafos, listas y tablas.
//
// Es un lector mínimo pero completo para extraer texto: objetos (también dentro de "object streams"),
// filtros de compresión habituales, fuentes simples y compuestas (Type0) con su tabla ToUnicode o su
// codificación, y el intérprete de los operadores de texto del contenido de cada página.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

class PdfRef { public int Num, Gen; }
class PdfName { public string Name; public override string ToString() { return Name; } }
class PdfOp { public string Op; }
class PdfStream { public Dictionary<string, object> Dict; public byte[] Raw; }

// Una letra (o grupo de letras de un mismo código) ya situada en la página, con y hacia abajo.
class PdfGlyph
{
    public double X, Y, Width, Size;   // Y = línea base
    public string Text;
    public bool Bold, Italic, Mono;
}

class PdfTextPage
{
    public double Width, Height;
    public List<PdfGlyph> Glyphs = new List<PdfGlyph>();
}

class PdfEncryptedException : Exception
{
    public PdfEncryptedException() : base("El PDF está cifrado.") { }
}

// ---------------------------------------------------------------------------------------------
// Lectura de objetos
// ---------------------------------------------------------------------------------------------

class PdfLexer
{
    public static readonly object Eof = new object();
    readonly byte[] d;
    public int Pos;
    readonly int end;

    public PdfLexer(byte[] data, int start = 0, int length = -1)
    {
        d = data;
        Pos = start;
        end = length < 0 ? data.Length : Math.Min(data.Length, start + length);
    }

    public static bool IsWhite(byte b) { return b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32; }
    public static bool IsDelim(byte b) { return b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%'; }

    public void SkipWhite()
    {
        while (Pos < end)
        {
            byte b = d[Pos];
            if (IsWhite(b)) Pos++;
            else if (b == '%') { while (Pos < end && d[Pos] != 10 && d[Pos] != 13) Pos++; }
            else break;
        }
    }

    public bool AtEnd { get { SkipWhite(); return Pos >= end; } }

    public object Read()
    {
        SkipWhite();
        if (Pos >= end) return Eof;
        byte b = d[Pos];
        if (b == '/') return ReadName();
        if (b == '(') return ReadLiteral();
        if (b == '<')
        {
            if (Pos + 1 < end && d[Pos + 1] == '<') return ReadDict();
            return ReadHex();
        }
        if (b == '[')
        {
            Pos++;
            var list = new List<object>();
            while (true)
            {
                SkipWhite();
                if (Pos >= end) break;
                if (d[Pos] == ']') { Pos++; break; }
                var o = Read();
                if (o == Eof) break;
                list.Add(o);
            }
            return list;
        }
        if (b == ']' || b == '>' || b == ')' || b == '{' || b == '}') { Pos++; return new PdfOp { Op = ((char)b).ToString() }; }
        if (b == '+' || b == '-' || b == '.' || (b >= '0' && b <= '9')) return ReadNumberOrRef();
        // Palabra clave u operador.
        int start = Pos;
        while (Pos < end && !IsWhite(d[Pos]) && !IsDelim(d[Pos])) Pos++;
        if (Pos == start) { Pos++; return new PdfOp { Op = "?" }; }
        string word = Encoding.ASCII.GetString(d, start, Pos - start);
        if (word == "true") return true;
        if (word == "false") return false;
        if (word == "null") return null;
        return new PdfOp { Op = word };
    }

    PdfName ReadName()
    {
        Pos++;
        var sb = new StringBuilder();
        while (Pos < end && !IsWhite(d[Pos]) && !IsDelim(d[Pos]))
        {
            if (d[Pos] == '#' && Pos + 2 < end && IsHex(d[Pos + 1]) && IsHex(d[Pos + 2]))
            {
                sb.Append((char)(HexVal(d[Pos + 1]) * 16 + HexVal(d[Pos + 2])));
                Pos += 3;
            }
            else sb.Append((char)d[Pos++]);
        }
        return new PdfName { Name = sb.ToString() };
    }

    static bool IsHex(byte b) { return (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F'); }
    static int HexVal(byte b) { return b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10; }

    byte[] ReadLiteral()
    {
        Pos++;
        var o = new List<byte>();
        int depth = 1;
        while (Pos < end)
        {
            byte b = d[Pos++];
            if (b == '\\' && Pos < end)
            {
                byte e = d[Pos++];
                switch (e)
                {
                    case (byte)'n': o.Add(10); break;
                    case (byte)'r': o.Add(13); break;
                    case (byte)'t': o.Add(9); break;
                    case (byte)'b': o.Add(8); break;
                    case (byte)'f': o.Add(12); break;
                    case 13: if (Pos < end && d[Pos] == 10) Pos++; break;   // continuación de línea
                    case 10: break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && Pos < end && d[Pos] >= '0' && d[Pos] <= '7'; k++) v = v * 8 + (d[Pos++] - '0');
                            o.Add((byte)v);
                        }
                        else o.Add(e);
                        break;
                }
            }
            else if (b == '(') { depth++; o.Add(b); }
            else if (b == ')') { if (--depth == 0) break; o.Add(b); }
            else o.Add(b);
        }
        return o.ToArray();
    }

    byte[] ReadHex()
    {
        Pos++;
        var o = new List<byte>();
        int hi = -1;
        while (Pos < end && d[Pos] != '>')
        {
            byte b = d[Pos++];
            if (!IsHex(b)) continue;
            if (hi < 0) hi = HexVal(b);
            else { o.Add((byte)(hi * 16 + HexVal(b))); hi = -1; }
        }
        Pos++;
        if (hi >= 0) o.Add((byte)(hi * 16));
        return o.ToArray();
    }

    Dictionary<string, object> ReadDict()
    {
        Pos += 2;
        var dict = new Dictionary<string, object>();
        while (true)
        {
            SkipWhite();
            if (Pos >= end) break;
            if (d[Pos] == '>' && Pos + 1 < end && d[Pos + 1] == '>') { Pos += 2; break; }
            var key = Read();
            if (key == Eof) break;
            var name = key as PdfName;
            if (name == null) continue;
            SkipWhite();
            if (Pos < end && d[Pos] == '>' && Pos + 1 < end && d[Pos + 1] == '>') { dict[name.Name] = null; Pos += 2; break; }
            dict[name.Name] = Read();
        }
        return dict;
    }

    object ReadNumberOrRef()
    {
        double first = ReadNumber();
        // ¿"num gen R"?
        if (first >= 0 && first == Math.Floor(first))
        {
            int save = Pos;
            SkipWhite();
            if (Pos < end && d[Pos] >= '0' && d[Pos] <= '9')
            {
                int genStart = Pos;
                while (Pos < end && d[Pos] >= '0' && d[Pos] <= '9') Pos++;
                int gen = int.Parse(Encoding.ASCII.GetString(d, genStart, Pos - genStart), CultureInfo.InvariantCulture);
                SkipWhite();
                if (Pos < end && d[Pos] == 'R' && (Pos + 1 >= end || IsWhite(d[Pos + 1]) || IsDelim(d[Pos + 1])))
                {
                    Pos++;
                    return new PdfRef { Num = (int)first, Gen = gen };
                }
            }
            Pos = save;
        }
        return first;
    }

    double ReadNumber()
    {
        int start = Pos;
        if (d[Pos] == '+' || d[Pos] == '-') Pos++;
        while (Pos < end && ((d[Pos] >= '0' && d[Pos] <= '9') || d[Pos] == '.')) Pos++;
        // Algunos generadores escriben "--5" o "-.5-": se toma lo que se pueda.
        string s = Encoding.ASCII.GetString(d, start, Pos - start).Replace("--", "-");
        double v;
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        return v;
    }

    // Imagen dentro del contenido (BI ... ID datos EI): se salta.
    public void SkipInlineImage()
    {
        // Diccionario hasta ID.
        while (true)
        {
            var o = Read();
            if (o == Eof) return;
            var op = o as PdfOp;
            if (op != null && op.Op == "ID") break;
        }
        Pos++;
        while (Pos + 2 < end)
        {
            if (d[Pos] == 'E' && d[Pos + 1] == 'I' && IsWhite(d[Pos - 1]) && (Pos + 2 >= end || IsWhite(d[Pos + 2]))) { Pos += 2; return; }
            Pos++;
        }
        Pos = end;
    }
}

class PdfFile
{
    readonly byte[] data;
    readonly Dictionary<int, object> objects = new Dictionary<int, object>();
    int catalog = -1;

    public PdfFile(byte[] bytes)
    {
        data = bytes;
        string latin = Encoding.GetEncoding(28591).GetString(bytes);
        if (latin.Contains("/Encrypt")) throw new PdfEncryptedException();
        ScanObjects(latin);
        ExpandObjectStreams();
        foreach (var kv in objects)
        {
            var dict = AsDict(kv.Value);
            if (dict != null && NameOf(dict, "Type") == "Catalog") catalog = kv.Key;
        }
    }

    // Se recorre el archivo buscando "n g obj": no depende de que la tabla xref esté bien (muchas no lo están).
    void ScanObjects(string latin)
    {
        var rx = new Regex(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b");
        int skipUntil = 0;
        foreach (Match m in rx.Matches(latin))
        {
            if (m.Index < skipUntil) continue;
            int num = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var lex = new PdfLexer(data, m.Index + m.Length);
            object value;
            try { value = lex.Read(); }
            catch { continue; }
            int after = lex.Pos;
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                lex.SkipWhite();
                if (lex.Pos + 6 <= data.Length && latin.Substring(lex.Pos, 6) == "stream")
                {
                    int start = lex.Pos + 6;
                    if (start < data.Length && data[start] == 13) start++;
                    if (start < data.Length && data[start] == 10) start++;
                    int length = -1;
                    object lenObj;
                    if (dict.TryGetValue("Length", out lenObj) && lenObj is double)
                    {
                        int l = (int)(double)lenObj;
                        int check = start + l;
                        if (l >= 0 && check <= data.Length)
                        {
                            int k = check;
                            while (k < data.Length && PdfLexer.IsWhite(data[k])) k++;
                            if (k + 9 <= data.Length && latin.Substring(k, 9) == "endstream") length = l;
                        }
                    }
                    int endIdx;
                    if (length < 0)
                    {
                        endIdx = latin.IndexOf("endstream", start, StringComparison.Ordinal);
                        if (endIdx < 0) endIdx = data.Length;
                        length = endIdx - start;
                        while (length > 0 && (data[start + length - 1] == 10 || data[start + length - 1] == 13)) length--;
                    }
                    else endIdx = start + length;
                    var raw = new byte[Math.Max(0, length)];
                    Buffer.BlockCopy(data, start, raw, 0, raw.Length);
                    value = new PdfStream { Dict = dict, Raw = raw };
                    after = endIdx + 9;
                }
            }
            objects[num] = value;
            skipUntil = after;
        }
    }

    void ExpandObjectStreams()
    {
        foreach (var s in objects.Values.OfType<PdfStream>().ToList())
        {
            if (NameOf(s.Dict, "Type") != "ObjStm") continue;
            byte[] body = Decode(s);
            if (body == null) continue;
            int n = (int)Num(s.Dict, "N", 0), first = (int)Num(s.Dict, "First", 0);
            var lex = new PdfLexer(body);
            var pairs = new List<int[]>();
            for (int i = 0; i < n; i++)
            {
                var a = lex.Read() as double?; var b = lex.Read() as double?;
                if (a == null || b == null) break;
                pairs.Add(new[] { (int)a.Value, (int)b.Value });
            }
            foreach (var p in pairs)
            {
                if (objects.ContainsKey(p[0])) continue;
                try { objects[p[0]] = new PdfLexer(body, first + p[1]).Read(); }
                catch { }
            }
        }
    }

    public object Resolve(object o)
    {
        for (int guard = 0; guard < 32; guard++)
        {
            var r = o as PdfRef;
            if (r == null) return o;
            object v;
            if (!objects.TryGetValue(r.Num, out v)) return null;
            o = v;
        }
        return null;
    }

    public Dictionary<string, object> AsDict(object o)
    {
        o = Resolve(o);
        var s = o as PdfStream;
        return s != null ? s.Dict : o as Dictionary<string, object>;
    }

    public List<object> AsArray(object o) { return Resolve(o) as List<object>; }

    public object Get(Dictionary<string, object> dict, string key)
    {
        object v;
        return dict != null && dict.TryGetValue(key, out v) ? Resolve(v) : null;
    }

    public double Num(Dictionary<string, object> dict, string key, double fallback)
    {
        var v = Get(dict, key);
        return v is double ? (double)v : fallback;
    }

    public string NameOf(Dictionary<string, object> dict, string key)
    {
        var n = Get(dict, key) as PdfName;
        return n == null ? null : n.Name;
    }

    // --- Filtros -----------------------------------------------------------------------------

    public byte[] Decode(PdfStream s)
    {
        byte[] bytes = s.Raw;
        var filters = new List<string>();
        var parms = new List<Dictionary<string, object>>();
        var f = Get(s.Dict, "Filter");
        if (f is PdfName) filters.Add(((PdfName)f).Name);
        else if (f is List<object>) foreach (var x in (List<object>)f) { var n = Resolve(x) as PdfName; if (n != null) filters.Add(n.Name); }
        var p = Get(s.Dict, "DecodeParms");
        if (p is List<object>) foreach (var x in (List<object>)p) parms.Add(AsDict(x));
        else parms.Add(AsDict(p));

        for (int i = 0; i < filters.Count; i++)
        {
            var parm = i < parms.Count ? parms[i] : null;
            switch (filters[i])
            {
                case "FlateDecode": case "Fl": bytes = Predict(Inflate(bytes), parm); break;
                case "LZWDecode": case "LZW": bytes = Predict(Lzw(bytes, parm == null || Num(parm, "EarlyChange", 1) != 0), parm); break;
                case "ASCIIHexDecode": case "AHx": bytes = AsciiHex(bytes); break;
                case "ASCII85Decode": case "A85": bytes = Ascii85(bytes); break;
                case "RunLengthDecode": case "RL": bytes = RunLength(bytes); break;
                default: return null;   // imágenes (DCT, JPX, CCITT...): no llevan texto
            }
            if (bytes == null) return null;
        }
        return bytes;
    }

    static byte[] Inflate(byte[] input)
    {
        int skip = input.Length > 2 && (input[0] & 0x0F) == 8 ? 2 : 0;
        using (var src = new MemoryStream(input, skip, input.Length - skip))
        using (var z = new DeflateStream(src, CompressionMode.Decompress))
        using (var dst = new MemoryStream())
        {
            var buf = new byte[16384];
            try
            {
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
            }
            catch { }   // datos dañados al final: se usa lo que se pudo leer
            return dst.ToArray();
        }
    }

    byte[] Predict(byte[] input, Dictionary<string, object> parm)
    {
        if (input == null || parm == null) return input;
        int predictor = (int)Num(parm, "Predictor", 1);
        if (predictor < 10) return input;
        int colors = (int)Num(parm, "Colors", 1), bpc = (int)Num(parm, "BitsPerComponent", 8), columns = (int)Num(parm, "Columns", 1);
        int bpp = Math.Max(1, colors * bpc / 8), row = (columns * colors * bpc + 7) / 8;
        var output = new MemoryStream();
        var prev = new byte[row];
        for (int pos = 0; pos + 1 <= input.Length; pos += row + 1)
        {
            int type = input[pos];
            var cur = new byte[row];
            int len = Math.Min(row, input.Length - pos - 1);
            Buffer.BlockCopy(input, pos + 1, cur, 0, Math.Max(0, len));
            for (int i = 0; i < row; i++)
            {
                int left = i >= bpp ? cur[i - bpp] : 0, up = prev[i], upLeft = i >= bpp ? prev[i - bpp] : 0;
                switch (type)
                {
                    case 1: cur[i] = (byte)(cur[i] + left); break;
                    case 2: cur[i] = (byte)(cur[i] + up); break;
                    case 3: cur[i] = (byte)(cur[i] + (left + up) / 2); break;
                    case 4:
                        int pp = left + up - upLeft, pa = Math.Abs(pp - left), pb = Math.Abs(pp - up), pc = Math.Abs(pp - upLeft);
                        cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft));
                        break;
                }
            }
            output.Write(cur, 0, row);
            prev = cur;
        }
        return output.ToArray();
    }

    static byte[] AsciiHex(byte[] input)
    {
        var o = new List<byte>();
        int hi = -1;
        foreach (byte b in input)
        {
            if (b == '>') break;
            int v = b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            if (v < 0) continue;
            if (hi < 0) hi = v; else { o.Add((byte)(hi * 16 + v)); hi = -1; }
        }
        if (hi >= 0) o.Add((byte)(hi * 16));
        return o.ToArray();
    }

    static byte[] Ascii85(byte[] input)
    {
        var o = new List<byte>();
        var group = new int[5];
        int n = 0;
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == '~') break;
            if (b == 'z' && n == 0) { o.AddRange(new byte[4]); continue; }
            if (b < '!' || b > 'u') continue;
            group[n++] = b - '!';
            if (n == 5)
            {
                long v = 0;
                for (int k = 0; k < 5; k++) v = v * 85 + group[k];
                o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
                n = 0;
            }
        }
        if (n > 1)
        {
            for (int k = n; k < 5; k++) group[k] = 84;
            long v = 0;
            for (int k = 0; k < 5; k++) v = v * 85 + group[k];
            for (int k = 0; k < n - 1; k++) o.Add((byte)(v >> (24 - 8 * k)));
        }
        return o.ToArray();
    }

    static byte[] RunLength(byte[] input)
    {
        var o = new List<byte>();
        for (int i = 0; i < input.Length; )
        {
            int len = input[i++];
            if (len == 128) break;
            if (len < 128) { for (int k = 0; k <= len && i < input.Length; k++) o.Add(input[i++]); }
            else if (i < input.Length) { byte b = input[i++]; for (int k = 0; k < 257 - len; k++) o.Add(b); }
        }
        return o.ToArray();
    }

    static byte[] Lzw(byte[] input, bool early)
    {
        var o = new List<byte>();
        var table = new List<byte[]>();
        Action reset = () => { table.Clear(); for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i }); table.Add(null); table.Add(null); };
        reset();
        int bits = 9, bitPos = 0;
        byte[] prev = null;
        while (true)
        {
            if (bitPos + bits > input.Length * 8) break;
            int code = 0;
            for (int k = 0; k < bits; k++, bitPos++) code = (code << 1) | ((input[bitPos >> 3] >> (7 - (bitPos & 7))) & 1);
            if (code == 256) { reset(); bits = 9; prev = null; continue; }
            if (code == 257) break;
            byte[] entry;
            if (code < table.Count && table[code] != null) entry = table[code];
            else if (prev != null) { entry = new byte[prev.Length + 1]; Buffer.BlockCopy(prev, 0, entry, 0, prev.Length); entry[prev.Length] = prev[0]; }
            else break;
            o.AddRange(entry);
            if (prev != null)
            {
                var add = new byte[prev.Length + 1];
                Buffer.BlockCopy(prev, 0, add, 0, prev.Length);
                add[prev.Length] = entry[0];
                table.Add(add);
            }
            prev = entry;
            int limit = table.Count + (early ? 1 : 0);
            if (limit >= 512 && bits < 10) bits = 10; else if (limit >= 1024 && bits < 11) bits = 11; else if (limit >= 2048 && bits < 12) bits = 12;
        }
        return o.ToArray();
    }

    // --- Páginas ----------------------------------------------------------------------------

    public class Page { public Dictionary<string, object> Dict, Resources; public double[] Box; }

    public List<Page> Pages()
    {
        var pages = new List<Page>();
        var visited = new HashSet<Dictionary<string, object>>();
        var root = catalog >= 0 ? AsDict(Get(AsDict(objects[catalog]), "Pages")) : null;
        if (root != null) Walk(root, null, null, pages, visited);
        if (pages.Count == 0)
        {
            // Sin catálogo legible: las páginas en el orden de sus objetos.
            foreach (var kv in objects.OrderBy(k => k.Key))
            {
                var dict = AsDict(kv.Value);
                if (dict != null && NameOf(dict, "Type") == "Page")
                    pages.Add(new Page { Dict = dict, Resources = AsDict(Get(dict, "Resources")), Box = Box(Get(dict, "MediaBox")) });
            }
        }
        return pages;
    }

    void Walk(Dictionary<string, object> node, Dictionary<string, object> res, double[] box, List<Page> pages, HashSet<Dictionary<string, object>> visited)
    {
        if (node == null || !visited.Add(node)) return;
        var myRes = AsDict(Get(node, "Resources")) ?? res;
        var myBox = Box(Get(node, "MediaBox")) ?? box;
        var kids = AsArray(Get(node, "Kids"));
        if (kids != null && NameOf(node, "Type") != "Page")
        {
            foreach (var k in kids) Walk(AsDict(k), myRes, myBox, pages, visited);
        }
        else pages.Add(new Page { Dict = node, Resources = myRes, Box = myBox });
    }

    double[] Box(object o)
    {
        var a = AsArray(o);
        if (a == null || a.Count < 4) return null;
        return a.Take(4).Select(x => { var v = Resolve(x); return v is double ? (double)v : 0; }).ToArray();
    }

    public byte[] Contents(Dictionary<string, object> page)
    {
        var c = Get(page, "Contents");
        var parts = new List<PdfStream>();
        if (c is PdfStream) parts.Add((PdfStream)c);
        else if (c is List<object>) foreach (var x in (List<object>)c) { var s = Resolve(x) as PdfStream; if (s != null) parts.Add(s); }
        var all = new MemoryStream();
        foreach (var s in parts)
        {
            var b = Decode(s);
            if (b == null) continue;
            all.Write(b, 0, b.Length);
            all.WriteByte(10);
        }
        return all.ToArray();
    }
}

// ---------------------------------------------------------------------------------------------
// Fuentes
// ---------------------------------------------------------------------------------------------

class PdfFont
{
    public bool Bold, Italic, Mono, Symbolic;
    readonly bool twoByte;
    readonly List<int[]> codespace = new List<int[]>();       // {bytes, low, high}
    readonly Dictionary<int, string> toUnicode = new Dictionary<int, string>();
    readonly string[] simple = new string[256];
    readonly Dictionary<int, double> widths = new Dictionary<int, double>();
    readonly double defaultWidth = 500;
    readonly double widthScale = 1;                            // Type3: las anchuras van en el espacio de sus glifos
    readonly bool ucs2;                                        // Type0 con CMap Uni*-UCS2: el código ya es Unicode
    public int Unmapped;

    public PdfFont(PdfFile pdf, Dictionary<string, object> dict)
    {
        string subtype = pdf.NameOf(dict, "Subtype") ?? "";
        string baseFont = pdf.NameOf(dict, "BaseFont") ?? "";
        int plus = baseFont.IndexOf('+');
        if (plus == 6) baseFont = baseFont.Substring(7);
        var font = dict;
        if (subtype == "Type0")
        {
            twoByte = true;
            var desc = pdf.AsArray(pdf.Get(dict, "DescendantFonts"));
            if (desc != null && desc.Count > 0) font = pdf.AsDict(desc[0]) ?? dict;
            defaultWidth = pdf.Num(font, "DW", 1000);
            var w = pdf.AsArray(pdf.Get(font, "W"));
            if (w != null) ReadCidWidths(pdf, w);
            var enc = pdf.Get(dict, "Encoding");
            if (enc is PdfStream)
            {
                var b = pdf.Decode((PdfStream)enc);
                if (b != null) ReadCMap(b, false);
            }
            else if (enc is PdfName && ((PdfName)enc).Name.Contains("UCS2")) ucs2 = true;
        }
        else
        {
            BuildSimpleEncoding(pdf, dict, baseFont);
            var w = pdf.AsArray(pdf.Get(dict, "Widths"));
            int firstChar = (int)pdf.Num(dict, "FirstChar", 0);
            var fd = pdf.AsDict(pdf.Get(dict, "FontDescriptor"));
            defaultWidth = pdf.Num(fd, "MissingWidth", baseFont.StartsWith("Courier") ? 600 : 500);
            if (w != null)
                for (int i = 0; i < w.Count; i++) { var v = pdf.Resolve(w[i]); if (v is double) widths[firstChar + i] = (double)v; }
            if (subtype == "Type3")
            {
                var m = pdf.AsArray(pdf.Get(dict, "FontMatrix"));
                if (m != null && m.Count > 0 && pdf.Resolve(m[0]) is double) widthScale = (double)pdf.Resolve(m[0]) * 1000;
            }
        }

        var tu = pdf.Get(dict, "ToUnicode") as PdfStream;
        if (tu != null)
        {
            var b = pdf.Decode(tu);
            if (b != null) ReadCMap(b, true);
        }

        // Estilo: por el nombre de la fuente y por su descriptor.
        var descriptor = pdf.AsDict(pdf.Get(font, "FontDescriptor")) ?? pdf.AsDict(pdf.Get(dict, "FontDescriptor"));
        string lower = baseFont.ToLowerInvariant();
        int flags = (int)pdf.Num(descriptor, "Flags", 0);
        double weight = pdf.Num(descriptor, "FontWeight", 400);
        Bold = Regex.IsMatch(lower, @"bold|black|heavy|semibold|demi|extrabold|,bd|-bd") || weight >= 600 || (flags & (1 << 18)) != 0;
        Italic = Regex.IsMatch(lower, @"italic|oblique|-it$|,it$") || (flags & 64) != 0 || Math.Abs(pdf.Num(descriptor, "ItalicAngle", 0)) > 3;
        Mono = Regex.IsMatch(lower, @"courier|consol|mono|menlo|code|typewriter") || (flags & 1) != 0;
        Symbolic = Regex.IsMatch(lower, @"symbol|dingbat|wingding|webding");

        // Hay generadores (la impresora PDF de Windows) que renombran las fuentes ("CIDFont+F2"): el estilo se lee
        // entonces de la propia fuente TrueType incrustada.
        var file = pdf.Get(descriptor, "FontFile2") as PdfStream;
        if (file != null)
        {
            try
            {
                var ttf = pdf.Decode(file);
                bool b, it, mono;
                if (ttf != null && TrueTypeStyle(ttf, out b, out it, out mono)) { Bold |= b; Italic |= it; Mono |= mono; }
            }
            catch { }
        }
    }

    // Negrita y cursiva según las tablas 'OS/2' (peso y fsSelection) y 'head' (macStyle) de una fuente TrueType;
    // ancho fijo según la tabla 'post' (isFixedPitch).
    static bool TrueTypeStyle(byte[] f, out bool bold, out bool italic, out bool mono)
    {
        bold = italic = mono = false;
        if (f.Length < 12) return false;
        int tables = (f[4] << 8) | f[5];
        bool found = false;
        for (int i = 0; i < tables && 12 + i * 16 + 16 <= f.Length; i++)
        {
            int rec = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(f, rec, 4);
            int off = (f[rec + 8] << 24) | (f[rec + 9] << 16) | (f[rec + 10] << 8) | f[rec + 11];
            if (tag == "OS/2" && off + 64 <= f.Length)
            {
                int weight = (f[off + 4] << 8) | f[off + 5];
                int sel = (f[off + 62] << 8) | f[off + 63];
                bold |= weight >= 600 || (sel & 32) != 0;
                italic |= (sel & 1) != 0;
                found = true;
            }
            else if (tag == "post" && off + 16 <= f.Length)
            {
                mono |= ((f[off + 12] << 24) | (f[off + 13] << 16) | (f[off + 14] << 8) | f[off + 15]) != 0;
            }
            else if (tag == "head" && off + 46 <= f.Length)
            {
                int mac = (f[off + 44] << 8) | f[off + 45];
                bold |= (mac & 1) != 0;
                italic |= (mac & 2) != 0;
                found = true;
            }
        }
        return found;
    }

    void ReadCidWidths(PdfFile pdf, List<object> w)
    {
        for (int i = 0; i < w.Count; )
        {
            var a = pdf.Resolve(w[i]);
            if (!(a is double)) { i++; continue; }
            int first = (int)(double)a;
            var b = i + 1 < w.Count ? pdf.Resolve(w[i + 1]) : null;
            if (b is List<object>)
            {
                var list = (List<object>)b;
                for (int k = 0; k < list.Count; k++) { var v = pdf.Resolve(list[k]); if (v is double) widths[first + k] = (double)v; }
                i += 2;
            }
            else if (b is double && i + 2 < w.Count && pdf.Resolve(w[i + 2]) is double)
            {
                int last = (int)(double)b;
                double v = (double)pdf.Resolve(w[i + 2]);
                for (int c = first; c <= last && c - first < 65536; c++) widths[c] = v;
                i += 3;
            }
            else i++;
        }
    }

    void BuildSimpleEncoding(PdfFile pdf, Dictionary<string, object> dict, string baseFont)
    {
        var win = Encoding.GetEncoding(1252);
        for (int i = 32; i < 256; i++) simple[i] = win.GetString(new[] { (byte)i });
        simple[0x27] = "'";
        var enc = pdf.Get(dict, "Encoding");
        var encDict = pdf.AsDict(enc);
        string encName = enc is PdfName ? ((PdfName)enc).Name : pdf.NameOf(encDict, "BaseEncoding");
        if (encName == "MacRomanEncoding")
        {
            var mac = Encoding.GetEncoding(10000);
            for (int i = 128; i < 256; i++) simple[i] = mac.GetString(new[] { (byte)i });
        }
        else if (encName == null || encName == "StandardEncoding")
        {
            simple[0x27] = "’"; simple[0x60] = "‘";
        }
        var diffs = encDict == null ? null : pdf.AsArray(pdf.Get(encDict, "Differences"));
        if (diffs == null) return;
        int code = 0;
        foreach (var item in diffs)
        {
            var v = pdf.Resolve(item);
            if (v is double) code = (int)(double)v;
            else if (v is PdfName)
            {
                if (code >= 0 && code < 256) simple[code] = GlyphNames.ToUnicode(((PdfName)v).Name);
                code++;
            }
        }
    }

    void ReadCMap(byte[] data, bool unicode)
    {
        var lex = new PdfLexer(data);
        var ops = new List<object>();
        while (true)
        {
            var o = lex.Read();
            if (o == PdfLexer.Eof) break;
            var op = o as PdfOp;
            if (op == null) { ops.Add(o); continue; }
            switch (op.Op)
            {
                case "endcodespacerange":
                    for (int i = 0; i + 1 < ops.Count; i += 2)
                    {
                        var lo = ops[i] as byte[]; var hi = ops[i + 1] as byte[];
                        if (lo != null && hi != null && lo.Length > 0) codespace.Add(new[] { lo.Length, ToInt(lo), ToInt(hi) });
                    }
                    break;
                case "endbfchar":
                    if (!unicode) break;
                    for (int i = 0; i + 1 < ops.Count; i += 2)
                    {
                        var src = ops[i] as byte[]; var dst = ops[i + 1] as byte[];
                        if (src != null && dst != null) toUnicode[ToInt(src)] = Utf16(dst);
                        else if (src != null && ops[i + 1] is PdfName) toUnicode[ToInt(src)] = GlyphNames.ToUnicode(((PdfName)ops[i + 1]).Name);
                    }
                    break;
                case "endbfrange":
                    if (!unicode) break;
                    for (int i = 0; i + 2 < ops.Count; i += 3)
                    {
                        var lo = ops[i] as byte[]; var hi = ops[i + 1] as byte[];
                        if (lo == null || hi == null) continue;
                        int a = ToInt(lo), b = ToInt(hi);
                        if (b - a > 65535 || b < a) continue;
                        if (ops[i + 2] is byte[])
                        {
                            var dst = (byte[])ops[i + 2];
                            string s = Utf16(dst);
                            if (s.Length == 0) continue;
                            for (int c = a; c <= b; c++)
                            {
                                toUnicode[c] = s.Substring(0, s.Length - 1) + (char)(s[s.Length - 1] + (c - a));
                            }
                        }
                        else if (ops[i + 2] is List<object>)
                        {
                            var list = (List<object>)ops[i + 2];
                            for (int c = a; c <= b && c - a < list.Count; c++) { var dst = list[c - a] as byte[]; if (dst != null) toUnicode[c] = Utf16(dst); }
                        }
                    }
                    break;
            }
            ops.Clear();
        }
    }

    static int ToInt(byte[] b) { int v = 0; foreach (byte x in b) v = (v << 8) | x; return v; }

    static string Utf16(byte[] b)
    {
        if (b.Length == 1) return ((char)b[0]).ToString();
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < b.Length; i += 2) sb.Append((char)((b[i] << 8) | b[i + 1]));
        return sb.ToString();
    }

    // Separa la cadena en códigos de carácter según la codificación de la fuente.
    public List<int[]> Codes(byte[] s)   // {código, bytes}
    {
        var result = new List<int[]>();
        for (int i = 0; i < s.Length; )
        {
            int len = twoByte ? 2 : 1;
            if (codespace.Count > 0)
            {
                foreach (var cs in codespace.OrderBy(c => c[0]))
                {
                    if (i + cs[0] > s.Length) continue;
                    int v = 0;
                    for (int k = 0; k < cs[0]; k++) v = (v << 8) | s[i + k];
                    if (v >= cs[1] && v <= cs[2]) { len = cs[0]; break; }
                }
            }
            if (i + len > s.Length) len = s.Length - i;
            int code = 0;
            for (int k = 0; k < len; k++) code = (code << 8) | s[i + k];
            result.Add(new[] { code, len });
            i += len;
        }
        return result;
    }

    public string Text(int code)
    {
        string s;
        if (toUnicode.TryGetValue(code, out s)) return Clean(s);
        if (ucs2) return ((char)code).ToString();
        if (!twoByte && code < 256 && simple[code] != null) return Symbolic ? "•" : simple[code];
        if (Symbolic) return "•";
        Unmapped++;
        return "";
    }

    // Viñetas de fuentes de símbolos y caracteres de uso privado: se ven como viñetas.
    string Clean(string s)
    {
        if (s.Length == 1 && s[0] >= 0xE000 && s[0] <= 0xF8FF) return "•";
        return s.Replace("\0", "");
    }

    public double Width(int code)
    {
        double w;
        return (widths.TryGetValue(code, out w) ? w : defaultWidth) * widthScale / 1000.0;
    }
}

// Nombres de glifo más habituales (los de Latin-1 y Windows-1252) a Unicode.
static class GlyphNames
{
    static Dictionary<string, string> map;

    public static string ToUnicode(string name)
    {
        if (map == null) Build();
        string s;
        if (map.TryGetValue(name, out s)) return s;
        int dot = name.IndexOf('.');
        if (dot > 0) return ToUnicode(name.Substring(0, dot));
        if (name.Contains("_")) return string.Concat(name.Split('_').Select(ToUnicode));
        var m = Regex.Match(name, @"^uni([0-9A-Fa-f]{4})+$");
        if (m.Success)
        {
            var sb = new StringBuilder();
            for (int i = 3; i + 4 <= name.Length; i += 4) sb.Append((char)int.Parse(name.Substring(i, 4), NumberStyles.HexNumber));
            return sb.ToString();
        }
        m = Regex.Match(name, @"^u([0-9A-Fa-f]{4,6})$");
        if (m.Success) return char.ConvertFromUtf32(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber));
        if (name.Length == 1) return name;
        return "";
    }

    static void Build()
    {
        map = new Dictionary<string, string>();
        string ascii = "space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus comma hyphen period slash " +
            "zero one two three four five six seven eight nine colon semicolon less equal greater question at";
        int c = 0x20;
        foreach (var n in ascii.Split(' ')) map[n] = ((char)c++).ToString();
        for (char ch = 'A'; ch <= 'Z'; ch++) map[ch.ToString()] = ch.ToString();
        for (char ch = 'a'; ch <= 'z'; ch++) map[ch.ToString()] = ch.ToString();
        string more = "bracketleft [ backslash \\ bracketright ] asciicircum ^ underscore _ grave ` braceleft { bar | braceright } asciitilde ~";
        var parts = more.Split(' ');
        for (int i = 0; i + 1 < parts.Length; i += 2) map[parts[i]] = parts[i + 1];
        string latin1 = "nbspace exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine guillemotleft logicalnot " +
            "sfthyphen registered macron degree plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior " +
            "ordmasculine guillemotright onequarter onehalf threequarters questiondown Agrave Aacute Acircumflex Atilde Adieresis Aring AE " +
            "Ccedilla Egrave Eacute Ecircumflex Edieresis Igrave Iacute Icircumflex Idieresis Eth Ntilde Ograve Oacute Ocircumflex Otilde " +
            "Odieresis multiply Oslash Ugrave Uacute Ucircumflex Udieresis Yacute Thorn germandbls agrave aacute acircumflex atilde " +
            "adieresis aring ae ccedilla egrave eacute ecircumflex edieresis igrave iacute icircumflex idieresis eth ntilde ograve oacute " +
            "ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis yacute thorn ydieresis";
        c = 0xA0;
        foreach (var n in latin1.Split(' ')) map[n] = ((char)c++).ToString();
        string extra = "Euro 20AC quotesinglbase 201A florin 0192 quotedblbase 201E ellipsis 2026 dagger 2020 daggerdbl 2021 circumflex 02C6 " +
            "perthousand 2030 Scaron 0160 guilsinglleft 2039 OE 0152 Zcaron 017D quoteleft 2018 quoteright 2019 quotedblleft 201C " +
            "quotedblright 201D bullet 2022 endash 2013 emdash 2014 tilde 02DC trademark 2122 scaron 0161 guilsinglright 203A oe 0153 " +
            "zcaron 017E Ydieresis 0178 fi FB01 fl FB02 ff FB00 ffi FB03 ffl FB04 minus 2212 dotlessi 0131 Lslash 0141 lslash 0142 " +
            "fraction 2044 arrowright 2192 arrowleft 2190 checkmark 2713 middot 00B7 space 0020 hyphen 002D";
        parts = extra.Split(' ');
        for (int i = 0; i + 1 < parts.Length; i += 2) map[parts[i]] = ((char)int.Parse(parts[i + 1], NumberStyles.HexNumber)).ToString();
        map["nbspace"] = " ";
        map["sfthyphen"] = "";
    }
}

// ---------------------------------------------------------------------------------------------
// Intérprete del contenido
// ---------------------------------------------------------------------------------------------

static class PdfText
{
    class State
    {
        public double[] Ctm = { 1, 0, 0, 1, 0, 0 };
        public PdfFont Font;
        public double Size = 12, CharSpace, WordSpace, Scale = 1, Leading, Rise;
        public int Render;   // modo de dibujo del texto: 2 = relleno + contorno (negrita "a mano")
        public State Clone() { var s = (State)MemberwiseClone(); s.Ctm = (double[])Ctm.Clone(); return s; }
    }

    // Lee todas las páginas. Devuelve null si el PDF no lleva texto utilizable (escaneado, cifrado, fuentes sin
    // tabla de caracteres): entonces se usa el OCR.
    public static List<PdfTextPage> Read(string path)
    {
        PdfFile pdf;
        try { pdf = new PdfFile(File.ReadAllBytes(path)); }
        catch (PdfEncryptedException) { return null; }
        var pages = new List<PdfTextPage>();
        var fonts = new Dictionary<object, PdfFont>();
        int glyphs = 0, unmapped = 0;
        foreach (var page in pdf.Pages())
        {
            var box = page.Box ?? new double[] { 0, 0, 612, 792 };
            var result = new PdfTextPage { Width = box[2] - box[0], Height = box[3] - box[1] };
            var state = new State();
            state.Ctm[4] = -box[0];
            state.Ctm[5] = -box[1];
            try { Run(pdf, pdf.Contents(page.Dict), page.Resources, state, result, fonts, 0); }
            catch { }
            // Y hacia abajo, como en la pantalla.
            foreach (var g in result.Glyphs) g.Y = result.Height - g.Y;
            glyphs += result.Glyphs.Count(g => g.Text.Trim().Length > 0);
            pages.Add(result);
        }
        foreach (var f in fonts.Values) unmapped += f.Unmapped;
        if (glyphs < 20 || unmapped > glyphs * 0.2) return null;
        return pages;
    }

    static double[] Mul(double[] m1, double[] m2)
    {
        return new[]
        {
            m1[0] * m2[0] + m1[1] * m2[2], m1[0] * m2[1] + m1[1] * m2[3],
            m1[2] * m2[0] + m1[3] * m2[2], m1[2] * m2[1] + m1[3] * m2[3],
            m1[4] * m2[0] + m1[5] * m2[2] + m2[4], m1[4] * m2[1] + m1[5] * m2[3] + m2[5]
        };
    }

    static void Run(PdfFile pdf, byte[] content, Dictionary<string, object> res, State st, PdfTextPage page, Dictionary<object, PdfFont> fonts, int depth)
    {
        if (content == null || depth > 8) return;
        var lex = new PdfLexer(content);
        var operands = new List<object>();
        var stack = new Stack<State>();
        double[] tm = { 1, 0, 0, 1, 0, 0 }, tlm = { 1, 0, 0, 1, 0, 0 };
        var fontRes = pdf.AsDict(pdf.Get(res, "Font"));
        var xobjects = pdf.AsDict(pdf.Get(res, "XObject"));

        while (true)
        {
            var o = lex.Read();
            if (o == PdfLexer.Eof) break;
            var op = o as PdfOp;
            if (op == null) { operands.Add(o); continue; }
            try
            {
                switch (op.Op)
                {
                    case "q": stack.Push(st.Clone()); break;
                    case "Q": if (stack.Count > 0) st = stack.Pop(); break;
                    case "cm": if (operands.Count >= 6) st.Ctm = Mul(Nums(operands, 6), st.Ctm); break;
                    case "BT": tm = new double[] { 1, 0, 0, 1, 0, 0 }; tlm = (double[])tm.Clone(); break;
                    case "Tf":
                        if (operands.Count >= 2)
                        {
                            var name = operands[operands.Count - 2] as PdfName;
                            st.Size = N(operands[operands.Count - 1]);
                            st.Font = name == null ? null : LoadFont(pdf, fontRes, name.Name, fonts);
                        }
                        break;
                    case "Tc": if (operands.Count >= 1) st.CharSpace = N(operands[0]); break;
                    case "Tw": if (operands.Count >= 1) st.WordSpace = N(operands[0]); break;
                    case "Tz": if (operands.Count >= 1) st.Scale = N(operands[0]) / 100; break;
                    case "TL": if (operands.Count >= 1) st.Leading = N(operands[0]); break;
                    case "Ts": if (operands.Count >= 1) st.Rise = N(operands[0]); break;
                    case "Tr": if (operands.Count >= 1) st.Render = (int)N(operands[0]); break;
                    case "Td":
                        if (operands.Count >= 2) { tlm = Mul(new[] { 1, 0, 0, 1, N(operands[0]), N(operands[1]) }, tlm); tm = (double[])tlm.Clone(); }
                        break;
                    case "TD":
                        if (operands.Count >= 2) { st.Leading = -N(operands[1]); tlm = Mul(new[] { 1, 0, 0, 1, N(operands[0]), N(operands[1]) }, tlm); tm = (double[])tlm.Clone(); }
                        break;
                    case "Tm": if (operands.Count >= 6) { tlm = Nums(operands, 6); tm = (double[])tlm.Clone(); } break;
                    case "T*": tlm = Mul(new[] { 1, 0, 0, 1, 0, -st.Leading }, tlm); tm = (double[])tlm.Clone(); break;
                    case "Tj": if (operands.Count >= 1) Show(operands[0] as byte[], st, ref tm, page); break;
                    case "'":
                        tlm = Mul(new[] { 1, 0, 0, 1, 0, -st.Leading }, tlm); tm = (double[])tlm.Clone();
                        if (operands.Count >= 1) Show(operands[operands.Count - 1] as byte[], st, ref tm, page);
                        break;
                    case "\"":
                        if (operands.Count >= 3) { st.WordSpace = N(operands[0]); st.CharSpace = N(operands[1]); }
                        tlm = Mul(new[] { 1, 0, 0, 1, 0, -st.Leading }, tlm); tm = (double[])tlm.Clone();
                        if (operands.Count >= 1) Show(operands[operands.Count - 1] as byte[], st, ref tm, page);
                        break;
                    case "TJ":
                        var arr = operands.Count >= 1 ? operands[0] as List<object> : null;
                        if (arr != null)
                            foreach (var item in arr)
                            {
                                if (item is byte[]) Show((byte[])item, st, ref tm, page);
                                else if (item is double) tm = Mul(new[] { 1, 0, 0, 1, -(double)item / 1000 * st.Size * st.Scale, 0 }, tm);
                            }
                        break;
                    case "Do":
                        var xname = operands.Count >= 1 ? operands[0] as PdfName : null;
                        var xs = xname == null ? null : pdf.Get(xobjects, xname.Name) as PdfStream;
                        if (xs != null && pdf.NameOf(xs.Dict, "Subtype") == "Form")
                        {
                            var saved = st.Clone();
                            var m = pdf.AsArray(pdf.Get(xs.Dict, "Matrix"));
                            if (m != null && m.Count >= 6) st.Ctm = Mul(m.Take(6).Select(x => N(pdf.Resolve(x))).ToArray(), st.Ctm);
                            Run(pdf, pdf.Decode(xs), pdf.AsDict(pdf.Get(xs.Dict, "Resources")) ?? res, st, page, fonts, depth + 1);
                            st = saved;
                        }
                        break;
                    case "BI": lex.SkipInlineImage(); break;
                }
            }
            catch { }
            operands.Clear();
        }
    }

    static PdfFont LoadFont(PdfFile pdf, Dictionary<string, object> fontRes, string name, Dictionary<object, PdfFont> cache)
    {
        object raw;
        if (fontRes == null || !fontRes.TryGetValue(name, out raw)) return null;
        object key = raw is PdfRef ? (object)((PdfRef)raw).Num : raw;
        PdfFont f;
        if (cache.TryGetValue(key, out f)) return f;
        var dict = pdf.AsDict(raw);
        f = dict == null ? null : new PdfFont(pdf, dict);
        cache[key] = f;
        return f;
    }

    static void Show(byte[] s, State st, ref double[] tm, PdfTextPage page)
    {
        if (s == null || st.Font == null) return;
        foreach (var code in st.Font.Codes(s))
        {
            string text = st.Font.Text(code[0]);
            double w0 = st.Font.Width(code[0]);
            double tx = (w0 * st.Size + st.CharSpace + (code[1] == 1 && code[0] == 32 ? st.WordSpace : 0)) * st.Scale;
            var trm = Mul(new[] { st.Size * st.Scale, 0, 0, st.Size, 0, st.Rise }, Mul(tm, st.Ctm));
            var lin = Mul(tm, st.Ctm);
            double sx = Math.Sqrt(lin[0] * lin[0] + lin[1] * lin[1]);
            double size = Math.Sqrt(trm[2] * trm[2] + trm[3] * trm[3]);
            // Solo texto horizontal: el girado (sellos, marcas de agua) no forma parte del contenido.
            if (text.Length > 0 && Math.Abs(trm[1]) < 0.2 * Math.Abs(trm[0]) + 0.01 && size > 0.5)
                page.Glyphs.Add(new PdfGlyph
                {
                    X = trm[4], Y = trm[5], Width = Math.Max(0, w0 * st.Size * st.Scale * sx), Size = size,
                    Text = text, Bold = st.Font.Bold || st.Render == 2, Italic = st.Font.Italic, Mono = st.Font.Mono
                });
            tm = Mul(new[] { 1, 0, 0, 1, tx, 0 }, tm);
        }
    }

    static double N(object o) { return o is double ? (double)o : 0; }

    static double[] Nums(List<object> ops, int count)
    {
        return ops.Skip(ops.Count - count).Take(count).Select(N).ToArray();
    }
}
