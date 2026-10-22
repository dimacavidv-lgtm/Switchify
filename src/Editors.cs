// Ventanas de herramientas de imagen (recortar, fondo, editar, anotar, tapar, metadatos).
// Todo se dibuja a mano con un pequeño kit de controles al estilo Apple: botones píldora,
// control segmentado, deslizadores y muestras de color, en claro u oscuro según Windows.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ---------------------------------------------------------------------------------------------
// Tema y kit de controles
// ---------------------------------------------------------------------------------------------

class EditorTheme
{
    public bool Dark, Glass, LightText;
    public float Scale;
    public Color Window, Canvas, Text, Secondary, Separator, Accent, AccentPressed, Control, ControlPressed, ControlSelected, Track;
    public FontFamily Semibold, Regular;

    // Cristal igual que el menú: lo que hay detrás se ve entero pero difuminado, con un tinte del 15 %.
    // El texto y los controles se vuelven claros u oscuros según ese fondo.
    public const float Frost = 1f;
    bool glassReady;

    public void UseGlass(double backdropLuminance)
    {
        // Con margen: si el fondo ronda el punto medio, no se cambia de claro a oscuro (evita parpadeos al mover).
        LightText = !glassReady ? backdropLuminance < 0.5 : LightText ? backdropLuminance < 0.55 : backdropLuminance < 0.45;
        glassReady = true;
        Glass = true;
        Window = Dark ? Color.FromArgb(42, 28, 28, 30) : Color.FromArgb(38, 246, 246, 248);
        if (LightText)
        {
            Canvas = Color.FromArgb(40, 0, 0, 0);
            Text = Color.FromArgb(245, 255, 255, 255);
            Secondary = Color.FromArgb(200, 255, 255, 255);
            Separator = Color.FromArgb(60, 255, 255, 255);
            Control = Color.FromArgb(64, 255, 255, 255);
            ControlPressed = Color.FromArgb(96, 255, 255, 255);
            ControlSelected = Color.FromArgb(120, 255, 255, 255);
            Track = Color.FromArgb(80, 255, 255, 255);
        }
        else
        {
            Canvas = Color.FromArgb(40, 255, 255, 255);
            Text = Color.FromArgb(235, 0, 0, 0);
            Secondary = Color.FromArgb(190, 20, 20, 25);
            Separator = Color.FromArgb(45, 0, 0, 0);
            Control = Color.FromArgb(64, 255, 255, 255);
            ControlPressed = Color.FromArgb(40, 0, 0, 0);
            ControlSelected = Color.FromArgb(235, 255, 255, 255);
            Track = Color.FromArgb(50, 0, 0, 0);
        }
    }

    // Texto con halo del color contrario (como las etiquetas del menú): se lee aunque detrás
    // haya cualquier cosa. Sin cristal, texto normal.
    public void DrawText(Graphics g, string text, Font font, RectangleF r, StringFormat sf, Color color, bool halo)
    {
        if (!Glass || !halo)
        {
            using (var b = new SolidBrush(color)) g.DrawString(text, font, b, r, sf);
            return;
        }
        using (var path = new GraphicsPath())
        {
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, r, sf);
            var h = LightText ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(190, 255, 255, 255);
            using (var glow = new Pen(Color.FromArgb(h.A / 3, h), 6f * Scale) { LineJoin = LineJoin.Round }) g.DrawPath(glow, path);
            using (var line = new Pen(h, 2.6f * Scale) { LineJoin = LineJoin.Round }) g.DrawPath(line, path);
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
        }
    }

    public static EditorTheme Current(float scale)
    {
        var look = Look.Current();
        var t = new EditorTheme
        {
            Dark = look.Dark,
            Glass = !look.Solid,
            Scale = scale,
            Accent = look.Accent,
            AccentPressed = look.AccentPressed,
            Semibold = RadialMenu.PickFont(RadialMenu.FontNames),
            Regular = RadialMenu.PickFont(RadialMenu.TextFontNames)
        };
        if (t.Dark)
        {
            t.Window = Color.FromArgb(28, 28, 30);
            t.Canvas = Color.FromArgb(44, 44, 46);
            t.Text = Color.FromArgb(240, 245, 245, 247);
            t.Secondary = Color.FromArgb(160, 235, 235, 245);
            t.Separator = Color.FromArgb(40, 255, 255, 255);
            t.Control = Color.FromArgb(58, 58, 60);
            t.ControlPressed = Color.FromArgb(72, 72, 74);
            t.ControlSelected = Color.FromArgb(99, 99, 102);
            t.Track = Color.FromArgb(72, 72, 74);
        }
        else
        {
            t.Window = Color.FromArgb(246, 246, 248);
            t.Canvas = Color.FromArgb(232, 232, 237);
            t.Text = Color.FromArgb(225, 0, 0, 0);
            t.Secondary = Color.FromArgb(150, 60, 60, 67);
            t.Separator = Color.FromArgb(40, 60, 60, 67);
            t.Control = Color.FromArgb(229, 229, 234);
            t.ControlPressed = Color.FromArgb(209, 209, 214);
            t.ControlSelected = Color.White;
            t.Track = Color.FromArgb(209, 209, 214);
        }
        return t;
    }

    public Font Font(float px, bool semibold)
    {
        return new Font(semibold ? Semibold : Regular, px * Scale, GraphicsUnit.Pixel);
    }
}

static class Shapes
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        if (radius < 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Sombra suave: varias capas casi transparentes cada vez más grandes.
    public static void SoftShadow(Graphics g, RectangleF r, float radius, float blur, float offsetY, int maxAlpha)
    {
        const int steps = 10;
        for (int i = steps; i >= 1; i--)
        {
            float grow = blur * i / steps;
            var rr = RectangleF.Inflate(new RectangleF(r.X, r.Y + offsetY, r.Width, r.Height), grow, grow);
            using (var path = Rounded(rr, radius + grow))
            using (var b = new SolidBrush(Color.FromArgb(Math.Max(1, maxAlpha / steps), 0, 0, 0)))
                g.FillPath(b, path);
        }
    }
}

abstract class Widget
{
    public RectangleF Bounds;
    public bool Visible = true;
    public event Action Changed;

    public abstract void Draw(Graphics g, EditorTheme t);
    public virtual void Down(PointF p) { }
    public virtual void Drag(PointF p) { }
    public virtual void Up(PointF p) { }

    protected void OnChanged()
    {
        if (Changed != null) Changed();
    }
}

class TextWidget : Widget
{
    public string Text = "";
    public bool Secondary = true;
    public float Size = 12.5f;
    public StringAlignment Align = StringAlignment.Near;

    public override void Draw(Graphics g, EditorTheme t)
    {
        using (var f = t.Font(Size, !Secondary))
        using (var sf = new StringFormat { Alignment = Align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            t.DrawText(g, Text, f, Bounds, sf, Secondary ? t.Secondary : t.Text, true);
    }
}

class PillButton : Widget
{
    public string Text = "";
    public bool Primary;
    public Action Click;
    bool pressed;

    public override void Draw(Graphics g, EditorTheme t)
    {
        Color fill = Primary ? (pressed ? t.AccentPressed : t.Accent) : (pressed ? t.ControlPressed : t.Control);
        using (var path = Shapes.Rounded(Bounds, Bounds.Height / 2))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        using (var f = t.Font(13f, true))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            t.DrawText(g, Text, f, Bounds, sf, Primary ? Color.White : t.Text, !Primary);
    }

    // Respuesta al pulsar; la acción ocurre al soltar dentro del botón (se cancela arrastrando fuera).
    public override void Down(PointF p) { pressed = true; }
    public override void Drag(PointF p) { pressed = Bounds.Contains(p); }

    public override void Up(PointF p)
    {
        bool fire = pressed && Bounds.Contains(p);
        pressed = false;
        if (fire && Click != null) Click();
    }
}

class Segmented : Widget
{
    public string[] Options = new string[0];
    public int Index;

    public override void Draw(Graphics g, EditorTheme t)
    {
        float r = 8 * t.Scale;
        using (var path = Shapes.Rounded(Bounds, r))
        using (var b = new SolidBrush(t.Control))
            g.FillPath(b, path);

        float w = Bounds.Width / Options.Length;
        var sel = new RectangleF(Bounds.X + Index * w + 2 * t.Scale, Bounds.Y + 2 * t.Scale, w - 4 * t.Scale, Bounds.Height - 4 * t.Scale);
        Shapes.SoftShadow(g, sel, r - 2, 2 * t.Scale, 1 * t.Scale, t.Dark ? 60 : 30);
        using (var path = Shapes.Rounded(sel, r - 2 * t.Scale))
        using (var b = new SolidBrush(t.ControlSelected))
            g.FillPath(b, path);

        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            for (int i = 0; i < Options.Length; i++)
            {
                // El segmento elegido es casi opaco: ahí el texto no necesita halo.
                bool selectedSolid = i == Index && t.ControlSelected.A > 200;
                var color = selectedSolid ? Color.FromArgb(230, 0, 0, 0) : t.Text;
                using (var f = t.Font(12.5f, i == Index))
                    t.DrawText(g, Options[i], f, new RectangleF(Bounds.X + i * w, Bounds.Y, w, Bounds.Height), sf, color, !selectedSolid);
            }
        }
    }

    public override void Down(PointF p) { Pick(p); }
    public override void Drag(PointF p) { Pick(p); }

    void Pick(PointF p)
    {
        int i = (int)((p.X - Bounds.X) / (Bounds.Width / Options.Length));
        i = Math.Max(0, Math.Min(Options.Length - 1, i));
        if (i == Index) return;
        Index = i;
        OnChanged();
    }
}

class Slider : Widget
{
    public string Label = "";
    public double Min, Max = 100, Value;
    public Func<double, string> Format = v => ((int)Math.Round(v)).ToString();
    float scale = 1;

