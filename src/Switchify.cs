// Switchify: menú radial para convertir archivos en Windows.
//  - Arrastra un archivo y pulsa Shift: aparece el menú bajo el cursor; suéltalo sobre una opción.
//  - O selecciona archivos en el Explorador y pulsa Ctrl+Shift+Espacio; haz clic en una opción.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Switchify_SingleInstance", out created))
        {
            if (!created) return;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}

static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_HOTKEY = 0x312;
    public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_SPACE = 0x20;
    public const int MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    // Muestra un bitmap ARGB como contenido de una ventana WS_EX_LAYERED (transparencia por píxel).
    public static void ApplyLayeredBitmap(Form form, Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE { CX = bmp.Width, CY = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = form.Left, Y = form.Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(form.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static bool LeftButtonDown()
    {
        // Respeta el intercambio de botones del ratón para zurdos.
        int vk = SystemInformation.MouseButtonsSwapped ? 0x02 : VK_LBUTTON;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}

// ---------------------------------------------------------------------------------------------
// Bandeja del sistema, atajo de teclado y detección de "arrastrar + Shift"
// ---------------------------------------------------------------------------------------------

class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon tray;
    readonly RadialMenu menu;
    readonly HotkeyWindow hotkey;
    readonly Native.LowLevelKeyboardProc hookProc; // referencia viva para que el GC no la recoja
    IntPtr hookHandle;
    bool shiftHeld;

    public TrayContext()
    {
        menu = new RadialMenu();
        menu.ActionFinished += ShowResult;
        var forceHandle = menu.Handle; // necesario para BeginInvoke desde el hook

        var startup = new ToolStripMenuItem("Iniciar con Windows") { Checked = IsStartupEnabled() };
        startup.Click += delegate { SetStartup(!startup.Checked); startup.Checked = IsStartupEnabled(); };

        var strip = new ContextMenuStrip();
        strip.Items.Add(new ToolStripMenuItem("Switchify") { Enabled = false });
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(startup);
        strip.Items.Add("Cómo se usa", null, delegate { ShowHelp(); });
        strip.Items.Add("Salir", null, delegate { ExitThread(); });

        tray = new NotifyIcon
        {
            Icon = Theme.MakeTrayIcon(),
            Text = "Switchify – arrastra + Shift o Ctrl+Shift+Espacio",
            ContextMenuStrip = strip,
            Visible = true
        };
        tray.DoubleClick += delegate { ShowHelp(); };

        hotkey = new HotkeyWindow(OnHotkey);
        if (!hotkey.Register(Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_SPACE))
            tray.ShowBalloonTip(4000, "Switchify", "Ctrl+Shift+Espacio ya está en uso por otro programa. El modo arrastrar + Shift sigue funcionando.", ToolTipIcon.Warning);

        hookProc = KeyboardHook;
        hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);

        tray.ShowBalloonTip(3000, "Switchify está activo", "Arrastra un archivo y pulsa Shift, o selecciona archivos y pulsa Ctrl+Shift+Espacio.", ToolTipIcon.Info);
    }

    IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            bool isShift = vk == Native.VK_LSHIFT || vk == Native.VK_RSHIFT || vk == Native.VK_SHIFT;
            if (isShift && (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN))
            {
                // Solo en la pulsación inicial (no en la auto-repetición) y con el botón izquierdo apretado.
                if (!shiftHeld && Native.LeftButtonDown())
                    menu.BeginInvoke(new Action(menu.ShowForDrag));
                shiftHeld = true;
            }
            else if (isShift && (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP))
            {
                shiftHeld = false;
            }
        }
        return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    void OnHotkey()
    {
        var files = ExplorerSelection.Get(Native.GetForegroundWindow());
        if (files.Count == 0)
        {
            tray.ShowBalloonTip(2500, "Switchify", "Selecciona uno o más archivos en el Explorador antes de pulsar Ctrl+Shift+Espacio.", ToolTipIcon.Info);
            return;
        }
        menu.ShowForFiles(files);
    }

    void ShowResult(string title, string text, bool error)
    {
        tray.ShowBalloonTip(3000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
    }

    void ShowHelp()
    {
        MessageBox.Show(
            "1) Arrastra un archivo (desde el Escritorio o el Explorador) y, sin soltarlo, pulsa Shift.\n" +
            "   Aparece el menú circular: suelta el archivo sobre la opción que quieras.\n\n" +
            "2) O selecciona archivos en el Explorador y pulsa Ctrl+Shift+Espacio, luego haz clic en una opción.\n\n" +
            "El resultado se guarda junto al archivo original (nunca se sobrescribe).\n" +
            "Suelta en el centro o pulsa Esc para cancelar.",
            "Switchify", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static bool IsStartupEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue("Switchify") != null;
    }

    static void SetStartup(bool enable)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enable) key.SetValue("Switchify", "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue("Switchify", false);
        }
    }

    protected override void ExitThreadCore()
    {
        if (hookHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(hookHandle);
        hotkey.Dispose();
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

class HotkeyWindow : NativeWindow, IDisposable
{
    readonly Action callback;
    bool registered;

    public HotkeyWindow(Action callback)
    {
        this.callback = callback;
        CreateHandle(new CreateParams());
    }

    public bool Register(int modifiers, int vk)
    {
        registered = Native.RegisterHotKey(Handle, 1, modifiers, vk);
        return registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) callback();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registered) Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

static class ExplorerSelection
{
    // Devuelve los archivos seleccionados en la ventana del Explorador que está en primer plano.
    public static List<string> Get(IntPtr foreground)
    {
        var result = new List<string>();
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType);
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)window.HWND) != foreground) continue;
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        string path = item.Path;
                        if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)) && !result.Contains(path))
                            result.Add(path);
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            // Suelta las referencias COM al Explorador en lugar de esperar al recolector.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return result;
    }
}

