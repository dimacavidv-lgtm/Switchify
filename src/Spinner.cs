// Aviso de "convirtiendo": el logo girando donde estaba el anillo hasta que el archivo está creado, y el icono
// de la bandeja girando mientras quede alguna conversión en marcha.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

// Cuántas conversiones hay en marcha (se usa siempre desde el hilo de la interfaz).
static class Activity
{
    static int running;
    public static event Action<bool> BusyChanged;

    public static void Begin()
    {
        if (running++ == 0 && BusyChanged != null) BusyChanged(true);
    }

    public static void End()
    {
        if (running > 0 && --running == 0 && BusyChanged != null) BusyChanged(false);
    }
}

// Pequeña ventana en capas, siempre encima, que no recibe clics ni el foco.
class Spinner : Form
{
    static Bitmap logo;

    readonly Native.LayeredSurface surface;
    readonly Timer timer = new Timer { Interval = 15 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Stopwatch doneClock = new Stopwatch();
    readonly float scale;
    readonly bool dark;
    int state;                // 0 = girando, 1 = hecho, 2 = error

    public Spinner(Point centerOnScreen)
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        dark = Look.Current().Dark;
        int size = (int)(84 * scale);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(size, size);
        Location = new Point(centerOnScreen.X - size / 2, centerOnScreen.Y - size / 2);
        surface = new Native.LayeredSurface(size, size);
        timer.Tick += delegate { Draw(); };
        if (logo == null) logo = Theme.Logo(128);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x80000 | 0x20 | 0x08000000;   // herramienta, encima, en capas, sin clics, sin foco
            return cp;
        }
    }

    public void Start()
    {
        Show();
        Draw();
        timer.Start();
        Native.timeBeginPeriod(1);
    }

    public void Finish(bool ok)
    {
        if (state != 0) return;
        state = ok ? 1 : 2;
        doneClock.Restart();
    }

    void Draw()
    {
        double t = clock.Elapsed.TotalSeconds;
        double d = doneClock.Elapsed.TotalSeconds;
        // Entrada: aparece y crece en 0,18 s. Salida: tras el ✓ (0,7 s) o enseguida si hubo error, se funde en 0,2 s.
        double appear = Ease(Math.Min(1, t / 0.18));
        double leaveStart = state == 1 ? 0.7 : 0;
        double leave = state == 0 ? 0 : Math.Max(0, Math.Min(1, (d - leaveStart) / 0.2));
        if (leave >= 1) { Close(); return; }
        double opacity = appear * (1 - leave);

        int size = ClientSize.Width;
        float c = size / 2f;
        using (var g = Graphics.FromImage(surface.Bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float k = (float)(0.7 + 0.3 * appear);
            g.TranslateTransform(c, c);
            g.ScaleTransform(k, k);

            // Placa redonda con sombra suave, del color del tema.
            float plate = 30 * scale;
            for (int i = 8; i > 0; i--)
                using (var b = new SolidBrush(Color.FromArgb((int)(10 * (1 - i / 9.0)), 0, 0, 0)))
                    g.FillEllipse(b, -plate - i * scale, -plate - i * scale + 3 * scale, (plate + i * scale) * 2, (plate + i * scale) * 2);
            using (var b = new SolidBrush(dark ? Color.FromArgb(235, 40, 40, 44) : Color.FromArgb(240, 255, 255, 255)))
                g.FillEllipse(b, -plate, -plate, plate * 2, plate * 2);
            using (var p = new Pen(dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0), 1f * scale))
                g.DrawEllipse(p, -plate, -plate, plate * 2, plate * 2);

            // El logo gira (más rápido al principio de cada vuelta, como una rueda de carga); con el ✓ se va encogiendo.
            double check = state == 1 ? Ease(Math.Min(1, d / 0.25)) : 0;
            float logoSize = 40 * scale * (float)(1 - check);
            if (logoSize > 0.5f)
            {
                double turn = t / 0.9;
                double angle = (Math.Floor(turn) + Ease(turn - Math.Floor(turn))) * 360;
                var state0 = g.Save();
                g.RotateTransform((float)angle);
                if (logo != null) g.DrawImage(logo, -logoSize / 2, -logoSize / 2, logoSize, logoSize);
                else
                    using (var p = new Pen(Color.FromArgb(0, 122, 255), 3.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(p, -logoSize / 2, -logoSize / 2, logoSize, logoSize, 0, 270);
                g.Restore(state0);
            }

            // Hecho: círculo verde con ✓ que aparece desde el centro.
            if (check > 0)
            {
                float r = 22 * scale * (float)check;
                using (var b = new SolidBrush(Color.FromArgb(52, 199, 89))) g.FillEllipse(b, -r, -r, r * 2, r * 2);
                using (var p = new Pen(Color.White, 3.2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new PointF(-9 * scale * (float)check, 0), new PointF(-3 * scale * (float)check, 6 * scale * (float)check), new PointF(9 * scale * (float)check, -7 * scale * (float)check) });
            }
        }
        surface.Present(this, (byte)Math.Max(1, Math.Round(255 * opacity)));
    }

    static double Ease(double x) { return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (timer.Enabled) Native.timeEndPeriod(1);
            timer.Dispose();
            surface.Dispose();
        }
        base.Dispose(disposing);
    }
}

// El icono de la bandeja girando: fotogramas ya hechos del logo en 12 ángulos.
class TraySpinner
{
    readonly NotifyIcon tray;
    readonly Icon idle;
    readonly Timer timer = new Timer { Interval = 70 };
    readonly List<Icon> frames = new List<Icon>();
    int index;

    public TraySpinner(NotifyIcon tray)
    {
        this.tray = tray;
        idle = tray.Icon;
        timer.Tick += delegate
        {
            if (frames.Count == 0) BuildFrames();
            if (frames.Count == 0) return;
            index = (index + 1) % frames.Count;
            tray.Icon = frames[index];
        };
        Activity.BusyChanged += busy =>
        {
            if (busy) { index = 0; timer.Start(); }
            else { timer.Stop(); tray.Icon = idle; }
        };
    }

    void BuildFrames()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using (var logo = Theme.Logo(Math.Max(64, size * 2)))
        {
            if (logo == null) return;
            for (int i = 0; i < 12; i++)
            {
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TranslateTransform(size / 2f, size / 2f);
                        g.RotateTransform(i * 30);
                        g.DrawImage(logo, -size / 2f, -size / 2f, size, size);
                    }
                    frames.Add(Icon.FromHandle(bmp.GetHicon()));
                }
            }
        }
    }
}