    RectangleF Track
    {
        get
        {
            float labelW = 92 * scale, valueW = 70 * scale;
            return new RectangleF(Bounds.X + labelW, Bounds.Y + Bounds.Height / 2 - 2 * scale, Bounds.Width - labelW - valueW, 4 * scale);
        }
    }

    public override void Draw(Graphics g, EditorTheme t)
    {
        scale = t.Scale;
        var track = Track;
        using (var f = t.Font(13f, false))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            t.DrawText(g, Label, f, new RectangleF(Bounds.X, Bounds.Y, 92 * scale, Bounds.Height), sf, t.Text, true);

        using (var path = Shapes.Rounded(track, track.Height / 2))
        using (var b = new SolidBrush(t.Track))
            g.FillPath(b, path);

        // El relleno sale del cero: en rangos con negativos (brillo, contraste) se ve hacia qué lado se ajusta.
        double origin = Min < 0 && Max > 0 ? 0 : Min;
        float x0 = XFor(origin), x1 = XFor(Value);
        var fill = new RectangleF(Math.Min(x0, x1), track.Y, Math.Abs(x1 - x0), track.Height);
        if (fill.Width > 0.5f)
        {
            using (var path = Shapes.Rounded(fill, track.Height / 2))
            using (var b = new SolidBrush(t.Accent))
                g.FillPath(b, path);
        }

        float d = 20 * scale;
        var thumb = new RectangleF(x1 - d / 2, track.Y + track.Height / 2 - d / 2, d, d);
        Shapes.SoftShadow(g, thumb, d / 2, 3 * scale, 1 * scale, 70);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, thumb);
        using (var p = new Pen(Color.FromArgb(30, 0, 0, 0), 1)) g.DrawEllipse(p, thumb);

        using (var f = t.Font(12.5f, false))
        using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            t.DrawText(g, Format(Value), f, new RectangleF(Bounds.Right - 70 * scale, Bounds.Y, 70 * scale, Bounds.Height), sf, t.Secondary, true);
    }

    float XFor(double v)
    {
        var track = Track;
        return track.X + (float)((v - Min) / (Max - Min)) * track.Width;
    }

    public override void Down(PointF p) { Set(p); }
    public override void Drag(PointF p) { Set(p); }

    void Set(PointF p)
    {
        var track = Track;
        double k = Math.Max(0, Math.Min(1, (p.X - track.X) / track.Width));
        double v = Math.Round(Min + k * (Max - Min));
        if (v == Value) return;
        Value = v;
        OnChanged();
    }
}

class Swatch
{
    public Color A, B;

    public Swatch(Color a, Color b) { A = a; B = b; }
    public Swatch(Color c) { A = c; B = c; }

    public Brush MakeBrush(RectangleF r)
    {
        if (A == B) return new SolidBrush(A);
        return new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), A, B, 45f);
    }
}

class SwatchRow : Widget
{
    public List<Swatch> Items = new List<Swatch>();
    public int Index;

    float Size(EditorTheme t) { return 26 * t.Scale; }

    public override void Draw(Graphics g, EditorTheme t)
    {
        float d = Size(t), gap = 9 * t.Scale;
        for (int i = 0; i < Items.Count; i++)
        {
            var r = new RectangleF(Bounds.X + i * (d + gap) + 3 * t.Scale, Bounds.Y + (Bounds.Height - d) / 2, d, d);
            using (var b = Items[i].MakeBrush(r)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(40, 0, 0, 0), 1)) g.DrawEllipse(p, r);
            if (i == Index)
            {
                using (var p = new Pen(t.Accent, 2 * t.Scale))
                    g.DrawEllipse(p, RectangleF.Inflate(r, 3.5f * t.Scale, 3.5f * t.Scale));
            }
        }
        lastScale = t.Scale;
    }

    float lastScale = 1;

    public override void Down(PointF p)
    {
        float d = 26 * lastScale, gap = 9 * lastScale;
        int i = (int)((p.X - Bounds.X) / (d + gap));
        if (i < 0 || i >= Items.Count || i == Index) return;
        Index = i;
        OnChanged();
    }
}

// ---------------------------------------------------------------------------------------------
// Ventana base
// ---------------------------------------------------------------------------------------------