// ---------------------------------------------------------------------------------------------
// Menú radial
// ---------------------------------------------------------------------------------------------

// Apariencia del sistema: claro/oscuro, transparencias y animaciones según la configuración de Windows.
class Look
{
    public bool Dark, Solid, Motion;
    public Color Tint, Center, Text, Secondary, Separator, Accent, AccentPressed, OnAccent, Edge;

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, out bool value, int winIni);

    public static Look Current()
    {
        var look = new Look
        {
            Dark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0,
            Solid = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 0,
            Motion = true
        };
        bool animations;
        if (SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out animations, 0)) look.Motion = animations;

        if (look.Dark)
        {
            look.Tint = Color.FromArgb(look.Solid ? 250 : 85, 30, 30, 32);
            look.Center = Color.FromArgb(look.Solid ? 255 : 150, 44, 44, 46);
            look.Text = Color.FromArgb(235, 255, 255, 255);
            look.Secondary = Color.FromArgb(150, 235, 235, 245);
            look.Separator = Color.FromArgb(30, 255, 255, 255);
            look.Accent = Color.FromArgb(10, 132, 255);
            look.AccentPressed = Color.FromArgb(0, 104, 214);
            look.Edge = Color.FromArgb(45, 255, 255, 255);
        }
        else
        {
            look.Tint = Color.FromArgb(look.Solid ? 250 : 60, 250, 250, 252);
            look.Center = Color.FromArgb(look.Solid ? 255 : 140, 255, 255, 255);
            look.Text = Color.FromArgb(220, 0, 0, 0);
            look.Secondary = Color.FromArgb(150, 60, 60, 67);
            look.Separator = Color.FromArgb(40, 60, 60, 67);
            look.Accent = Color.FromArgb(0, 122, 255);
            look.AccentPressed = Color.FromArgb(0, 96, 210);
            look.Edge = Color.FromArgb(140, 255, 255, 255);
        }
        look.OnAccent = Color.White;
        return look;
    }

    static int ReadDword(string key, string name, int fallback)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v : fallback;
            }
        }
        catch { return fallback; }
    }
}

// Muelle con los parámetros de Apple: amortiguación (1 = sin rebote) y respuesta (segundos).
// Siempre parte del valor actual, así que cualquier animación se puede interrumpir y redirigir.
class Spring
{
    public double Value, Velocity, Target;
    readonly double stiffness, damping;

    public Spring(double dampingRatio, double response)
    {
        stiffness = Math.Pow(2 * Math.PI / response, 2);
        damping = 4 * Math.PI * dampingRatio / response;
    }

    public void Step(double dt)
    {
        // Subpasos de 2 ms: integración estable aunque el temporizador vaya irregular.
        for (double t = 0; t < dt; t += 0.002)
        {
            double h = Math.Min(0.002, dt - t);
            Velocity += (-stiffness * (Value - Target) - damping * Velocity) * h;
            Value += Velocity * h;
        }
    }

    public bool Settled { get { return Math.Abs(Value - Target) < 0.002 && Math.Abs(Velocity) < 0.02; } }

    public void Snap(double v)
    {
        Value = Target = v;
        Velocity = 0;
    }
}

static class Theme
{
    public static Icon MakeTrayIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(64, 156, 255), Color.FromArgb(0, 102, 230), 90f))
                g.FillEllipse(brush, 1, 1, 30, 30);
            using (var pen = new Pen(Color.White, 2.2f))
            {
                // Seis segmentos del anillo, como el menú.
                for (int i = 0; i < 6; i++)
                    g.DrawArc(pen, 7, 7, 18, 18, -90 + i * 60 + 8, 44);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

class RadialMenu : Form
{
    public event Action<string, string, bool> ActionFinished;

    static readonly string[] FontNames = { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" };
    static readonly string[] TextFontNames = { "Segoe UI Variable Text", "Segoe UI" };

    readonly float scale;
    readonly int outerR, innerR, pad, center;
    readonly System.Windows.Forms.Timer releaseTimer, frameTimer;
    readonly Stopwatch clock = new Stopwatch();
    readonly Spring appear = new Spring(1.0, 0.32);   // entrada/salida: sin rebote
    readonly FontFamily labelFont, textFont;
    Spring[] hovers = new Spring[0];                    // resaltado de cada opción
    List<FileAction> actions = new List<FileAction>();
    List<string> files = new List<string>();
    Look look = Look.Current();
    Bitmap backdrop;                                    // lo que hay detrás, ya desenfocado
    int hover = -1, pressed = -1;
    bool dragMode, revealed, closing;

    public RadialMenu()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        outerR = (int)(170 * scale);
        innerR = (int)(62 * scale);
        pad = (int)(28 * scale);           // margen para la sombra
        center = outerR + pad;
        labelFont = PickFont(FontNames);
        textFont = PickFont(TextFontNames);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AllowDrop = true;
        KeyPreview = true;
        ClientSize = new Size(center * 2, center * 2);

        releaseTimer = new System.Windows.Forms.Timer { Interval = 30 };
        releaseTimer.Tick += delegate
        {
            // En modo arrastre el menú vive mientras el botón siga apretado.
            if (dragMode && !Native.LeftButtonDown()) HideMenu();
        };

        frameTimer = new System.Windows.Forms.Timer { Interval = 10 };
        frameTimer.Tick += delegate { Animate(); };
    }

    static FontFamily PickFont(string[] names)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToList();
        string name = names.FirstOrDefault(installed.Contains) ?? "Segoe UI";
        return new FontFamily(name);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW: fuera de Alt+Tab
            cp.ExStyle |= 0x8;        // WS_EX_TOPMOST
            cp.ExStyle |= 0x80000;    // WS_EX_LAYERED: transparencia por píxel (UpdateLayeredWindow)
            return cp;
        }
    }

    // --- Mostrar / ocultar ----------------------------------------------------------------

    void PlaceAtCursor()
    {
        var p = Cursor.Position;
        var area = Screen.FromPoint(p).WorkingArea;
        int x = Math.Max(area.Left - pad, Math.Min(p.X - center, area.Right - Width + pad));
        int y = Math.Max(area.Top - pad, Math.Min(p.Y - center, area.Bottom - Height + pad));
        Location = new Point(x, y);
    }

    void Prepare(List<string> selected)
    {
        look = Look.Current();
        closing = false;
        pressed = -1;
        hover = -1;
        files = selected;
        SetActions(FileActions.For(files));
        PlaceAtCursor();
        // El fondo se captura antes de mostrar la ventana (las ventanas en capas no salen en la captura).
        if (backdrop != null) backdrop.Dispose();
        backdrop = look.Solid ? null : CaptureBackdrop();
        appear.Snap(0);
    }

    public void ShowForDrag()
    {
        if (Visible && !closing) return;
        dragMode = true;
        Prepare(new List<string>());
        // Invisible hasta que Windows confirme que se está arrastrando un archivo (DragEnter).
        // Así no aparece si Shift se pulsa durante otra cosa (p. ej. seleccionar texto).
        revealed = false;
        Show();
        Render();
        releaseTimer.Start();
    }

    public void ShowForFiles(List<string> selected)
    {
        dragMode = false;
        Prepare(selected);
        revealed = true;
        Show();
        Reveal();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    void Reveal()
    {
        revealed = true;
        appear.Target = 1;
        StartAnimation();
    }

    void HideMenu()
    {
        releaseTimer.Stop();
        dragMode = false;
        if (!Visible || closing) return;
        if (!revealed) { Finish(); return; }
        // Sale por el mismo camino por el que entró: se encoge hacia el cursor.
        closing = true;
        appear.Target = 0;
        StartAnimation();
    }

    void Finish()
    {
        frameTimer.Stop();
        closing = false;
        revealed = false;
        Hide();
        if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!dragMode) HideMenu();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HideMenu();
        base.OnKeyDown(e);
    }

    // --- Animación -----------------------------------------------------------------------

    void StartAnimation()
    {
        if (frameTimer.Enabled) return;
        clock.Restart();
        frameTimer.Start();
    }

    void Animate()
    {
        double dt = Math.Min(0.05, clock.Elapsed.TotalSeconds);
        clock.Restart();
        appear.Step(dt);
        foreach (var h in hovers) h.Step(dt);
        Render();

        if (closing && appear.Value < 0.02) { Finish(); return; }
        if (appear.Settled && hovers.All(h => h.Settled)) frameTimer.Stop();
    }

    void SetActions(List<FileAction> list)
    {
        actions = list;
        hovers = list.Select(a => new Spring(1.0, 0.18)).ToArray();
    }

    void SetHover(int index)
    {
        // Al cerrar, la opción elegida se queda resaltada mientras el menú se va.
        if (closing || index == hover) return;
        hover = index;
        if (pressed != index) pressed = -1;
        for (int i = 0; i < hovers.Length; i++) hovers[i].Target = i == index ? 1 : 0;
        StartAnimation();
    }

    // --- Arrastrar y soltar --------------------------------------------------------------

    protected override void OnDragEnter(DragEventArgs e)
    {
        var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (dropped == null || dropped.Length == 0) { e.Effect = DragDropEffects.None; return; }
        if (!revealed || closing)
        {
            files = dropped.ToList();
            SetActions(FileActions.For(files));
            closing = false;
            Reveal();
        }
        UpdateDrag(e);
    }

    protected override void OnDragOver(DragEventArgs e) { UpdateDrag(e); }

    protected override void OnDragLeave(EventArgs e) { SetHover(-1); }

    void UpdateDrag(DragEventArgs e)
    {
        SetHover(HitTest(PointToClient(new Point(e.X, e.Y))));
        // Siempre "Copiar": con Shift el Explorador propondría "Mover" y borraría el original.
        e.Effect = hover >= 0 && (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        int chosen = HitTest(PointToClient(new Point(e.X, e.Y)));
        var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
        HideMenu();
        if (chosen >= 0 && dropped != null) Execute(actions[chosen], dropped.ToList());
    }

    // --- Ratón (modo atajo de teclado) ------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragMode) SetHover(HitTest(e.Location));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // La respuesta llega al pulsar, no al soltar.
        if (dragMode || e.Button != MouseButtons.Left || closing) return;
        pressed = HitTest(e.Location);
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragMode || e.Button != MouseButtons.Left || closing) return;
        int chosen = HitTest(e.Location);
        var selected = files;
        if (chosen >= 0 && chosen != pressed) { pressed = -1; Render(); return; } // soltó en otra opción: se cancela
        pressed = -1;
        HideMenu();
        if (chosen >= 0) Execute(actions[chosen], selected);
    }

    // --- Ejecución ---------------------------------------------------------------------------

    void Execute(FileAction action, List<string> targets)
    {
        Task.Factory.StartNew(() =>
        {
            var outputs = new List<string>();
            var errors = new List<string>();
            foreach (var input in action.Batch ? new List<string> { null } : targets)
            {
                try
                {
                    if (action.Batch) outputs.Add(action.RunBatch(targets));
                    else outputs.Add(action.Run(input));
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(input ?? "") + ": " + ex.Message);
                }
            }
            // Libera ya los objetos de Windows (PDF, OCR) que pudieran seguir referenciando archivos.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            BeginInvoke(new Action(() =>
            {
                if (ActionFinished == null) return;
                if (errors.Count > 0)
                    ActionFinished("Switchify – error", string.Join("\n", errors.Take(3)), true);
                else
                    ActionFinished(action.Description + " ✓",
                        outputs.Count == 1 ? Path.GetFileName(outputs[0]) : outputs.Count + " archivos creados", false);
            }));
        });
    }

    // --- Geometría -----------------------------------------------------------------------

    int HitTest(Point p)
    {
        if (actions.Count == 0) return -1;
        double dx = p.X - center, dy = p.Y - center;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < innerR || dist > outerR) return -1;
        double sweep = 360.0 / actions.Count;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        double rel = ((angle - (-90 - sweep / 2)) % 360 + 360) % 360;
        return Math.Min(actions.Count - 1, (int)(rel / sweep));
    }

    // --- Material: captura del fondo desenfocada y saturada --------------------------------

    Bitmap CaptureBackdrop()
    {
        try
        {
            using (var shot = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(Location, Point.Empty, Size);
                return Blur(shot);
            }
        }
        catch { return null; }
    }

    static Bitmap Blur(Bitmap src)
    {
        // Desenfoque barato y suave: reducir mucho y volver a ampliar con bicúbico.
        // Se satura un poco (como el "vibrancy" de Apple) para que el color de detrás se note a través del cristal.
        const float s = 1.6f;
        float[] lum = { 0.2126f, 0.7152f, 0.0722f };
        var m = new float[5][];
        for (int r = 0; r < 3; r++)
        {
            m[r] = new float[5];
            for (int c = 0; c < 3; c++) m[r][c] = (1 - s) * lum[r] + (r == c ? s : 0);
        }
        m[3] = new float[] { 0, 0, 0, 1, 0 };
        m[4] = new float[] { 0, 0, 0, 0, 1 };

        using (var attrs = new ImageAttributes())
        using (var mid = new Bitmap(Math.Max(1, src.Width / 4), Math.Max(1, src.Height / 4)))
        using (var tiny = new Bitmap(Math.Max(1, src.Width / 40), Math.Max(1, src.Height / 40)))
        using (var soft = new Bitmap(Math.Max(1, src.Width / 4), Math.Max(1, src.Height / 4)))
        {
            attrs.SetColorMatrix(new ColorMatrix(m));
            attrs.SetWrapMode(WrapMode.TileFlipXY); // evita bordes oscuros al escalar
            using (var g = Graphics.FromImage(mid))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, mid.Width, mid.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            using (var plain = new ImageAttributes())
            {
                plain.SetWrapMode(WrapMode.TileFlipXY);
                using (var g = Graphics.FromImage(tiny))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(mid, new Rectangle(0, 0, tiny.Width, tiny.Height), 0, 0, mid.Width, mid.Height, GraphicsUnit.Pixel, plain);
                }
                // Se amplía en dos pasos: de golpe quedarían manchas cuadradas.
                using (var g = Graphics.FromImage(soft))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(tiny, new Rectangle(0, 0, soft.Width, soft.Height), 0, 0, tiny.Width, tiny.Height, GraphicsUnit.Pixel, plain);
                }
                var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(soft, new Rectangle(0, 0, result.Width, result.Height), 0, 0, soft.Width, soft.Height, GraphicsUnit.Pixel, plain);
                }
                return result;
            }
        }
    }

    // --- Dibujo --------------------------------------------------------------------------

    // Dibuja el menú en un bitmap ARGB y lo pasa a Windows con UpdateLayeredWindow,
    // así cada píxel puede ser semitransparente.
    void Render()
    {
        if (!IsHandleCreated) return;
        using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                // Disco casi invisible a tamaño completo: mantiene el arrastre "enganchado" a la ventana
                // aunque el menú visible esté a mitad de su animación de entrada.
                using (var b = new SolidBrush(Color.FromArgb(1, 255, 255, 255)))
                    g.FillEllipse(b, pad, pad, outerR * 2, outerR * 2);

                if (revealed)
                {
                    float a = (float)Math.Max(0, Math.Min(1, appear.Value));
                    using (var scene = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
                    {
                        using (var sg = Graphics.FromImage(scene)) Draw(sg);
                        // Se materializa desde el cursor: escala + opacidad. Con animaciones desactivadas, solo fundido.
                        float k = look.Motion ? 0.86f + 0.14f * a : 1f;
                        g.TranslateTransform(center, center);
                        g.ScaleTransform(k, k);
                        g.TranslateTransform(-center, -center);
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        using (var attrs = new ImageAttributes())
                        {
                            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                            g.DrawImage(scene, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attrs);
                        }
                    }
                }
            }
            Native.ApplyLayeredBitmap(this, bmp);
        }
    }

    void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var outer = new RectangleF(pad, pad, outerR * 2, outerR * 2);
        var inner = new RectangleF(center - innerR, center - innerR, innerR * 2, innerR * 2);

        // Sombra amplia y suave, algo desplazada hacia abajo: una superficie grande "flota" más.
        int shadowSteps = pad - 2;
        for (int i = shadowSteps; i > 0; i--)
        {
            double t = 1 - (double)i / shadowSteps;
            int alpha = (int)((look.Dark ? 70 : 38) * t * t / 6);
            using (var b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                g.FillEllipse(b, RectangleF.Inflate(new RectangleF(outer.X, outer.Y + 6 * scale, outer.Width, outer.Height), i, i));
        }

        // Material: fondo desenfocado + tinte translúcido.
        using (var disk = new GraphicsPath())
        {
            disk.AddEllipse(outer);
            var oldClip = g.Clip;
            g.SetClip(disk);
            if (backdrop != null) g.DrawImage(backdrop, 0, 0, Width, Height);
            using (var tint = new SolidBrush(look.Tint)) g.FillPath(tint, disk);
            g.Clip = oldClip;
        }

        int n = actions.Count;
        float sweep = n > 0 ? 360f / n : 360f;

        // Separadores finos, sin llegar a los bordes.
        if (n > 1)
        {
            using (var sep = new Pen(look.Separator, 1f * scale))
            {
                for (int i = 0; i < n; i++)
                {
                    double a = (-90 - sweep / 2 + i * sweep) * Math.PI / 180;
                    float r0 = innerR + 14 * scale, r1 = outerR - 14 * scale;
                    g.DrawLine(sep, center + (float)Math.Cos(a) * r0, center + (float)Math.Sin(a) * r0,
                                    center + (float)Math.Cos(a) * r1, center + (float)Math.Sin(a) * r1);
                }
            }
        }

        // Opción resaltada: una pieza sólida de color de acento, con esquinas redondeadas,
        // que se adelanta un poco hacia el cursor.
        for (int i = 0; i < n; i++)
        {
            float hv = (float)Math.Max(0, Math.Min(1, hovers[i].Value));
            if (hv < 0.01f) continue;
            float start = -90 - sweep / 2 + i * sweep;
            double mid = (start + sweep / 2) * Math.PI / 180;
            float push = 3 * scale * hv;
            using (var path = SegmentPath(start, sweep, (float)Math.Cos(mid) * push, (float)Math.Sin(mid) * push))
            {
                Color c = i == pressed ? look.AccentPressed : look.Accent;
                var fill = Color.FromArgb((int)(255 * hv), c);
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var round = new Pen(fill, 8 * scale) { LineJoin = LineJoin.Round }) g.DrawPath(round, path);
            }
        }

        // Etiquetas.
        float labelSize = (n > 8 ? 12.5f : 14f) * scale;
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            for (int i = 0; i < n; i++)
            {
                float hv = (float)Math.Max(0, Math.Min(1, hovers[i].Value));
                float start = -90 - sweep / 2 + i * sweep;
                double mid = (start + sweep / 2) * Math.PI / 180;
                float r = (outerR + innerR) / 2f + 3 * scale * hv;
                var c = new PointF(center + (float)Math.Cos(mid) * r, center + (float)Math.Sin(mid) * r);
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(Lerp(look.Text, look.OnAccent, hv)))
                {
                    var box = new RectangleF(c.X - 60 * scale, c.Y - 16 * scale, 120 * scale, 32 * scale);
                    path.AddString(actions[i].Label, labelFont, (int)FontStyle.Regular, labelSize, box, sf);
                    g.FillPath(brush, path);
                }
            }
        }

        // Borde de luz: una línea clara donde el cristal "atrapa" la luz.
        using (var edge = new Pen(look.Edge, 1f * scale)) g.DrawEllipse(edge, RectangleF.Inflate(outer, -0.5f * scale, -0.5f * scale));

        // Centro: superficie más opaca con la jerarquía de texto (principal + secundario).
        using (var cb = new SolidBrush(look.Center)) g.FillEllipse(cb, inner);
        using (var edge = new Pen(look.Separator, 1f * scale)) g.DrawEllipse(edge, inner);

        string name = files.Count == 1 ? Shorten(Path.GetFileName(files[0])) : files.Count + " archivos";
        string primary, secondary;
        if (n == 0) { primary = "Switchify"; secondary = ""; }
        else if (hover >= 0) { primary = actions[hover].Description; secondary = name; }
        else { primary = name; secondary = dragMode ? "Suelta en una opción" : "Elige una opción"; }

        var textBox = RectangleF.Inflate(inner, -12 * scale, -14 * scale);
        using (var f1 = new Font(labelFont, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var f2 = new Font(textFont, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var b1 = new SolidBrush(look.Text))
        using (var b2 = new SolidBrush(look.Secondary))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter })
        using (var sf2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter })
        {
            float gap = 2 * scale;
            var top = new RectangleF(textBox.X, textBox.Y, textBox.Width, textBox.Height / 2 + 6 * scale - gap);
            var bottom = new RectangleF(textBox.X, textBox.Y + textBox.Height / 2 + 6 * scale + gap, textBox.Width, textBox.Height / 2 - 6 * scale);
            if (secondary.Length == 0) top = textBox;
            g.DrawString(primary, f1, b1, top, secondary.Length == 0 ? new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center } : sf);
            if (secondary.Length > 0) g.DrawString(secondary, f2, b2, bottom, sf2);
        }
    }

    // Pieza del anillo, recortada hacia dentro para que el trazo redondeado no se salga del borde.
    GraphicsPath SegmentPath(float start, float sweep, float dx, float dy)
    {
        float inset = 9 * scale;
        var o = new RectangleF(pad + inset + dx, pad + inset + dy, (outerR - inset) * 2, (outerR - inset) * 2);
        var ir = innerR + inset;
        var i = new RectangleF(center - ir + dx, center - ir + dy, ir * 2, ir * 2);
        float angOuter = (float)(inset / (outerR - inset) * 180 / Math.PI);
        float angInner = (float)(inset / ir * 180 / Math.PI);
        var path = new GraphicsPath();
        if (sweep >= 359)
        {
            path.AddEllipse(o);
            path.AddEllipse(i);
            return path;
        }
        path.AddArc(o, start + angOuter, sweep - 2 * angOuter);
        path.AddArc(i, start + sweep - angInner, -(sweep - 2 * angInner));
        path.CloseFigure();
        return path;
    }

    static Color Lerp(Color a, Color b, float t)
    {
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static string Shorten(string name)
    {
        return name.Length <= 22 ? name : name.Substring(0, 10) + "…" + name.Substring(name.Length - 10);
    }
}