abstract class EditorForm : Form
{
    public static Action<string, string, bool> Notify;   // la bandeja muestra el aviso al guardar

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, int affinity);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    // Barra de título propia, al estilo macOS: cerrar, minimizar y maximizar a la izquierda.
    static readonly Color[] LightColors = { Color.FromArgb(255, 95, 87), Color.FromArgb(254, 188, 46), Color.FromArgb(40, 200, 64) };
    readonly float titleHeight;
    int hoverLight = -1, pressedLight = -1;
    Bitmap buffer;
    Bitmap backdrop;            // lo que hay detrás de la ventana, desenfocado (para el esmerilado)
    bool renderQueued;
    readonly System.Windows.Forms.Timer moveTimer;
    readonly System.Diagnostics.Stopwatch moveClock = System.Diagnostics.Stopwatch.StartNew();
    readonly Size intendedSize;

    protected readonly string SourcePath;
    protected Bitmap Original;      // resolución completa
    protected Bitmap PreviewBase;   // copia reducida para dibujar rápido
    protected readonly EditorTheme T;
    protected readonly float S;
    protected readonly List<Widget> Widgets = new List<Widget>();
    protected RectangleF PreviewArea, PanelArea;
    readonly float panelHeight;
    Widget active;
    bool previewDown;

    protected EditorForm(string path, string title, float panelHeightDip)
    {
        SourcePath = path;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
        T = EditorTheme.Current(S);
        Original = ImageOps.Load(path);
        PreviewBase = Downscale(Original, 1600);
        panelHeight = panelHeightDip * S;
        titleHeight = 40 * S;

        Text = title + " — " + Path.GetFileName(path);
        Icon = Theme.MakeTrayIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        intendedSize = new Size((int)(780 * S), (int)(560 * S + panelHeight + titleHeight));
        ClientSize = intendedSize;
        MinimumSize = new Size((int)(560 * S), (int)(360 * S + panelHeight + titleHeight));
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        MaximizedBounds = area;
        BackColor = T.Window;
        KeyPreview = true;
        // Se pinta a mano en un búfer con alfa (ver OnPaint); el doble búfer de WinForms perdería la transparencia.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = false;

        // Cristal: se mira lo que hay detrás antes de mostrar la ventana para elegir texto claro u oscuro.
        moveTimer = new System.Windows.Forms.Timer { Interval = 140 };
        moveTimer.Tick += delegate { moveTimer.Stop(); RefreshBackdrop(); Render(); };
        if (T.Glass) RefreshBackdrop();
    }

    // Captura lo que hay detrás (las ventanas en capas, como esta, no salen en la captura),
    // decide el color del texto según su luminosidad y guarda una copia desenfocada para el esmerilado.
    void RefreshBackdrop()
    {
        if (!T.Glass) return;
        try
        {
            var r = Bounds;
            using (var shot = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                // La captura incluiría la propia ventana y sus colores se irían colando en el cristal cada vez
                // que se rehace (al moverla cambiaba de color). Se excluye de la captura solo durante este
                // instante, esperando a que Windows componga un fotograma sin ella.
                bool exclude = IsHandleCreated && Visible;
                if (exclude) { SetWindowDisplayAffinity(Handle, 0x11); DwmFlush(); }   // WDA_EXCLUDEFROMCAPTURE
                try
                {
                    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                }
                finally
                {
                    if (exclude) SetWindowDisplayAffinity(Handle, 0);                     // WDA_NONE
                }
                T.UseGlass(Luminance(shot));
                if (backdrop != null) backdrop.Dispose();
                backdrop = RadialMenu.Blur(shot);
            }
        }
        catch { T.UseGlass(0.5); }
    }

    static double Luminance(Bitmap bmp)
    {
        double sum = 0;
        int count = 0, step = Math.Max(8, Math.Min(bmp.Width, bmp.Height) / 24);
        for (int y = step / 2; y < bmp.Height; y += step)
            for (int x = step / 2; x < bmp.Width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                sum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
                count++;
            }
        return count == 0 ? 0.5 : sum / count;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Sin borde de Windows, pero con menú de sistema y minimizar: así la barra de tareas puede
            // minimizar/restaurar la ventana y Alt+Espacio sigue funcionando.
            cp.Style |= 0x00080000 | 0x00020000 | 0x00010000; // WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
            // Con cristal, ventana en capas: cada píxel lleva su propia transparencia (como el menú).
            if (T != null && T.Glass) cp.ExStyle |= 0x00080000;  // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, 4);   // esquinas redondeadas de Windows 11 (sin cristal)
    }

    // --- Barra de título propia -------------------------------------------------------------

    RectangleF LightRect(int i)
    {
        float d = 12 * S, gap = 8 * S;
        return new RectangleF(16 * S + i * (d + gap), (titleHeight - d) / 2, d, d);
    }

    int LightAt(Point p)
    {
        for (int i = 0; i < 3; i++) if (RectangleF.Inflate(LightRect(i), 4 * S, 4 * S).Contains(p)) return i;
        return -1;
    }

    void DrawTitleBar(Graphics g)
    {
        bool showGlyphs = hoverLight >= 0;
        for (int i = 0; i < 3; i++)
        {
            var r = LightRect(i);
            var c = LightColors[i];
            if (i == pressedLight) c = Color.FromArgb(c.R * 4 / 5, c.G * 4 / 5, c.B * 4 / 5);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(40, 0, 0, 0), 1)) g.DrawEllipse(p, r);
            if (!showGlyphs) continue;
            // Los símbolos aparecen al pasar por encima, como en macOS.
            using (var p = new Pen(Color.FromArgb(150, 0, 0, 0), 1.3f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = 2.8f * S;
                if (i == 0) { g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx - k, cy + k, cx + k, cy - k); }
                else if (i == 1) g.DrawLine(p, cx - k, cy, cx + k, cy);
                else { g.DrawLine(p, cx - k, cy, cx + k, cy); g.DrawLine(p, cx, cy - k, cx, cy + k); }
            }
        }
        using (var f = T.Font(13f, true))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            T.DrawText(g, Text, f, new RectangleF(100 * S, 0, ClientSize.Width - 200 * S, titleHeight), sf, T.Text, true);
    }

    void PressLight(int i)
    {
        if (i == 0) Close();
        else if (i == 1) WindowState = FormWindowState.Minimized;
        else WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    // Arrastrar desde la barra y redimensionar desde los bordes, ya que la ventana no tiene marco de Windows.
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST)
        {
            int lp = m.LParam.ToInt32();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            int edge = (int)(6 * S), w = ClientSize.Width, h = ClientSize.Height;
            bool left = p.X < edge, right = p.X >= w - edge, top = p.Y < edge, bottom = p.Y >= h - edge;
            if (WindowState == FormWindowState.Normal)
            {
                if (top && left) { m.Result = (IntPtr)13; return; }
                if (top && right) { m.Result = (IntPtr)14; return; }
                if (bottom && left) { m.Result = (IntPtr)16; return; }
                if (bottom && right) { m.Result = (IntPtr)17; return; }
                if (left) { m.Result = (IntPtr)10; return; }
                if (right) { m.Result = (IntPtr)11; return; }
                if (top) { m.Result = (IntPtr)12; return; }
                if (bottom) { m.Result = (IntPtr)15; return; }
            }
            if (p.Y < titleHeight && LightAt(p) < 0) { m.Result = (IntPtr)2; return; }   // HTCAPTION
            m.Result = (IntPtr)1;                                                           // HTCLIENT
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Traer al frente: tras soltar un archivo, Windows no siempre deja activar ventanas nuevas.
        TopMost = true;
        Activate();
        BeginInvoke(new Action(() => TopMost = false));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Al crear la ventana, Windows le descuenta un marco que no tiene: se le devuelve su tamaño y se centra.
        if (ClientSize != intendedSize)
        {
            ClientSize = intendedSize;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
            RefreshBackdrop();
        }
        PerformLayout();
        Render();   // una ventana en capas no se ve hasta que se le da su primera imagen
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // La ventana base se coloca antes de que la derivada cree sus controles.
        if (Widgets.Count == 0) return;
        float m = 20 * S;
        PanelArea = new RectangleF(m, ClientSize.Height - panelHeight - m, ClientSize.Width - 2 * m, panelHeight);
        PreviewArea = new RectangleF(m, titleHeight, ClientSize.Width - 2 * m, PanelArea.Top - m - titleHeight);
        LayoutWidgets(PanelArea);
        Invalidate();
    }

    protected abstract void LayoutWidgets(RectangleF panel);

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (T.Glass) { Render(); return; }
        if (!DrawBuffer()) return;
        // Sin cristal: el búfer se copia a la ventana como siempre.
        IntPtr hbmp = buffer.GetHbitmap(Color.FromArgb(0));
        IntPtr mem = CreateCompatibleDC(IntPtr.Zero);
        IntPtr old = SelectObject(mem, hbmp);
        IntPtr hdc = e.Graphics.GetHdc();
        try { BitBlt(hdc, 0, 0, buffer.Width, buffer.Height, mem, 0, 0, 0x00CC0020); }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
            SelectObject(mem, old);
            DeleteDC(mem);
            DeleteObject(hbmp);
        }
    }

    // Una ventana en capas no recibe WM_PAINT útil: cada Invalidate() programa un redibujado
    // (agrupado, para no repetirlo varias veces por movimiento del ratón).
    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        if (!T.Glass || renderQueued || !IsHandleCreated) return;
        renderQueued = true;
        BeginInvoke(new Action(() => { renderQueued = false; Render(); }));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (T.Glass && IsHandleCreated && WindowState != FormWindowState.Minimized) { RefreshBackdrop(); Render(); }
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        // Al terminar de mover la ventana se vuelve a mirar qué hay detrás.
        // (moveTimer aún no existe mientras el constructor coloca la ventana.)
        if (!T.Glass || moveTimer == null) return;
        // Mientras se arrastra, el desenfoque se rehace como mucho cada 50 ms para que el fondo acompañe
        // a la ventana; al soltarla, una última vez.
        if (IsHandleCreated && moveClock.ElapsedMilliseconds > 50)
        {
            moveClock.Restart();
            RefreshBackdrop();
            Render();
        }
        moveTimer.Stop();
        moveTimer.Start();
    }

    void Render()
    {
        if (!T.Glass || !IsHandleCreated || WindowState == FormWindowState.Minimized) return;
        if (DrawBuffer()) Native.ApplyLayeredBitmap(this, buffer);
    }

    bool DrawBuffer()
    {
        if (ClientSize.Width < 1 || ClientSize.Height < 1 || Widgets.Count == 0) return false;
        if (buffer == null || buffer.Size != ClientSize)
        {
            if (buffer != null) buffer.Dispose();
            buffer = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        }

        using (var g = Graphics.FromImage(buffer))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // ClearType no sirve sobre fondo transparente (deja bordes de color); suavizado normal.
            g.TextRenderingHint = T.Glass ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var whole = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            bool rounded = T.Glass && WindowState == FormWindowState.Normal;
            float radius = rounded ? 12 * S : 0;
            using (var shape = Shapes.Rounded(RectangleF.Inflate(whole, -0.5f, -0.5f), radius))
            {
                if (T.Glass)
                {
                    // Cristal: esquinas redondeadas propias, esmerilado al 15 % y tinte ligero.
                    g.Clear(Color.Transparent);
                    g.SetClip(shape);
                    if (backdrop != null)
                    {
                        using (var attrs = new ImageAttributes())
                        {
                            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = EditorTheme.Frost });
                            g.DrawImage(backdrop, new Rectangle(0, 0, buffer.Width, buffer.Height), 0, 0, backdrop.Width, backdrop.Height, GraphicsUnit.Pixel, attrs);
                        }
                    }
                    using (var b = new SolidBrush(T.Window)) g.FillPath(b, shape);
                }
                else g.Clear(T.Window);

                DrawTitleBar(g);
                using (var path = Shapes.Rounded(PreviewArea, 12 * S))
                using (var b = new SolidBrush(T.Canvas))
                    g.FillPath(b, path);
                var clip = g.Clip;
                using (var path = Shapes.Rounded(PreviewArea, 12 * S)) g.SetClip(path, CombineMode.Intersect);
                DrawPreview(g);
                g.Clip = clip;

                foreach (var w in Widgets.Where(w => w.Visible)) w.Draw(g, T);
                g.ResetClip();

                // Borde fino de luz alrededor de la ventana.
                using (var p = new Pen(T.Separator, 1)) g.DrawPath(p, shape);
            }
        }
        return true;
    }

    protected virtual void DrawPreview(Graphics g)
    {
        DrawImageShadowed(g, PreviewBase, ImageRect);
    }

    protected void DrawImageShadowed(Graphics g, Image img, RectangleF r)
    {
        Shapes.SoftShadow(g, r, 2 * S, 10 * S, 3 * S, T.Dark ? 120 : 60);
        g.DrawImageUnscaled(Fitted(img, r), (int)Math.Round(r.X), (int)Math.Round(r.Y));
    }

    // Copia de `img` ya escalada al tamaño en pantalla. Se rehace solo si cambia la imagen o el tamaño,
    // así arrastrar el recorte o dibujar no reescala la foto en cada movimiento del ratón.
    Image fitSource;
    Bitmap fitted;

    protected Bitmap Fitted(Image img, RectangleF r)
    {
        var size = new Size(Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));
        if (fitted != null && fitSource == img && fitted.Size == size) return fitted;
        if (fitted != null) fitted.Dispose();
        fitted = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(fitted))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, 0, 0, size.Width, size.Height);
        }
        fitSource = img;
        return fitted;
    }

    // Rectángulo donde se dibuja la imagen dentro de la vista previa (encajada, centrada).
    protected RectangleF FitRect(SizeF size)
    {
        var area = RectangleF.Inflate(PreviewArea, -24 * S, -24 * S);
        float k = Math.Min(area.Width / size.Width, area.Height / size.Height);
        k = Math.Min(k, 1 * S * 2); // no ampliar demasiado imágenes pequeñas
        float w = size.Width * k, h = size.Height * k;
        return new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    protected RectangleF ImageRect { get { return FitRect(Original.Size); } }
    protected float ImageScale { get { return ImageRect.Width / Original.Width; } }

    protected PointF ToImage(PointF p)
    {
        var r = ImageRect;
        float k = ImageScale;
        return new PointF((p.X - r.X) / k, (p.Y - r.Y) / k);
    }

    protected PointF ToScreen(PointF p)
    {
        var r = ImageRect;
        float k = ImageScale;
        return new PointF(r.X + p.X * k, r.Y + p.Y * k);
    }

    protected RectangleF ToScreen(RectangleF r)
    {
        var a = ToScreen(r.Location);
        return new RectangleF(a.X, a.Y, r.Width * ImageScale, r.Height * ImageScale);
    }

    // Matriz que lleva coordenadas de imagen a pantalla (para dibujar marcas en la vista previa).
    protected Matrix ImageToScreenMatrix()
    {
        var m = new Matrix();
        var r = ImageRect;
        m.Translate(r.X, r.Y);
        m.Scale(ImageScale, ImageScale);
        return m;
    }

    // --- Ratón: primero los controles; si no, la vista previa ------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressedLight = LightAt(e.Location);
        if (pressedLight >= 0) { Invalidate(); return; }
        active = Widgets.LastOrDefault(w => w.Visible && w.Bounds.Contains(e.Location));
        if (active != null) active.Down(e.Location);
        else if (PreviewArea.Contains(e.Location)) { previewDown = true; PreviewDown(e.Location); }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int light = LightAt(e.Location) >= 0 ? 0 : -1;   // al pasar por cualquiera se ven los tres símbolos
        if (light != hoverLight) { hoverLight = light; Invalidate(new Rectangle(0, 0, (int)(100 * S), (int)titleHeight)); }
        if (active != null) { active.Drag(e.Location); Invalidate(); return; }
        if (previewDown) { PreviewDrag(e.Location); Invalidate(); }
        Cursor = PreviewArea.Contains(e.Location) || previewDown ? PreviewCursor(e.Location) : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverLight >= 0) { hoverLight = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (pressedLight >= 0)
        {
            int i = pressedLight;
            pressedLight = -1;
            Invalidate();
            if (LightAt(e.Location) == i) PressLight(i);
            return;
        }
        if (active != null) { var w = active; active = null; w.Up(e.Location); }
        else if (previewDown) { previewDown = false; PreviewUp(e.Location); }
        Invalidate();
    }

    protected virtual void PreviewDown(PointF p) { }
    protected virtual void PreviewDrag(PointF p) { }
    protected virtual void PreviewUp(PointF p) { }
    protected virtual Cursor PreviewCursor(PointF p) { return Cursors.Default; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    // --- Guardar --------------------------------------------------------------------------

    protected string SourceExt
    {
        get
        {
            string ext = Path.GetExtension(SourcePath).ToLowerInvariant();
            return ext == ".jpeg" ? ".jpg" : ext == ".tif" ? ".tiff" : ext;
        }
    }

    protected void SaveResult(Bitmap result, string suffix, string ext)
    {
        try
        {
            string output = Paths.Beside(SourcePath, " " + suffix, ext);
            ImageOps.SaveAs(result, output, ext);
            if (Notify != null) Notify("Guardado ✓", Path.GetFileName(output), false);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Switchify", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected static Bitmap Downscale(Bitmap src, int max)
    {
        float k = Math.Min(1f, (float)max / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));

        // Bilineal simple: sobre una foto de 12 MP tarda ~50 ms frente a ~150 ms del bicúbico de alta calidad.
        // Es solo para la vista previa (que luego se suaviza al ajustarla a la ventana); lo que se guarda
        // siempre sale de la imagen original.
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        return bmp;
    }

    protected static Bitmap Copy32(Bitmap src)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        return bmp;
    }

    // Fila de botones al pie, alineados a la derecha (el principal, el último).
    protected void LayoutButtons(RectangleF panel, params PillButton[] buttons)
    {
        float h = 34 * S, gap = 10 * S, x = panel.Right;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            float w;
            using (var f = T.Font(13f, true))
            using (var g = CreateGraphics())
                w = g.MeasureString(buttons[i].Text, f).Width + 36 * S;
            x -= w;
            buttons[i].Bounds = new RectangleF(x, panel.Bottom - h, w, h);
            x -= gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (Original != null) Original.Dispose();
            if (PreviewBase != null) PreviewBase.Dispose();
            if (buffer != null) buffer.Dispose();
            if (backdrop != null) backdrop.Dispose();
            moveTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------------------------
// Recortar
// ---------------------------------------------------------------------------------------------

class CropWindow : EditorForm
{
    static readonly string[] Ratios = { "Libre", "1:1", "16:9", "9:16", "4:3", "3:4" };
    static readonly float[] RatioValues = { 0, 1, 16f / 9, 9f / 16, 4f / 3, 3f / 4 };
    const int HL = 1, HT = 2, HR = 4, HB = 8, HMove = 16;

    RectangleF crop;
    int handle;
    PointF dragStart;
    RectangleF cropStart;
    readonly Segmented ratio;
    readonly TextWidget ratioLabel, sizeText;
    readonly PillButton reset, apply;

    public CropWindow(string path) : base(path, "Recortar imagen", 92)
    {
        crop = Full;
        ratioLabel = new TextWidget { Text = "Proporción" };
        ratio = new Segmented { Options = Ratios };
        ratio.Changed += ApplyRatio;
        sizeText = new TextWidget();
        reset = new PillButton { Text = "Restablecer", Click = () => { ratio.Index = 0; crop = Full; Invalidate(); } };
        apply = new PillButton { Text = "Aplicar", Primary = true, Click = Apply };
        Widgets.AddRange(new Widget[] { ratioLabel, ratio, sizeText, reset, apply });
    }

    RectangleF Full { get { return new RectangleF(0, 0, Original.Width, Original.Height); } }

    protected override void LayoutWidgets(RectangleF panel)
    {
        ratioLabel.Bounds = new RectangleF(panel.X, panel.Y, 96 * S, 32 * S);
        ratio.Bounds = new RectangleF(panel.X + 96 * S, panel.Y, Math.Min(440 * S, panel.Width - 96 * S), 32 * S);
        LayoutButtons(panel, reset, apply);
        sizeText.Bounds = new RectangleF(panel.X, reset.Bounds.Y, 260 * S, reset.Bounds.Height);
    }

    protected override void DrawPreview(Graphics g)
    {
        base.DrawPreview(g);
        var img = ImageRect;
        var c = ToScreen(crop);
        sizeText.Text = (int)Math.Round(crop.Width) + " × " + (int)Math.Round(crop.Height) + " px";

        // Fuera del recorte, oscurecido.
        using (var outside = new GraphicsPath { FillMode = FillMode.Alternate })
        using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        {
            outside.AddRectangle(img);
            outside.AddRectangle(c);
            g.FillPath(b, outside);
        }

        using (var p = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f * S)) g.DrawRectangle(p, c.X, c.Y, c.Width, c.Height);

        // Regla de los tercios mientras se ajusta.
        if (handle != 0)
        {
            using (var p = new Pen(Color.FromArgb(110, 255, 255, 255), 1))
            {
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(p, c.X + c.Width * i / 3, c.Y, c.X + c.Width * i / 3, c.Bottom);
                    g.DrawLine(p, c.X, c.Y + c.Height * i / 3, c.Right, c.Y + c.Height * i / 3);
                }
            }
        }

        // Asas en las esquinas (en L) y en el centro de cada lado.
        float len = 18 * S, th = 4 * S;
        using (var p = new Pen(T.Accent, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var corners = new[] { new PointF(c.Left, c.Top), new PointF(c.Right, c.Top), new PointF(c.Right, c.Bottom), new PointF(c.Left, c.Bottom) };
            var dirs = new[] { new PointF(1, 1), new PointF(-1, 1), new PointF(-1, -1), new PointF(1, -1) };
            for (int i = 0; i < 4; i++)
            {
                g.DrawLine(p, corners[i], new PointF(corners[i].X + dirs[i].X * len, corners[i].Y));
                g.DrawLine(p, corners[i], new PointF(corners[i].X, corners[i].Y + dirs[i].Y * len));
            }
            float mx = c.X + c.Width / 2, my = c.Y + c.Height / 2, half = len * 0.6f;
            g.DrawLine(p, mx - half, c.Top, mx + half, c.Top);
            g.DrawLine(p, mx - half, c.Bottom, mx + half, c.Bottom);
            g.DrawLine(p, c.Left, my - half, c.Left, my + half);
            g.DrawLine(p, c.Right, my - half, c.Right, my + half);
        }
    }

    int HandleAt(PointF p)
    {
        var c = ToScreen(crop);
        float tol = 14 * S;
        bool inY = p.Y > c.Top - tol && p.Y < c.Bottom + tol, inX = p.X > c.Left - tol && p.X < c.Right + tol;
        int h = 0;
        if (inY && Math.Abs(p.X - c.Left) < tol) h |= HL;
        if (inY && Math.Abs(p.X - c.Right) < tol) h |= HR;
        if (inX && Math.Abs(p.Y - c.Top) < tol) h |= HT;
        if (inX && Math.Abs(p.Y - c.Bottom) < tol) h |= HB;
        if (h == 0 && c.Contains(p)) h = HMove;
        return h;
    }

    protected override Cursor PreviewCursor(PointF p)
    {
        int h = handle != 0 ? handle : HandleAt(p);
        if (h == (HL | HT) || h == (HR | HB)) return Cursors.SizeNWSE;
        if (h == (HR | HT) || h == (HL | HB)) return Cursors.SizeNESW;
        if (h == HL || h == HR) return Cursors.SizeWE;
        if (h == HT || h == HB) return Cursors.SizeNS;
        if (h == HMove) return Cursors.SizeAll;
        return Cursors.Default;
    }

    protected override void PreviewDown(PointF p)
    {
        handle = HandleAt(p);
        dragStart = p;
        cropStart = crop;
    }

    protected override void PreviewDrag(PointF p)
    {
        if (handle == 0) return;
        float dx = (p.X - dragStart.X) / ImageScale, dy = (p.Y - dragStart.Y) / ImageScale;
        var full = Full;

        if (handle == HMove)
        {
            float x = Math.Max(0, Math.Min(full.Width - cropStart.Width, cropStart.X + dx));
            float y = Math.Max(0, Math.Min(full.Height - cropStart.Height, cropStart.Y + dy));
            crop = new RectangleF(x, y, cropStart.Width, cropStart.Height);
            return;
        }

        const float min = 16;
        float left = cropStart.Left, right = cropStart.Right, top = cropStart.Top, bottom = cropStart.Bottom;
        if ((handle & HL) != 0) left = Math.Max(0, Math.Min(right - min, left + dx));
        if ((handle & HR) != 0) right = Math.Min(full.Width, Math.Max(left + min, right + dx));
        if ((handle & HT) != 0) top = Math.Max(0, Math.Min(bottom - min, top + dy));
        if ((handle & HB) != 0) bottom = Math.Min(full.Height, Math.Max(top + min, bottom + dy));

        float ar = RatioValues[ratio.Index];
        if (ar > 0)
        {
            bool horizontal = (handle & (HL | HR)) != 0, vertical = (handle & (HT | HB)) != 0;
            float w = right - left, h = bottom - top;
            if (horizontal && vertical)
            {
                h = w / ar;
                if ((handle & HT) != 0) top = bottom - h; else bottom = top + h;
            }
            else if (horizontal)
            {
                h = w / ar;
                float cy = (cropStart.Top + cropStart.Bottom) / 2;
                top = cy - h / 2; bottom = cy + h / 2;
            }
            else
            {
                w = h * ar;
                float cx = (cropStart.Left + cropStart.Right) / 2;
                left = cx - w / 2; right = cx + w / 2;
            }
            // Con proporción fija, si se sale de la imagen no se acepta el cambio.
            if (left < -0.5f || top < -0.5f || right > full.Width + 0.5f || bottom > full.Height + 0.5f) return;
        }
        crop = RectangleF.FromLTRB(left, top, right, bottom);
    }

    protected override void PreviewUp(PointF p) { handle = 0; }

    void ApplyRatio()
    {
        float ar = RatioValues[ratio.Index];
        if (ar <= 0) { Invalidate(); return; }
        // El mayor recorte con esa proporción, centrado en la imagen.
        float w = Original.Width, h = w / ar;
        if (h > Original.Height) { h = Original.Height; w = h * ar; }
        crop = new RectangleF((Original.Width - w) / 2, (Original.Height - h) / 2, w, h);
        Invalidate();
    }

    void Apply()
    {
        var rect = Rectangle.Round(crop);
        rect.Intersect(new Rectangle(0, 0, Original.Width, Original.Height));
        using (var result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(result))
                g.DrawImage(Original, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            SaveResult(result, "recortada", SourceExt);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) Apply();
        base.OnKeyDown(e);
    }
}

// ---------------------------------------------------------------------------------------------
// Añadir fondo
// ---------------------------------------------------------------------------------------------

class BackgroundWindow : EditorForm
{
    static readonly string[] Ratios = { "Auto", "1:1", "16:9", "4:3" };
    static readonly float[] RatioValues = { 0, 1, 16f / 9, 4f / 3 };

    readonly SwatchRow swatches;
    readonly Slider padding, corners, shadow;
    readonly Segmented ratio;
    readonly TextWidget bgLabel, ratioLabel;
    readonly PillButton reset, save;
    Bitmap previewCache, composeBase;

    public BackgroundWindow(string path) : base(path, "Añadir fondo", 236)
    {
        swatches = new SwatchRow
        {
            Items = new List<Swatch>
            {
                new Swatch(Color.FromArgb(255, 95, 109), Color.FromArgb(255, 195, 113)),
                new Swatch(Color.FromArgb(33, 147, 176), Color.FromArgb(109, 213, 237)),
                new Swatch(Color.FromArgb(142, 45, 226), Color.FromArgb(74, 0, 224)),
                new Swatch(Color.FromArgb(240, 147, 251), Color.FromArgb(245, 87, 108)),
                new Swatch(Color.FromArgb(67, 233, 123), Color.FromArgb(56, 249, 215)),
                new Swatch(Color.FromArgb(161, 196, 253), Color.FromArgb(194, 233, 251)),
                new Swatch(Color.FromArgb(35, 37, 38), Color.FromArgb(65, 67, 69)),
                new Swatch(Color.White),
                new Swatch(Color.FromArgb(242, 242, 247)),
                new Swatch(Color.FromArgb(28, 28, 30)),
                new Swatch(Color.FromArgb(0, 122, 255)),
                new Swatch(Color.FromArgb(255, 159, 10)),
                new Swatch(Color.FromArgb(52, 199, 89))
            }
        };
        float maxD = Math.Max(Original.Width, Original.Height);
        Func<double, string> px = v => ((int)Math.Round(maxD * v / 100)) + " px";
        padding = new Slider { Label = "Relleno", Min = 0, Max = 30, Value = 8, Format = px };
        corners = new Slider { Label = "Esquinas", Min = 0, Max = 15, Value = 3, Format = px };
        shadow = new Slider { Label = "Sombra", Min = 0, Max = 100, Value = 50, Format = v => (int)v + " %" };
        ratio = new Segmented { Options = Ratios };
        bgLabel = new TextWidget { Text = "Fondo" };
        ratioLabel = new TextWidget { Text = "Proporción" };
        reset = new PillButton { Text = "Restablecer", Click = Reset };
        save = new PillButton { Text = "Guardar con fondo", Primary = true, Click = Save };

        foreach (var w in new Widget[] { swatches, padding, corners, shadow, ratio }) w.Changed += () => { Dirty(); };
        Widgets.AddRange(new Widget[] { bgLabel, swatches, padding, corners, shadow, ratioLabel, ratio, reset, save });
    }

    protected override void LayoutWidgets(RectangleF panel)
    {
        float row = 34 * S, y = panel.Y, labelW = 92 * S;
        bgLabel.Bounds = new RectangleF(panel.X, y, labelW, row);
        swatches.Bounds = new RectangleF(panel.X + labelW, y, panel.Width - labelW, row);
        y += row + 4 * S;
        foreach (var s in new[] { padding, corners, shadow })
        {
            s.Bounds = new RectangleF(panel.X, y, panel.Width, row);
            y += row;
        }
        y += 4 * S;
        ratioLabel.Bounds = new RectangleF(panel.X, y, labelW, 30 * S);
        ratio.Bounds = new RectangleF(panel.X + labelW, y, Math.Min(300 * S, panel.Width - labelW), 30 * S);
        LayoutButtons(panel, reset, save);
    }

    void Dirty()
    {
        if (previewCache != null) { previewCache.Dispose(); previewCache = null; }
        Invalidate();
    }

    void Reset()
    {
        swatches.Index = 0; padding.Value = 8; corners.Value = 3; shadow.Value = 50; ratio.Index = 0;
        Dirty();
    }

    protected override void DrawPreview(Graphics g)
    {
        // La vista previa se compone sobre una copia pequeña: mover un deslizador rehace el fondo al instante.
        if (composeBase == null) composeBase = Downscale(PreviewBase, 640);
        if (previewCache == null) previewCache = Compose(composeBase);
        DrawImageShadowed(g, previewCache, FitRect(previewCache.Size));
    }

    Bitmap Compose(Bitmap img)
    {
        float maxD = Math.Max(img.Width, img.Height), minD = Math.Min(img.Width, img.Height);
        int pad = (int)(maxD * padding.Value / 100);
        float radius = (float)(maxD * corners.Value / 100);
        int w = img.Width + 2 * pad, h = img.Height + 2 * pad;
        float ar = RatioValues[ratio.Index];
        if (ar > 0)
        {
            if ((float)w / h < ar) w = (int)Math.Round(h * ar);
            else h = (int)Math.Round(w / ar);
        }

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var all = new RectangleF(0, 0, w, h);
            using (var b = swatches.Items[swatches.Index].MakeBrush(all)) g.FillRectangle(b, all);

            var rect = new RectangleF((w - img.Width) / 2f, (h - img.Height) / 2f, img.Width, img.Height);
            if (shadow.Value > 0)
            {
                // Sombra desenfocada: la forma se dibuja en pequeño y se amplía (el escalado hace de desenfoque).
                float blur = Math.Max(2, minD * 0.07f * (float)shadow.Value / 100);
                float f = 1 / Math.Max(1f, blur / 2.5f);
                using (var small = new Bitmap(Math.Max(1, (int)(w * f)), Math.Max(1, (int)(h * f)), PixelFormat.Format32bppPArgb))
                {
                    using (var sg = Graphics.FromImage(small))
                    {
                        sg.SmoothingMode = SmoothingMode.AntiAlias;
                        var sr = new RectangleF(rect.X * f, (rect.Y + blur * 0.4f) * f, rect.Width * f, rect.Height * f);
                        using (var path = Shapes.Rounded(sr, radius * f))
                        using (var b = new SolidBrush(Color.FromArgb((int)(110 * shadow.Value / 100), 0, 0, 0)))
                            sg.FillPath(b, path);
                    }
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attrs);
                    }
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                }
            }
            // Relleno con textura: bordes redondeados suavizados (un recorte normal quedaría dentado).
            using (var path = Shapes.Rounded(rect, radius))
            using (var tb = new TextureBrush(img, WrapMode.Clamp))
            {
                tb.TranslateTransform(rect.X, rect.Y);
                g.FillPath(tb, path);
            }
        }
        return bmp;
    }

    void Save()
    {
        using (var result = Compose(Original)) SaveResult(result, "con fondo", ".png");
    }
}