// ---------------------------------------------------------------------------------------------
// Acciones sobre archivos
// ---------------------------------------------------------------------------------------------

class FileAction
{
    public string Label;
    public string Description;
    public Func<string, string> Run;               // un archivo -> ruta de salida
    public Func<List<string>, string> RunBatch;    // todos los archivos a la vez -> ruta de salida
    public bool Batch { get { return RunBatch != null; } }
}

static class FileActions
{
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" };

    static readonly string[][] Formats =
    {
        new[] { "PNG", ".png" }, new[] { "JPG", ".jpg" }, new[] { "BMP", ".bmp" },
        new[] { "GIF", ".gif" }, new[] { "TIFF", ".tiff" }, new[] { "ICO", ".ico" }
    };

    static readonly string[][] DocFormats =
    {
        new[] { "PDF", ".pdf" }, new[] { "DOCX", ".docx" }, new[] { "MD", ".md" },
        new[] { "HTML", ".html" }, new[] { "TXT", ".txt" }
    };

    static bool IsImage(string path)
    {
        return File.Exists(path) && ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    static string NormalizeExt(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext == ".jpeg" ? ".jpg" : ext == ".tif" ? ".tiff" : ext;
    }

    public static List<FileAction> For(List<string> files)
    {
        var list = new List<FileAction>();

        if (files.All(IsImage))
        {
            var exts = files.Select(f => NormalizeExt(Path.GetExtension(f))).Distinct().ToList();
            foreach (var fmt in Formats)
            {
                if (exts.Count == 1 && exts[0] == fmt[1]) continue;
                string ext = fmt[1];
                list.Add(new FileAction
                {
                    Label = fmt[0],
                    Description = "Convertir a " + fmt[0],
                    Run = f => ImageOps.Convert(f, ext)
                });
            }
            list.Add(new FileAction { Label = "Comprimir", Description = "Comprimir (JPG 70 %)", Run = ImageOps.Compress });
            list.Add(new FileAction { Label = "50 %", Description = "Reducir a la mitad", Run = f => ImageOps.Resize(f, 0.5) });
            list.Add(new FileAction { Label = "Girar 90°", Description = "Girar 90°", Run = ImageOps.Rotate });
            list.Add(new FileAction { Label = "PDF", Description = files.Count == 1 ? "Convertir a PDF" : "Unir en un PDF", RunBatch = PdfWriter.FromImages });
            list.Add(new FileAction { Label = "Texto", Description = "Extraer texto (OCR)", Run = Documents.ImageToText });
        }
        else if (files.All(f => File.Exists(f) && Path.GetExtension(f).ToLowerInvariant() == ".pdf"))
        {
            list.Add(new FileAction { Label = "PNG", Description = "Páginas a PNG", Run = f => Pdf.ToImages(f, ".png") });
            list.Add(new FileAction { Label = "JPG", Description = "Páginas a JPG", Run = f => Pdf.ToImages(f, ".jpg") });
            list.Add(new FileAction { Label = "DOCX", Description = "Texto a Word (OCR)", Run = f => Documents.FromPdf(f, ".docx") });
            list.Add(new FileAction { Label = "TXT", Description = "Extraer texto (OCR)", Run = f => Documents.FromPdf(f, ".txt") });
            list.Add(new FileAction { Label = "MD", Description = "Texto a Markdown (OCR)", Run = f => Documents.FromPdf(f, ".md") });
        }
        else if (files.All(f => File.Exists(f) && Documents.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            var exts = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().ToList();
            bool html = exts.All(e => e == ".html" || e == ".htm");
            foreach (var fmt in DocFormats)
            {
                string ext = fmt[1];
                if (exts.Count == 1 && (exts[0] == ext || (ext == ".md" && exts[0] == ".markdown"))) continue;
                if (html && ext != ".pdf") continue;
                if (exts.Any(e => e == ".html" || e == ".htm") && !html) continue; // HTML mezclado con otros: solo ZIP
                list.Add(new FileAction { Label = fmt[0], Description = "Convertir a " + fmt[0], Run = f => Documents.Convert(f, ext) });
            }
        }

        if (files.All(f => File.Exists(f) && Path.GetExtension(f).ToLowerInvariant() == ".zip"))
            list.Add(new FileAction { Label = "Extraer", Description = "Extraer ZIP", Run = ArchiveOps.Extract });
        else
            list.Add(new FileAction { Label = "ZIP", Description = "Comprimir en ZIP", RunBatch = ArchiveOps.Zip });

        return list;
    }
}

static class Paths
{
    // Ruta libre junto al original: "foto.png", "foto (1).png", ...
    public static string Unique(string dir, string name, string ext)
    {
        string path = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(dir, name + " (" + i + ")" + ext);
        return path;
    }

    public static string Beside(string input, string suffix, string ext)
    {
        return Unique(Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input) + suffix, ext);
    }
}

static class ImageOps
{
    public static Bitmap Load(string path)
    {
        // Se lee a memoria para no bloquear el archivo original.
        using (var ms = new MemoryStream(File.ReadAllBytes(path)))
        using (var img = Image.FromStream(ms))
        {
            var bmp = new Bitmap(img);
            ApplyExifOrientation(img, bmp);
            return bmp;
        }
    }