// ---------------------------------------------------------------------------------------------
// Editar (brillo, contraste, saturación, girar, voltear)
// ---------------------------------------------------------------------------------------------

class AdjustWindow : EditorForm
{
    readonly Slider brightness, contrast, saturation;
    readonly PillButton rotL, rotR, flipH, flipV, reset, save;
    readonly List<RotateFlipType> ops = new List<RotateFlipType>();
    Bitmap transformed;

    public AdjustWindow(string path) : base(path, "Editar imagen", 176)
    {
        Func<double, string> signed = v => v > 0 ? "+" + (int)v : ((int)v).ToString();
        brightness = new Slider { Label = "Brillo", Min = -100, Max = 100, Format = signed };
        contrast = new Slider { Label = "Contraste", Min = -100, Max = 100, Format = signed };
        saturation = new Slider { Label = "Saturación", Min = -100, Max = 100, Format = signed };
        rotL = new PillButton { Text = "Girar izq.", Click = () => AddOp(RotateFlipType.Rotate270FlipNone) };
        rotR = new PillButton { Text = "Girar der.", Click = () => AddOp(RotateFlipType.Rotate90FlipNone) };
        flipH = new PillButton { Text = "Voltear ↔", Click = () => AddOp(RotateFlipType.RotateNoneFlipX) };
        flipV = new PillButton { Text = "Voltear ↕", Click = () => AddOp(RotateFlipType.RotateNoneFlipY) };
        reset = new PillButton { Text = "Restablecer", Click = Reset };
        save = new PillButton { Text = "Guardar", Primary = true, Click = Save };
        foreach (var s in new[] { brightness, contrast, saturation }) s.Changed += Invalidate;
        Widgets.AddRange(new Widget[] { brightness, contrast, saturation, rotL, rotR, flipH, flipV, reset, save });
    }

    protected override void LayoutWidgets(RectangleF panel)
    {
        float row = 34 * S, y = panel.Y;
        foreach (var s in new[] { brightness, contrast, saturation })
        {
            s.Bounds = new RectangleF(panel.X, y, panel.Width, row);
            y += row;
        }
        LayoutButtons(panel, reset, save);
        float x = panel.X, h = 34 * S;
        foreach (var b in new[] { rotL, rotR, flipH, flipV })
        {
            float w = 104 * S;
            b.Bounds = new RectangleF(x, panel.Bottom - h, w, h);
            x += w + 8 * S;
        }
    }

    void AddOp(RotateFlipType op)
    {
        ops.Add(op);
        if (transformed != null) { transformed.Dispose(); transformed = null; }
        Invalidate();
    }

    void Reset()
    {
        brightness.Value = contrast.Value = saturation.Value = 0;
        ops.Clear();
        if (transformed != null) { transformed.Dispose(); transformed = null; }
        Invalidate();
    }

    Bitmap ApplyOps(Bitmap src)
    {
        var b = Copy32(src);
        foreach (var op in ops) b.RotateFlip(op);
        return b;
    }

    ImageAttributes Attributes()
    {
        float br = (float)brightness.Value / 100 * 0.35f;
        float c = 1 + (float)contrast.Value / 100;
        float s = 1 + (float)saturation.Value / 100;
        float[] lum = { 0.2126f, 0.7152f, 0.0722f };
        var m = new float[5][];
        for (int r = 0; r < 3; r++)
        {
            m[r] = new float[5];
            for (int col = 0; col < 3; col++) m[r][col] = ((1 - s) * lum[r] + (r == col ? s : 0)) * c;
        }
        float offset = (1 - c) / 2 + br;
        m[3] = new float[] { 0, 0, 0, 1, 0 };
        m[4] = new float[] { offset, offset, offset, 0, 1 };
        var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix(m));
        return attrs;
    }

    protected override void DrawPreview(Graphics g)
    {
        if (transformed == null) transformed = ApplyOps(PreviewBase);
        var r = FitRect(transformed.Size);
        Shapes.SoftShadow(g, r, 2 * S, 10 * S, 3 * S, T.Dark ? 120 : 60);
        // El ajuste de color se aplica sobre la copia ya escalada: mover un deslizador no reescala la foto.
        var fit = Fitted(transformed, r);
        using (var attrs = Attributes())
            g.DrawImage(fit, new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), fit.Width, fit.Height), 0, 0, fit.Width, fit.Height, GraphicsUnit.Pixel, attrs);
    }

    void Save()
    {
        using (var t = ApplyOps(Original))
        using (var result = new Bitmap(t.Width, t.Height, PixelFormat.Format32bppArgb))
        using (var attrs = Attributes())
        {
            using (var g = Graphics.FromImage(result))
                g.DrawImage(t, new Rectangle(0, 0, t.Width, t.Height), 0, 0, t.Width, t.Height, GraphicsUnit.Pixel, attrs);
            SaveResult(result, "editada", SourceExt);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Anotar
// ---------------------------------------------------------------------------------------------

class AnnotateWindow : EditorForm
{
    enum Tool { Pen, Arrow, Rect, Marker }

    class Mark
    {
        public Tool Tool;
        public Color Color;
        public float Width;
        public List<PointF> Points = new List<PointF>();
    }

    readonly Segmented tools;
    readonly SwatchRow colors;
    readonly Slider thickness;
    readonly TextWidget toolLabel, colorLabel;
    readonly PillButton undo, save;
    readonly List<Mark> marks = new List<Mark>();
    Mark current;

    public AnnotateWindow(string path) : base(path, "Anotar imagen", 170)
    {
        tools = new Segmented { Options = new[] { "Lápiz", "Flecha", "Rectángulo", "Marcador" } };
        colors = new SwatchRow
        {
            Items = new List<Swatch>
            {
                new Swatch(Color.FromArgb(255, 59, 48)), new Swatch(Color.FromArgb(255, 149, 0)), new Swatch(Color.FromArgb(255, 204, 0)),
                new Swatch(Color.FromArgb(52, 199, 89)), new Swatch(Color.FromArgb(0, 122, 255)), new Swatch(Color.Black), new Swatch(Color.White)
            }
        };
        thickness = new Slider { Label = "Grosor", Min = 1, Max = 30, Value = 6 };
        toolLabel = new TextWidget { Text = "Herramienta" };
        colorLabel = new TextWidget { Text = "Color" };
        undo = new PillButton { Text = "Deshacer", Click = () => { if (marks.Count > 0) marks.RemoveAt(marks.Count - 1); Invalidate(); } };
        save = new PillButton { Text = "Guardar", Primary = true, Click = Save };
        Widgets.AddRange(new Widget[] { toolLabel, tools, colorLabel, colors, thickness, undo, save });
    }

    protected override void LayoutWidgets(RectangleF panel)
    {
        float row = 34 * S, labelW = 92 * S, y = panel.Y;
        toolLabel.Bounds = new RectangleF(panel.X, y, labelW, 32 * S);
        tools.Bounds = new RectangleF(panel.X + labelW, y, Math.Min(400 * S, panel.Width - labelW), 32 * S);
        y += row + 6 * S;
        colorLabel.Bounds = new RectangleF(panel.X, y, labelW, row);
        colors.Bounds = new RectangleF(panel.X + labelW, y, panel.Width - labelW, row);
        y += row;
        thickness.Bounds = new RectangleF(panel.X, y, Math.Min(460 * S, panel.Width), row);
        LayoutButtons(panel, undo, save);
    }

    // Grosor en píxeles de la imagen: relativo a su tamaño para que se vea igual en fotos grandes o pequeñas.
    float ImageWidth(double value)
    {
        return (float)value * Math.Max(Original.Width, Original.Height) / 900f;
    }

    protected override Cursor PreviewCursor(PointF p)
    {
        return ImageRect.Contains(p) ? Cursors.Cross : Cursors.Default;
    }

    protected override void PreviewDown(PointF p)
    {
        if (!ImageRect.Contains(p)) return;
        current = new Mark { Tool = (Tool)tools.Index, Color = colors.Items[colors.Index].A, Width = ImageWidth(thickness.Value) };
        current.Points.Add(ToImage(p));
        if (current.Tool == Tool.Arrow || current.Tool == Tool.Rect) current.Points.Add(ToImage(p));
        marks.Add(current);
    }

    protected override void PreviewDrag(PointF p)
    {
        if (current == null) return;
        var ip = ToImage(p);
        if (current.Tool == Tool.Pen || current.Tool == Tool.Marker) current.Points.Add(ip);
        else current.Points[1] = ip;
    }

    protected override void PreviewUp(PointF p) { current = null; }

    static void DrawMarks(Graphics g, IEnumerable<Mark> list)
    {
        foreach (var m in list)
        {
            bool marker = m.Tool == Tool.Marker;
            var color = marker ? Color.FromArgb(110, m.Color) : m.Color;
            using (var pen = new Pen(color, marker ? m.Width * 3 : m.Width) { LineJoin = LineJoin.Round })
            {
                pen.StartCap = pen.EndCap = marker ? LineCap.Flat : LineCap.Round;
                switch (m.Tool)
                {
                    case Tool.Pen:
                    case Tool.Marker:
                        if (m.Points.Count > 1) g.DrawLines(pen, m.Points.ToArray());
                        else
                        {
                            using (var b = new SolidBrush(color))
                                g.FillEllipse(b, m.Points[0].X - m.Width / 2, m.Points[0].Y - m.Width / 2, m.Width, m.Width);
                        }
                        break;
                    case Tool.Arrow:
                        pen.CustomEndCap = new AdjustableArrowCap(3.5f, 3.5f, true);
                        g.DrawLine(pen, m.Points[0], m.Points[1]);
                        break;
                    case Tool.Rect:
                        var r = RectangleF.FromLTRB(Math.Min(m.Points[0].X, m.Points[1].X), Math.Min(m.Points[0].Y, m.Points[1].Y),
                                                    Math.Max(m.Points[0].X, m.Points[1].X), Math.Max(m.Points[0].Y, m.Points[1].Y));
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                        break;
                }
            }
        }
    }

    protected override void DrawPreview(Graphics g)
    {
        base.DrawPreview(g);
        var state = g.Save();
        g.SetClip(ImageRect, CombineMode.Intersect);
        using (var m = ImageToScreenMatrix()) g.MultiplyTransform(m);
        DrawMarks(g, marks);
        g.Restore(state);
    }

    void Save()
    {
        using (var result = Copy32(Original))
        {
            using (var g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawMarks(g, marks);
            }
            SaveResult(result, "anotada", SourceExt);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Tapar (negro o pixelado)
// ---------------------------------------------------------------------------------------------

class RedactWindow : EditorForm
{
    class Area
    {
        public RectangleF Rect;
        public bool Pixelate;
    }

    readonly Segmented mode;
    readonly TextWidget modeLabel, hint;
    readonly PillButton undo, save;
    readonly List<Area> areas = new List<Area>();
    Area current;
    PointF start;
    Bitmap previewCache;

    public RedactWindow(string path) : base(path, "Tapar información", 92)
    {
        mode = new Segmented { Options = new[] { "Tapar", "Pixelar" } };
        modeLabel = new TextWidget { Text = "Modo" };
        hint = new TextWidget { Text = "Arrastra sobre la imagen para tapar una zona." };
        undo = new PillButton { Text = "Deshacer", Click = () => { if (areas.Count > 0) areas.RemoveAt(areas.Count - 1); Dirty(); } };
        save = new PillButton { Text = "Guardar", Primary = true, Click = Save };
        Widgets.AddRange(new Widget[] { modeLabel, mode, hint, undo, save });
    }

    protected override void LayoutWidgets(RectangleF panel)
    {
        float labelW = 92 * S;
        modeLabel.Bounds = new RectangleF(panel.X, panel.Y, labelW, 32 * S);
        mode.Bounds = new RectangleF(panel.X + labelW, panel.Y, 220 * S, 32 * S);
        LayoutButtons(panel, undo, save);
        hint.Bounds = new RectangleF(panel.X, undo.Bounds.Y, Math.Max(0, undo.Bounds.X - panel.X - 12 * S), undo.Bounds.Height);
    }

    void Dirty()
    {
        if (previewCache != null) { previewCache.Dispose(); previewCache = null; }
        Invalidate();
    }

    protected override Cursor PreviewCursor(PointF p) { return ImageRect.Contains(p) ? Cursors.Cross : Cursors.Default; }

    protected override void PreviewDown(PointF p)
    {
        if (!ImageRect.Contains(p)) return;
        start = ToImage(p);
        current = new Area { Rect = new RectangleF(start, SizeF.Empty), Pixelate = mode.Index == 1 };
    }

    protected override void PreviewDrag(PointF p)
    {
        if (current == null) return;
        var q = ToImage(p);
        var r = RectangleF.FromLTRB(Math.Min(start.X, q.X), Math.Min(start.Y, q.Y), Math.Max(start.X, q.X), Math.Max(start.Y, q.Y));
        r.Intersect(new RectangleF(0, 0, Original.Width, Original.Height));
        current.Rect = r;
    }

    protected override void PreviewUp(PointF p)
    {
        if (current != null && current.Rect.Width > 2 && current.Rect.Height > 2) { areas.Add(current); Dirty(); }
        current = null;
    }

    // Aplica las zonas sobre una copia de `src`; `k` pasa de coordenadas de la imagen original a las de `src`.
    Bitmap Apply(Bitmap src, float k)
    {
        var result = Copy32(src);
        using (var g = Graphics.FromImage(result))
        {
            foreach (var a in areas)
            {
                var r = Rectangle.Round(new RectangleF(a.Rect.X * k, a.Rect.Y * k, a.Rect.Width * k, a.Rect.Height * k));
                r.Intersect(new Rectangle(0, 0, result.Width, result.Height));
                if (r.Width < 1 || r.Height < 1) continue;
                if (!a.Pixelate)
                {
                    g.FillRectangle(Brushes.Black, r);
                    continue;
                }
                int block = Math.Max(4, Math.Max(src.Width, src.Height) / 70);
                using (var tiny = new Bitmap(Math.Max(1, r.Width / block), Math.Max(1, r.Height / block)))
                {
                    using (var tg = Graphics.FromImage(tiny))
                    {
                        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        tg.DrawImage(src, new Rectangle(0, 0, tiny.Width, tiny.Height), r, GraphicsUnit.Pixel);
                    }
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(tiny, r);
                }
            }
        }
        return result;
    }

    protected override void DrawPreview(Graphics g)
    {
        if (previewCache == null) previewCache = Apply(PreviewBase, (float)PreviewBase.Width / Original.Width);
        DrawImageShadowed(g, previewCache, ImageRect);
        if (current != null)
        {
            var r = ToScreen(current.Rect);
            using (var b = new SolidBrush(Color.FromArgb(70, T.Accent))) g.FillRectangle(b, r);
            using (var p = new Pen(T.Accent, 1.5f * S) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        }
    }

    void Save()
    {
        using (var result = Apply(Original, 1)) SaveResult(result, "tapada", SourceExt);
    }
}

// ---------------------------------------------------------------------------------------------
// Metadatos
// ---------------------------------------------------------------------------------------------

class MetadataWindow : EditorForm
{
    readonly List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
    readonly bool hasGps;
    readonly int propertyCount;
    readonly PillButton close, strip;

    public MetadataWindow(string path) : base(path, "Metadatos", 40)
    {
        var info = new FileInfo(path);
        rows.Add(new KeyValuePair<string, string>("Archivo", info.Name));
        rows.Add(new KeyValuePair<string, string>("Tamaño", Sizes.Format(info.Length)));
        rows.Add(new KeyValuePair<string, string>("Dimensiones", Original.Width + " × " + Original.Height + " px"));

        using (var ms = new MemoryStream(File.ReadAllBytes(path)))
        using (var img = Image.FromStream(ms))
        {
            var props = img.PropertyItems.ToDictionary(p => p.Id);
            // 0x5000–0x5FFF son etiquetas internas de GDI+ (tablas de compresión, etc.), no datos de la foto.
            propertyCount = props.Keys.Count(id => id < 0x5000 || id > 0x5FFF);
            string make = Ascii(props, 0x010F), model = Ascii(props, 0x0110);
            // Muchas cámaras repiten la marca en el modelo ("Canon" + "Canon EOS R6").
            string camera = model ?? make;
            if (make != null && model != null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) camera = make + " " + model;
            Add("Cámara", camera);
            string date = Ascii(props, 0x9003) ?? Ascii(props, 0x0132);
            DateTime taken;
            if (date != null && DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
                date = taken.ToString("g", CultureInfo.CurrentCulture);
            Add("Fecha", date);
            Add("Objetivo", Ascii(props, 0xA434));
            var exposure = Rational(props, 0x829A);
            if (exposure > 0) Add("Exposición", exposure < 1 ? "1/" + Math.Round(1 / exposure) + " s" : exposure.ToString("0.#", CultureInfo.CurrentCulture) + " s");
            var f = Rational(props, 0x829D);
            if (f > 0) Add("Apertura", "f/" + f.ToString("0.#", CultureInfo.CurrentCulture));
            if (props.ContainsKey(0x8827)) Add("ISO", BitConverter.ToUInt16(props[0x8827].Value, 0).ToString());
            var focal = Rational(props, 0x920A);
            if (focal > 0) Add("Distancia focal", focal.ToString("0.#", CultureInfo.CurrentCulture) + " mm");
            Add("Software", Ascii(props, 0x0131));
            Add("Autor", Ascii(props, 0x013B));
            Add("Copyright", Ascii(props, 0x8298));

            if (props.ContainsKey(0x0002) && props.ContainsKey(0x0004))
            {
                double lat = Degrees(props[0x0002].Value), lon = Degrees(props[0x0004].Value);
                if (Ascii(props, 0x0001) == "S") lat = -lat;
                if (Ascii(props, 0x0003) == "W") lon = -lon;
                hasGps = true;
                Add("Ubicación GPS", lat.ToString("0.00000", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.00000", CultureInfo.InvariantCulture));
            }
        }
        rows.Add(new KeyValuePair<string, string>("Metadatos", propertyCount == 0 ? "Ninguno" : propertyCount + " campos"));

        close = new PillButton { Text = "Cerrar", Click = Close };
        strip = new PillButton { Text = "Guardar sin metadatos", Primary = true, Click = Strip };
        Widgets.AddRange(new Widget[] { close, strip });
    }

    void Add(string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) rows.Add(new KeyValuePair<string, string>(name, value.Trim()));
    }

    static string Ascii(Dictionary<int, PropertyItem> props, int id)
    {
        if (!props.ContainsKey(id) || props[id].Value == null) return null;
        string s = Encoding.ASCII.GetString(props[id].Value).TrimEnd('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    static double Rational(Dictionary<int, PropertyItem> props, int id)
    {
        if (!props.ContainsKey(id) || props[id].Value == null || props[id].Value.Length < 8) return 0;
        var v = props[id].Value;
        uint num = BitConverter.ToUInt32(v, 0), den = BitConverter.ToUInt32(v, 4);
        return den == 0 ? 0 : (double)num / den;
    }

    static double Degrees(byte[] v)
    {
        // Tres racionales: grados, minutos, segundos.
        Func<int, double> r = i => { uint n = BitConverter.ToUInt32(v, i * 8), d = BitConverter.ToUInt32(v, i * 8 + 4); return d == 0 ? 0 : (double)n / d; };
        return r(0) + r(1) / 60 + r(2) / 3600;
    }

    protected override void LayoutWidgets(RectangleF panel)
    {
        LayoutButtons(panel, close, strip);
    }

    protected override void DrawPreview(Graphics g)
    {
        var area = RectangleF.Inflate(PreviewArea, -24 * S, -24 * S);
        // Miniatura a la izquierda, datos a la derecha.
        var thumbArea = new RectangleF(area.X, area.Y, area.Width * 0.38f, area.Height);
        float k = Math.Min(thumbArea.Width / PreviewBase.Width, thumbArea.Height / PreviewBase.Height);
        var thumb = new RectangleF(thumbArea.X + (thumbArea.Width - PreviewBase.Width * k) / 2, thumbArea.Y + (thumbArea.Height - PreviewBase.Height * k) / 2,
                                   PreviewBase.Width * k, PreviewBase.Height * k);
        DrawImageShadowed(g, PreviewBase, thumb);

        float x = thumbArea.Right + 28 * S, w = area.Right - x, y = area.Y, rowH = 30 * S;
        if (hasGps)
        {
            var banner = new RectangleF(x, y, w, 40 * S);
            // Aviso: naranja sólido con texto blanco, para que destaque aunque la ventana sea de cristal.
            using (var path = Shapes.Rounded(banner, 10 * S))
            using (var b = new SolidBrush(T.Glass ? Color.FromArgb(230, 255, 149, 0) : Color.FromArgb(T.Dark ? 60 : 36, 255, 149, 0)))
                g.FillPath(b, path);
            var bannerText = T.Glass ? Color.White : Color.FromArgb(T.Dark ? 255 : 200, T.Dark ? 255 : 160, T.Dark ? 179 : 80, 0);
            using (var f = T.Font(12.5f, true))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                T.DrawText(g, "Esta foto revela dónde se hizo (GPS).", f, RectangleF.Inflate(banner, -12 * S, 0), sf, bannerText, false);
            y += 52 * S;
        }

        using (var fName = T.Font(13f, false))
        using (var fValue = T.Font(13f, true))
        using (var bName = new SolidBrush(T.Secondary))
        using (var bValue = new SolidBrush(T.Text))
        using (var sep = new Pen(T.Separator, 1))
        using (var near = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var far = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            foreach (var row in rows)
            {
                if (y + rowH > area.Bottom) break;
                T.DrawText(g, row.Key, fName, new RectangleF(x, y, w * 0.42f, rowH), near, T.Secondary, true);
                T.DrawText(g, row.Value, fValue, new RectangleF(x + w * 0.42f, y, w * 0.58f, rowH), far, T.Text, true);
                y += rowH;
                g.DrawLine(sep, x, y, x + w, y);
            }
        }
    }

    void Strip()
    {
        // ImageOps.Load copia solo los píxeles: el archivo nuevo sale sin EXIF, GPS ni otros datos.
        using (var clean = Copy32(Original)) SaveResult(clean, "sin metadatos", SourceExt);
    }
}

static class Sizes
{
    public static string Format(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", c) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0", c) + " MB";
        return (bytes / 1073741824.0).ToString("0.00", c) + " GB";
    }
}