    static void ApplyExifOrientation(Image source, Bitmap bmp)
    {
        if (!source.PropertyIdList.Contains(0x0112)) return;
        switch (BitConverter.ToUInt16(source.GetPropertyItem(0x0112).Value, 0))
        {
            case 2: bmp.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
            case 3: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 4: bmp.RotateFlip(RotateFlipType.Rotate180FlipX); break;
            case 5: bmp.RotateFlip(RotateFlipType.Rotate90FlipX); break;
            case 6: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 7: bmp.RotateFlip(RotateFlipType.Rotate270FlipX); break;
            case 8: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
    }

    public static string Convert(string input, string ext)
    {
        using (var bmp = Load(input))
        {
            string output = Paths.Beside(input, "", ext);
            Save(bmp, output, ext, 92);
            return output;
        }
    }

    public static string Compress(string input)
    {
        using (var bmp = Load(input))
        {
            string output = Paths.Beside(input, " comprimido", ".jpg");
            Save(bmp, output, ".jpg", 70);
            return output;
        }
    }

    public static string Resize(string input, double factor)
    {
        using (var bmp = Load(input))
        using (var small = new Bitmap(Math.Max(1, (int)(bmp.Width * factor)), Math.Max(1, (int)(bmp.Height * factor))))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, 0, 0, small.Width, small.Height);
            }
            string ext = NormalizedSaveExt(input);
            string output = Paths.Beside(input, " " + (int)(factor * 100) + "%", ext);
            Save(small, output, ext, 92);
            return output;
        }
    }

    public static string Rotate(string input)
    {
        using (var bmp = Load(input))
        {
            bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
            string ext = NormalizedSaveExt(input);
            string output = Paths.Beside(input, " girado", ext);
            Save(bmp, output, ext, 92);
            return output;
        }
    }

    static string NormalizedSaveExt(string input)
    {
        string ext = Path.GetExtension(input).ToLowerInvariant();
        return ext == ".jpeg" ? ".jpg" : ext == ".tif" ? ".tiff" : ext;
    }

    public static void SaveAs(Bitmap bmp, string output, string ext)
    {
        Save(bmp, output, ext, 92);
    }

    public static byte[] ToJpeg(Bitmap bmp, long quality)
    {
        // JPG no tiene transparencia: se aplana sobre blanco.
        using (var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
        using (var ms = new MemoryStream())
        {
            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            }
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var prms = new EncoderParameters(1))
            {
                prms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                flat.Save(ms, codec, prms);
            }
            return ms.ToArray();
        }
    }

    static void Save(Bitmap bmp, string output, string ext, long jpegQuality)
    {
        switch (ext)
        {
            case ".jpg": File.WriteAllBytes(output, ToJpeg(bmp, jpegQuality)); break;
            case ".png": bmp.Save(output, ImageFormat.Png); break;
            case ".bmp": bmp.Save(output, ImageFormat.Bmp); break;
            case ".gif": bmp.Save(output, ImageFormat.Gif); break;
            case ".tiff": bmp.Save(output, ImageFormat.Tiff); break;
            case ".ico": SaveIco(bmp, output); break;
            default: throw new NotSupportedException("Formato no soportado: " + ext);
        }
    }

    static void SaveIco(Bitmap bmp, string output)
    {
        // ICO moderno: una imagen PNG de hasta 256x256 dentro de un contenedor ICO.
        int size = Math.Min(256, Math.Max(bmp.Width, bmp.Height));
        float k = (float)size / Math.Max(bmp.Width, bmp.Height);
        byte[] png;
        using (var square = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(square))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int w = (int)(bmp.Width * k), h = (int)(bmp.Height * k);
                g.DrawImage(bmp, (size - w) / 2, (size - h) / 2, w, h);
            }
            using (var ms = new MemoryStream())
            {
                square.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
        }
        using (var fs = File.Create(output))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);          // ICONDIR
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);                             // planos, bpp
            w.Write(png.Length); w.Write(22);                                  // tamaño, offset
            w.Write(png);
        }
    }
}

static class ArchiveOps
{
    public static string Zip(List<string> inputs)
    {
        string first = inputs[0].TrimEnd('\\');
        string dir = Path.GetDirectoryName(first);
        string name = inputs.Count == 1 ? Path.GetFileNameWithoutExtension(first) : "Archivo";
        if (inputs.Count == 1 && Directory.Exists(first)) name = Path.GetFileName(first);
        string output = Paths.Unique(dir, name, ".zip");

        using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            foreach (var input in inputs)
            {
                string path = input.TrimEnd('\\');
                if (Directory.Exists(path))
                {
                    string root = Path.GetDirectoryName(path);
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(file, file.Substring(root.Length + 1).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                else
                {
                    zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
                }
            }
        }
        return output;
    }

    public static string Extract(string input)
    {
        string output = Paths.Beside(input, "", "");
        ZipFile.ExtractToDirectory(input, output);
        return output;
    }
}
