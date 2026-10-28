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
    [DllImport("winmm.dll")] public static extern int timeBeginPeriod(int ms);
    [DllImport("winmm.dll")] public static extern int timeEndPeriod(int ms);
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
        EditorForm.Notify = ShowResult;
        // Progreso de vídeo/audio en el texto del icono de la bandeja (llega desde otro hilo).
        MediaOps.Progress = text => menu.BeginInvoke(new Action(() =>
            tray.Text = text == null ? "Switchify – arrastra + Shift o Ctrl+Shift+Espacio" : "Switchify – " + text));
        var forceHandle = menu.Handle; // necesario para BeginInvoke desde el hook

        // Si una versión anterior dejó el inicio en la clave Run (que Windows ignoraba), se pasa a la tarea.
        try { if (RemoveLegacyStartup()) SetStartup(true); }
        catch { }

        var startup = new ToolStripMenuItem("Iniciar con Windows") { Checked = IsStartupEnabled() };
        startup.Click += delegate
        {
            try { SetStartup(!startup.Checked); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Switchify", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            startup.Checked = IsStartupEnabled();
        };

        var strip = new ContextMenuStrip();
        strip.Items.Add(new ToolStripMenuItem("Switchify") { Enabled = false });
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(startup);
        strip.Items.Add("Cómo se usa", null, delegate { ShowHelp(); });
        strip.Items.Add("Salir", null, delegate { ExitThread(); });

        tray = new NotifyIcon
        {
            Icon = Theme.TrayIcon(),
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

    // Inicio con Windows mediante una tarea programada («al iniciar sesión»). La lista de inicio clásica
    // (clave Run) no sirve: esta versión de Windows 11 se saltaba Switchify sin motivo aparente, aunque
    // tuviera la misma marca de «activada» que los programas que sí lanzaba.
    const string TaskName = "Switchify";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static bool IsStartupEnabled()
    {
        string xml;
        return Schtasks("/Query /TN \"" + TaskName + "\" /XML", out xml) == 0 && !xml.Contains("<Enabled>false</Enabled>");
    }

    static void SetStartup(bool enable)
    {
        string output;
        if (enable)
        {
            // Formato de tarea de Windows (UTF-16). Sin límite de tiempo: por defecto Windows corta las tareas
            // a las 72 horas, y eso cerraría Switchify a los tres días.
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Abre Switchify al iniciar sesión.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(Application.ExecutablePath) + "</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "Switchify-tarea.xml");
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            try
            {
                if (Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out output) != 0)
                    throw new InvalidOperationException("No se pudo crear la tarea de inicio: " + output.Trim());
            }
            finally { File.Delete(file); }
        }
        else
        {
            Schtasks("/Delete /TN \"" + TaskName + "\" /F", out output);
        }
        RemoveLegacyStartup();
    }

    // Versiones anteriores usaban la clave Run (y su marca de aprobación): se quitan para no dejar restos.
    static bool RemoveLegacyStartup()
    {
        bool found = false;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (key != null && key.GetValue("Switchify") != null) { key.DeleteValue("Switchify", false); found = true; }
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
            if (approved != null) approved.DeleteValue("Switchify", false);
        return found;
    }

    static int Schtasks(string args, out string output)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
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
    public Color Tint, Center, Text, Secondary, Separator, Accent, AccentPressed, OnAccent, Edge, Halo;
    public float Frost;   // opacidad de la copia desenfocada del fondo (0 = el fondo real se ve tal cual)

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
            // Cristal esmerilado: el fondo de detrás se ve entero pero difuminado, con un tinte del 15 %.
            look.Frost = look.Solid ? 0 : 1f;
            look.Tint = Color.FromArgb(look.Solid ? 250 : 42, 30, 30, 32);
            look.Center = Color.FromArgb(look.Solid ? 255 : 64, 44, 44, 46);
            look.Halo = Color.FromArgb(look.Solid ? 0 : 120, 0, 0, 0);
            look.Text = Color.FromArgb(235, 255, 255, 255);
            look.Secondary = Color.FromArgb(150, 235, 235, 245);
            look.Separator = Color.FromArgb(30, 255, 255, 255);
            look.Accent = Color.FromArgb(10, 132, 255);
            look.AccentPressed = Color.FromArgb(0, 104, 214);
            look.Edge = Color.FromArgb(45, 255, 255, 255);
        }
        else
        {
            look.Frost = look.Solid ? 0 : 1f;
            look.Tint = Color.FromArgb(look.Solid ? 250 : 38, 250, 250, 252);
            look.Center = Color.FromArgb(look.Solid ? 255 : 64, 255, 255, 255);
            look.Halo = Color.FromArgb(look.Solid ? 0 : 130, 255, 255, 255);
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

    // "Vibrancy": con el cristal tan transparente, el texto se adapta a lo que hay detrás.
    // Fondo oscuro → texto claro con halo oscuro; fondo claro → texto oscuro con halo claro.
    public void AdaptTo(double luminance)
    {
        if (Solid) return;
        if (!Dark && luminance < 0.42)
        {
            Text = Color.FromArgb(240, 255, 255, 255);
            Secondary = Color.FromArgb(175, 235, 235, 245);
            Separator = Color.FromArgb(40, 255, 255, 255);
            Halo = Color.FromArgb(110, 0, 0, 0);
            Center = Color.FromArgb(64, 44, 44, 46);
            Edge = Color.FromArgb(70, 255, 255, 255);
        }
        else if (Dark && luminance > 0.7)
        {
            Text = Color.FromArgb(225, 0, 0, 0);
            Secondary = Color.FromArgb(160, 60, 60, 67);
            Separator = Color.FromArgb(40, 60, 60, 67);
            Halo = Color.FromArgb(130, 255, 255, 255);
            Center = Color.FromArgb(64, 255, 255, 255);
        }
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
    // Icono del logo (la S de flechas), que build.ps1 incrusta como recurso "Switchify.ico" con todos sus tamaños.
    static Icon Embedded(Size? size)
    {
        using (var stream = typeof(Theme).Assembly.GetManifestResourceStream("Switchify.ico"))
        {
            if (stream == null) return null;
            return size.HasValue ? new Icon(stream, size.Value) : new Icon(stream);
        }
    }

    // Bandeja: el tamaño pequeño que pide Windows según la escala de pantalla (16 px al 100 %, 24 al 150 %…).
    public static Icon TrayIcon()
    {
        return Embedded(SystemInformation.SmallIconSize) ?? MakeTrayIcon();
    }

    // Ventanas: el icono completo; Windows elige el tamaño para la barra de tareas y Alt+Tab.
    public static Icon WindowIcon()
    {
        return Embedded(null) ?? MakeTrayIcon();
    }

    // Respaldo si faltara el recurso: el icono dibujado de antes.
    static Icon MakeTrayIcon()
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

    public static readonly string[] FontNames = { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" };
    public static readonly string[] TextFontNames = { "Segoe UI Variable Text", "Segoe UI" };

    readonly float scale;
    readonly int outerR, innerR, pad, center;
    readonly System.Windows.Forms.Timer releaseTimer, frameTimer, dwellTimer;
    readonly Stopwatch clock = new Stopwatch();
    readonly Spring appear = new Spring(1.0, 0.32);   // entrada/salida: sin rebote
    readonly Spring labelsIn = new Spring(1.0, 0.3);  // las etiquetas emergen al cambiar de página
    readonly FontFamily labelFont, textFont;
    Spring[] hovers = new Spring[0];                    // resaltado de cada opción
    List<FileAction> actions = new List<FileAction>();
    List<string> files = new List<string>();
    Look look = Look.Current();
    Bitmap backdrop;                                    // lo que hay detrás, ya desenfocado
    Bitmap frame, scene, baseLayer;                     // bitmaps reutilizados entre fotogramas
    GraphicsPath[] labelPaths;                          // texto de cada etiqueta, ya convertido en trazado
    bool[] labelLight;                                  // por etiqueta: texto claro (fondo oscuro detrás) u oscuro
    bool centerLight;
    readonly Font primaryFont, secondaryFont;
    readonly StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    readonly StringFormat above = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter };
    readonly StringFormat below = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
    int hover = -1, pressed = -1;
    int noDwell = -1;          // tras cambiar de página, la opción bajo el cursor no vuelve a cambiarla
    float angleOffset;         // giro del anillo para que "volver" quede donde estaba "Herramientas"
    string sizeText;           // tamaño del archivo, mostrado en el centro
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
        labelsIn.Snap(1);
        primaryFont = new Font(labelFont, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        secondaryFont = new Font(textFont, 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

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

        frameTimer = new System.Windows.Forms.Timer { Interval = 8 };
        frameTimer.Tick += delegate { Animate(); };

        // Como las carpetas "con resorte" de macOS: dejar el cursor sobre una opción con submenú lo abre.
        dwellTimer = new System.Windows.Forms.Timer { Interval = 260 };
        dwellTimer.Tick += delegate
        {
            dwellTimer.Stop();
            if (!closing && hover >= 0 && hover < actions.Count && actions[hover].SubActions != null) SwitchPage(hover);
        };
    }

    static HashSet<string> installedFonts;

    public static FontFamily PickFont(string[] names)
    {
        if (installedFonts == null) installedFonts = new HashSet<string>(FontFamily.Families.Select(f => f.Name));
        var installed = installedFonts;
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
        SetFiles(selected);
        PlaceAtCursor();
        // El fondo se captura antes de mostrar la ventana (las ventanas en capas no salen en la captura).
        if (backdrop != null) backdrop.Dispose();
        backdrop = look.Solid ? null : CaptureBackdrop();
        if (backdrop != null) look.AdaptTo(Luminance(backdrop));
        InvalidateCache();
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
        StopAnimation();
        dwellTimer.Stop();
        closing = false;
        revealed = false;
        Hide();
        if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        InvalidateCache();
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
        // El reloj de Windows va a saltos de ~16 ms; mientras se anima se pide 1 ms para que los fotogramas
        // salgan regulares. Se devuelve al terminar para no gastar batería.
        Native.timeBeginPeriod(1);
        clock.Restart();
        frameTimer.Start();
    }

    void StopAnimation()
    {
        if (!frameTimer.Enabled) return;
        frameTimer.Stop();
        Native.timeEndPeriod(1);
    }

    void Animate()
    {
        double dt = Math.Min(0.05, clock.Elapsed.TotalSeconds);
        clock.Restart();
        appear.Step(dt);
        labelsIn.Step(dt);
        foreach (var h in hovers) h.Step(dt);
        Render();

        if (closing && appear.Value < 0.02) { Finish(); return; }
        if (appear.Settled && labelsIn.Settled && hovers.All(h => h.Settled)) StopAnimation();
    }

    void SetFiles(List<string> list)
    {
        files = list;
        angleOffset = 0;
        labelsIn.Snap(1);
        SetActions(FileActions.For(files));
        sizeText = null;
        try { if (files.Count == 1 && File.Exists(files[0])) sizeText = Sizes.Format(new FileInfo(files[0]).Length); }
        catch { }
    }

    void SetActions(List<FileAction> list)
    {
        actions = list;
        InvalidateCache();
        hovers = list.Select(a => new Spring(1.0, 0.18)).ToArray();
        hover = -1;
    }

    // Cambia el anillo a la página de la opción `index`, girándolo para que la opción de vuelta
    // quede justo bajo el cursor (se sale por el mismo sitio por el que se entró).
    void SwitchPage(int index)
    {
        var from = actions;
        var page = actions[index].SubActions;
        int back = page.FindIndex(a => a.SubActions == from);
        float oldMid = angleOffset + index * 360f / from.Count;
        angleOffset = back >= 0 ? oldMid - back * 360f / page.Count : 0;
        SetActions(page);
        pressed = -1;
        labelsIn.Snap(0);
        labelsIn.Target = 1;
        // Lo que quede bajo el cursor (normalmente "volver") no se reactiva hasta salir de ello.
        int under = HitTest(PointToClient(Cursor.Position));
        noDwell = under;
        SetHover(under);
        StartAnimation();
    }

    void SetHover(int index)
    {
        // Al cerrar, la opción elegida se queda resaltada mientras el menú se va.
        if (closing || index == hover) return;
        hover = index;
        if (pressed != index) pressed = -1;
        dwellTimer.Stop();
        if (index != noDwell) noDwell = -1;
        if (index >= 0 && index != noDwell && actions[index].SubActions != null) dwellTimer.Start();
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
            SetFiles(dropped.ToList());
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
        // Soltar sobre un submenú no hace nada: hay que esperar a que se abra y elegir dentro.
        if (chosen >= 0 && dropped != null && actions[chosen].SubActions == null) Execute(actions[chosen], dropped.ToList());
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
        if (chosen >= 0 && actions[chosen].SubActions != null) { SwitchPage(chosen); return; }
        HideMenu();
        if (chosen >= 0) Execute(actions[chosen], selected);
    }

    // --- Ejecución ---------------------------------------------------------------------------

    void Execute(FileAction action, List<string> targets)
    {
        if (action.Open != null)
        {
            // Herramientas con ventana: el menú se quita al instante (la ventana nueva es la transición)
            // y la ventana se abre en el hilo de la interfaz cuando acabe el arrastre.
            if (Visible) Finish();
            string first = targets.FirstOrDefault();
            if (first != null) BeginInvoke(new Action(() =>
            {
                try { action.Open(first); }
                catch (Exception ex) { if (ActionFinished != null) ActionFinished("Switchify – error", ex.Message, true); }
            }));
            return;
        }
        if (action.Slow && ActionFinished != null)
            ActionFinished(action.Description + "…", "Te aviso cuando termine. El progreso se ve en el icono de la bandeja.", false);
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
        double rel = ((angle - angleOffset - (-90 - sweep / 2)) % 360 + 360) % 360;
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

    // Luminosidad media (0 = negro, 1 = blanco) del fondo bajo el anillo, muestreando una rejilla.
    double Luminance(Bitmap bmp)
    {
        double sum = 0;
        int count = 0, step = Math.Max(4, outerR / 12);
        for (int y = pad; y < pad + outerR * 2; y += step)
        {
            for (int x = pad; x < pad + outerR * 2; x += step)
            {
                double dx = x - center, dy = y - center;
                if (dx * dx + dy * dy > (double)outerR * outerR) continue;
                var c = bmp.GetPixel(x, y);
                sum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
                count++;
            }
        }
        return count == 0 ? 0.5 : sum / count;
    }

    public static Bitmap Blur(Bitmap src)
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
    //
    // Para que cada fotograma sea barato, lo que no cambia mientras se mueve el ratón (sombra, cristal,
    // separadores, borde y los trazados de las etiquetas) se dibuja una sola vez por página y se reutiliza.
    void Render()
    {
        if (!IsHandleCreated) return;
        if (frame == null || frame.Size != Size)
        {
            DisposeFrames();
            frame = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            scene = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        }

        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);
            // Disco casi invisible a tamaño completo: mantiene el arrastre "enganchado" a la ventana
            // aunque el menú visible esté a mitad de su animación de entrada.
            using (var b = new SolidBrush(Color.FromArgb(1, 255, 255, 255)))
                g.FillEllipse(b, pad, pad, outerR * 2, outerR * 2);

            if (revealed)
            {
                float a = (float)Math.Max(0, Math.Min(1, appear.Value));
                float k = look.Motion ? 0.86f + 0.14f * a : 1f;
                if (a > 0.995f && k > 0.995f)
                {
                    // Ya a tamaño completo (lo normal al mover el ratón): se dibuja directamente.
                    DrawScene(g);
                }
                else
                {
                    // Se materializa desde el cursor: escala + opacidad. Con animaciones desactivadas, solo fundido.
                    using (var sg = Graphics.FromImage(scene))
                    {
                        sg.Clear(Color.Transparent);
                        DrawScene(sg);
                    }
                    g.TranslateTransform(center, center);
                    g.ScaleTransform(k, k);
                    g.TranslateTransform(-center, -center);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                        g.DrawImage(scene, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attrs);
                    }
                }
            }
        }
        Native.ApplyLayeredBitmap(this, frame);
    }

    void DisposeFrames()
    {
        if (frame != null) { frame.Dispose(); frame = null; }
        if (scene != null) { scene.Dispose(); scene = null; }
    }

    // La capa base y las etiquetas se rehacen al cambiar de archivo, de página o de tema.
    void InvalidateCache()
    {
        if (baseLayer != null) { baseLayer.Dispose(); baseLayer = null; }
        if (labelPaths != null) foreach (var p in labelPaths) if (p != null) p.Dispose();
        labelPaths = null;
    }

    float Sweep { get { return actions.Count > 0 ? 360f / actions.Count : 360f; } }

    double MidAngle(int i)
    {
        return (-90 + i * Sweep + angleOffset) * Math.PI / 180;
    }

    void BuildBaseLayer()
    {
        baseLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(baseLayer))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var outer = new RectangleF(pad, pad, outerR * 2, outerR * 2);

            // Sombra amplia y suave, algo desplazada hacia abajo: una superficie grande "flota" más.
            int shadowSteps = pad - 2;
            for (int i = shadowSteps; i > 0; i--)
            {
                double t = 1 - (double)i / shadowSteps;
                int alpha = (int)((look.Dark ? 70 : 38) * t * t / 6);
                using (var b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    g.FillEllipse(b, RectangleF.Inflate(new RectangleF(outer.X, outer.Y + 6 * scale, outer.Width, outer.Height), i, i));
            }

            // Material: el fondo real se ve a través del anillo. Encima, solo un velo de la copia desenfocada
            // (un toque de esmerilado) y el tinte. Bajo el cristal se borra la sombra para que no lo oscurezca.
            using (var disk = new GraphicsPath())
            {
                disk.AddEllipse(outer);
                g.SetClip(disk);
                g.Clear(Color.Transparent);
                if (backdrop != null && look.Frost > 0)
                {
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = look.Frost });
                        g.DrawImage(backdrop, new Rectangle(0, 0, Width, Height), 0, 0, Width, Height, GraphicsUnit.Pixel, attrs);
                    }
                }
                using (var tint = new SolidBrush(look.Tint)) g.FillPath(tint, disk);
                g.ResetClip();
            }

            // Separadores finos, sin llegar a los bordes.
            int n = actions.Count;
            if (n > 1)
            {
                using (var sep = new Pen(look.Separator, 1f * scale))
                {
                    for (int i = 0; i < n; i++)
                    {
                        double a = MidAngle(i) - Sweep / 2 * Math.PI / 180;
                        float r0 = innerR + 14 * scale, r1 = outerR - 14 * scale;
                        g.DrawLine(sep, center + (float)Math.Cos(a) * r0, center + (float)Math.Sin(a) * r0,
                                        center + (float)Math.Cos(a) * r1, center + (float)Math.Sin(a) * r1);
                    }
                }
            }

            // Borde de luz: una línea clara donde el cristal "atrapa" la luz.
            using (var edge = new Pen(look.Edge, 1f * scale)) g.DrawEllipse(edge, RectangleF.Inflate(outer, -0.5f * scale, -0.5f * scale));
        }
    }

    // Trazados de texto de cada etiqueta en su posición de reposo (luego solo se desplazan).
    void BuildLabelPaths()
    {
        int n = actions.Count;
        labelPaths = new GraphicsPath[n];
        labelLight = new bool[n];
        float labelSize = (n > 8 ? 12.5f : 14f) * scale;
        float r = (outerR + innerR) / 2f;
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            for (int i = 0; i < n; i++)
            {
                double mid = MidAngle(i);
                var c = new PointF(center + (float)Math.Cos(mid) * r, center + (float)Math.Sin(mid) * r);
                var path = new GraphicsPath();
                if (actions[i].Icon != null)
                {
                    var small = new RectangleF(c.X - 60 * scale, c.Y + 4 * scale, 120 * scale, 22 * scale);
                    path.AddString(actions[i].Label.ToUpperInvariant(), labelFont, (int)FontStyle.Regular, 10.5f * scale, small, sf);
                }
                else
                {
                    var box = new RectangleF(c.X - 60 * scale, c.Y - 16 * scale, 120 * scale, 32 * scale);
                    path.AddString(actions[i].Label, labelFont, (int)FontStyle.Regular, labelSize, box, sf);
                }
                labelPaths[i] = path;
                labelLight[i] = RegionLuminance(new RectangleF(c.X - 44 * scale, c.Y - 24 * scale, 88 * scale, 48 * scale)) < 0.5;
            }
        }

        // El centro tiene su propio velo: cuenta en lo que se ve detrás del texto.
        var inner = new RectangleF(center - innerR * 0.7f, center - innerR * 0.5f, innerR * 1.4f, innerR);
        double a = look.Center.A / 255.0;
        double centerLum = (0.2126 * look.Center.R + 0.7152 * look.Center.G + 0.0722 * look.Center.B) / 255;
        centerLight = RegionLuminance(inner) * (1 - a) + centerLum * a < 0.5;
    }

    void DrawScene(Graphics g)
    {
        if (baseLayer == null) BuildBaseLayer();
        if (labelPaths == null || labelPaths.Length != actions.Count) BuildLabelPaths();

        g.DrawImageUnscaled(baseLayer, 0, 0);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var inner = new RectangleF(center - innerR, center - innerR, innerR * 2, innerR * 2);
        int n = actions.Count;
        float sweep = Sweep;

        // Opción resaltada: una pieza sólida de color de acento, con esquinas redondeadas,
        // que se adelanta un poco hacia el cursor.
        for (int i = 0; i < n; i++)
        {
            float hv = (float)Math.Max(0, Math.Min(1, hovers[i].Value));
            if (hv < 0.01f) continue;
            float start = -90 - sweep / 2 + i * sweep + angleOffset;
            double mid = MidAngle(i);
            float push = 3 * scale * hv;
            using (var path = SegmentPath(start, sweep, (float)Math.Cos(mid) * push, (float)Math.Sin(mid) * push))
            {
                Color c = i == pressed ? look.AccentPressed : look.Accent;
                var fill = Color.FromArgb((int)(255 * hv), c);
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var round = new Pen(fill, 8 * scale) { LineJoin = LineJoin.Round }) g.DrawPath(round, path);
            }
        }

        // Etiquetas. Al cambiar de página emergen desde el centro (apuntan hacia donde va el gesto).
        float li = (float)Math.Max(0, Math.Min(1, labelsIn.Value));
        float r = (outerR + innerR) / 2f;
        for (int i = 0; i < n; i++)
        {
            float hv = (float)Math.Max(0, Math.Min(1, hovers[i].Value));
            double mid = MidAngle(i);
            float shift = 3 * scale * hv - 14 * scale * (1 - li);
            float dx = (float)Math.Cos(mid) * shift, dy = (float)Math.Sin(mid) * shift;
            // Cada etiqueta usa el color que contrasta con lo que tiene justo detrás.
            Color text, halo;
            Vibrancy(labelLight[i], out text, out halo);
            var color = Lerp(text, look.OnAccent, hv);
            color = Color.FromArgb((int)(color.A * li), color);
            float haloK = li * Math.Max(0, 1 - hv * 2);   // sobre el color de acento no hace falta halo

            var state = g.Save();
            g.TranslateTransform(dx, dy);
            if (haloK > 0.01f) DrawHalo(g, labelPaths[i], halo, haloK);
            using (var brush = new SolidBrush(color)) g.FillPath(brush, labelPaths[i]);
            g.Restore(state);

            if (actions[i].Icon != null)
            {
                var c = new PointF(center + (float)Math.Cos(mid) * r + dx, center + (float)Math.Sin(mid) * r + dy);
                var iconPos = new PointF(c.X, c.Y - 9 * scale);
                if (haloK > 0.01f)
                {
                    Icons.Draw(g, actions[i].Icon, iconPos, 20 * scale, Color.FromArgb((int)(halo.A * haloK * 0.35f), halo), 4.2f);
                    Icons.Draw(g, actions[i].Icon, iconPos, 20 * scale, Color.FromArgb((int)(halo.A * haloK), halo), 2.4f);
                }
                Icons.Draw(g, actions[i].Icon, iconPos, 20 * scale, color, 1f);
            }
        }

        // Centro: superficie con la jerarquía de texto (principal + secundario).
        using (var cb = new SolidBrush(look.Center)) g.FillEllipse(cb, inner);
        using (var edge = new Pen(look.Separator, 1f * scale)) g.DrawEllipse(edge, inner);

        string name = files.Count == 1 ? Shorten(Path.GetFileName(files[0])) : files.Count + " archivos";
        string primary, secondary;
        if (n == 0) { primary = "Switchify"; secondary = ""; }
        else if (hover >= 0) { primary = actions[hover].Description; secondary = name; }
        else { primary = name; secondary = sizeText ?? (dragMode ? "Suelta en una opción" : "Elige una opción"); }

        var textBox = RectangleF.Inflate(inner, -12 * scale, -14 * scale);
        float gap = 2 * scale;
        var top = new RectangleF(textBox.X, textBox.Y, textBox.Width, textBox.Height / 2 + 6 * scale - gap);
        var bottom = new RectangleF(textBox.X, textBox.Y + textBox.Height / 2 + 6 * scale + gap, textBox.Width, textBox.Height / 2 - 6 * scale);
        if (secondary.Length == 0) top = textBox;

        Color ctext, chalo;
        Vibrancy(centerLight, out ctext, out chalo);
        var csecondary = Color.FromArgb(ctext.A * 2 / 3, ctext);
        using (var p1 = new GraphicsPath())
        using (var p2 = new GraphicsPath())
        {
            p1.AddString(primary, labelFont, (int)FontStyle.Regular, 12.5f * scale, top, secondary.Length == 0 ? centered : above);
            if (secondary.Length > 0) p2.AddString(secondary, textFont, (int)FontStyle.Regular, 11f * scale, bottom, below);
            DrawHalo(g, p1, chalo, 0.8f);
            DrawHalo(g, p2, chalo, 0.6f);
            using (var b1 = new SolidBrush(ctext)) g.FillPath(b1, p1);
            using (var b2 = new SolidBrush(csecondary)) g.FillPath(b2, p2);
        }
    }

    // Texto claro sobre fondos oscuros y oscuro sobre fondos claros, cada uno con su halo contrario.
    static void Vibrancy(bool lightText, out Color text, out Color halo)
    {
        text = lightText ? Color.FromArgb(245, 255, 255, 255) : Color.FromArgb(230, 0, 0, 0);
        halo = lightText ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
    }

    // Halo doble: un resplandor ancho y suave más un contorno fino y firme.
    void DrawHalo(Graphics g, GraphicsPath path, Color halo, float k)
    {
        using (var glow = new Pen(Color.FromArgb((int)(halo.A * k * 0.35f), halo), 6.5f * scale) { LineJoin = LineJoin.Round })
            g.DrawPath(glow, path);
        using (var line = new Pen(Color.FromArgb((int)(halo.A * k), halo), 3f * scale) { LineJoin = LineJoin.Round })
            g.DrawPath(line, path);
    }

    // Luminosidad media (0..1) del fondo en una zona; sin fondo capturado, la del tema.
    double RegionLuminance(RectangleF r)
    {
        if (backdrop == null) return look.Dark ? 0.1 : 0.9;
        double sum = 0;
        int count = 0;
        for (int iy = 0; iy < 5; iy++)
        {
            for (int ix = 0; ix < 7; ix++)
            {
                int x = (int)(r.X + r.Width * (ix + 0.5f) / 7), y = (int)(r.Y + r.Height * (iy + 0.5f) / 5);
                if (x < 0 || y < 0 || x >= backdrop.Width || y >= backdrop.Height) continue;
                var c = backdrop.GetPixel(x, y);
                sum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
                count++;
            }
        }
        return count == 0 ? 0.5 : sum / count;
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
    public Action<string> Open;                    // abre una ventana de herramienta (hilo de la interfaz)
    public List<FileAction> SubActions;            // otra página del anillo
    public string Icon;                            // símbolo dibujado sobre la etiqueta (ver Icons)
    public bool Slow;                              // tarda (vídeo, audio): se avisa al empezar
    public bool Batch { get { return RunBatch != null; } }
}

// Símbolos de línea, al estilo de los SF Symbols: trazo redondeado, dibujados en una caja de `size`.
static class Icons
{
    public static void Draw(Graphics g, string name, PointF c, float size, Color color, float stroke)
    {
        float u = size / 20f;  // unidad: la caja mide 20×20
        Func<float, float, PointF> P = (x, y) => new PointF(c.X + (x - 10) * u, c.Y + (y - 10) * u);
        Func<float, float, float, float, RectangleF> Rc = (x, y, w, h) => new RectangleF(c.X + (x - 10) * u, c.Y + (y - 10) * u, w * u, h * u);

        // `stroke` engrosa el trazo (se usa para dibujar el halo debajo del símbolo).
        using (var pen = new Pen(color, 1.7f * u * stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        using (var fill = new SolidBrush(color))
        {
            switch (name)
            {
                case "tools":
                    foreach (var o in new[] { new PointF(2, 2), new PointF(11, 2), new PointF(2, 11), new PointF(11, 11) })
                        using (var path = Shapes.Rounded(Rc(o.X, o.Y, 7, 7), 2 * u)) g.DrawPath(pen, path);
                    break;
                case "back":
                    g.DrawLines(pen, new[] { P(12, 4), P(6, 10), P(12, 16) });
                    break;
                case "compress":
                    // Dos flechas que se encuentran en el centro.
                    g.DrawLine(pen, P(3, 3), P(8.5f, 8.5f));
                    g.DrawLines(pen, new[] { P(4, 8.5f), P(8.5f, 8.5f), P(8.5f, 4) });
                    g.DrawLine(pen, P(17, 17), P(11.5f, 11.5f));
                    g.DrawLines(pen, new[] { P(16, 11.5f), P(11.5f, 11.5f), P(11.5f, 16) });
                    break;
                case "tag":
                    g.DrawPolygon(pen, new[] { P(3, 3), P(10, 3), P(17.5f, 10.5f), P(10.5f, 17.5f), P(3, 10) });
                    g.FillEllipse(fill, Rc(5.5f, 5.5f, 2.6f, 2.6f));
                    break;
                case "sliders":
                    foreach (var row in new[] { new PointF(5, 13), new PointF(10, 6), new PointF(15, 10) })
                    {
                        g.DrawLine(pen, P(3, row.X), P(17, row.X));
                        g.FillEllipse(fill, Rc(row.Y - 2.3f, row.X - 2.3f, 4.6f, 4.6f));
                    }
                    break;
                case "pen":
                    g.DrawPolygon(pen, new[] { P(13.5f, 3.5f), P(16.5f, 6.5f), P(7, 16), P(3.5f, 16.5f), P(4, 13) });
                    g.DrawLine(pen, P(11.5f, 5.5f), P(14.5f, 8.5f));
                    break;
                case "frame":
                    using (var path = Shapes.Rounded(Rc(2.5f, 3.5f, 15, 13), 2.5f * u)) g.DrawPath(pen, path);
                    g.DrawLines(pen, new[] { P(4.5f, 14), P(8.5f, 9.5f), P(11.5f, 12.5f), P(13, 11), P(15.5f, 14) });
                    g.FillEllipse(fill, Rc(12, 6, 2.6f, 2.6f));
                    break;
                case "crop":
                    g.DrawLines(pen, new[] { P(6, 2), P(6, 14), P(18, 14) });
                    g.DrawLines(pen, new[] { P(2, 6), P(14, 6), P(14, 18) });
                    break;
                case "eyeslash":
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(P(2, 10), P(5, 4.5f), P(15, 4.5f), P(18, 10));
                        path.AddBezier(P(18, 10), P(15, 15.5f), P(5, 15.5f), P(2, 10));
                        g.DrawPath(pen, path);
                    }
                    g.DrawEllipse(pen, Rc(7.5f, 7.5f, 5, 5));
                    g.DrawLine(pen, P(3.5f, 3.5f), P(16.5f, 16.5f));
                    break;
            }
        }
    }
}

static class FileActions
{
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" };

    static readonly string[][] Formats =
    {
        new[] { "PNG", ".png" }, new[] { "JPG", ".jpg" }, new[] { "BMP", ".bmp" },
        new[] { "GIF", ".gif" }, new[] { "TIFF", ".tiff" }, new[] { "ICO", ".ico" }
    };

    static readonly string[][] AudioFormats =
    {
        new[] { "MP3", ".mp3" }, new[] { "M4A", ".m4a" }, new[] { "WAV", ".wav" },
        new[] { "FLAC", ".flac" }, new[] { "WMA", ".wma" }
    };

    // Las conversiones de vídeo y audio pueden tardar: se marcan como lentas para avisar al empezar.
    static FileAction Media(string label, string description, string ext, bool compress)
    {
        return new FileAction { Label = label, Description = description, Slow = true, Run = f => MediaOps.Convert(f, ext, compress) };
    }

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
        List<FileAction> tools = null;

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
            if (files.Count == 1)
            {
                // Una sola imagen: las herramientas van en su propia página del anillo.
                tools = ImageTools();
                list.Add(new FileAction { Label = "Herramientas", Description = "Herramientas de imagen", Icon = "tools", SubActions = tools });
            }
            else
            {
                list.Add(new FileAction { Label = "Comprimir", Description = "Comprimir (JPG 70 %)", Run = ImageOps.Compress });
                list.Add(new FileAction { Label = "50 %", Description = "Reducir a la mitad", Run = f => ImageOps.Resize(f, 0.5) });
                list.Add(new FileAction { Label = "Girar 90°", Description = "Girar 90°", Run = ImageOps.Rotate });
            }
            list.Add(new FileAction { Label = "PDF", Description = files.Count == 1 ? "Convertir a PDF" : "Unir en un PDF", RunBatch = PdfWriter.FromImages });
            list.Add(new FileAction { Label = "Texto", Description = "Extraer texto (OCR)", Run = Documents.ImageToText });
        }
        else if (files.All(MediaOps.IsVideo))
        {
            string own = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().Count() == 1
                ? Path.GetExtension(files[0]).ToLowerInvariant() : null;
            if (own != ".mp4") list.Add(Media("MP4", "Convertir a MP4", ".mp4", false));
            if (own != ".wmv") list.Add(Media("WMV", "Convertir a WMV", ".wmv", false));
            list.Add(Media("Comprimir", "Vídeo más ligero (MP4)", ".mp4", true));
            list.Add(Media("MP3", "Extraer audio a MP3", ".mp3", false));
            list.Add(Media("M4A", "Extraer audio a M4A", ".m4a", false));
            list.Add(Media("WAV", "Extraer audio a WAV", ".wav", false));
        }
        else if (files.All(MediaOps.IsAudio))
        {
            var exts = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().ToList();
            foreach (var fmt in AudioFormats)
            {
                if (exts.Count == 1 && exts[0] == fmt[1]) continue;
                list.Add(Media(fmt[0], "Convertir a " + fmt[0], fmt[1], false));
            }
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

        if (tools != null)
            tools.Add(new FileAction { Label = "Formatos", Description = "Volver a formatos", Icon = "back", SubActions = list });
        return list;
    }

    // Página de herramientas de imagen, en el orden del menú de macOS (desde arriba, en el sentido del reloj).
    static List<FileAction> ImageTools()
    {
        return new List<FileAction>
        {
            new FileAction { Label = "Comprimir", Description = "Comprimir (JPG 70 %)", Icon = "compress", Run = ImageOps.Compress },
            new FileAction { Label = "Metadatos", Description = "Ver y quitar metadatos", Icon = "tag", Open = f => new MetadataWindow(f).Show() },
            new FileAction { Label = "Editar", Description = "Brillo, color y giro", Icon = "sliders", Open = f => new AdjustWindow(f).Show() },
            new FileAction { Label = "Anotar", Description = "Dibujar y señalar", Icon = "pen", Open = f => new AnnotateWindow(f).Show() },
            new FileAction { Label = "Fondo", Description = "Añadir fondo", Icon = "frame", Open = f => new BackgroundWindow(f).Show() },
            new FileAction { Label = "Recortar", Description = "Recortar imagen", Icon = "crop", Open = f => new CropWindow(f).Show() },
            new FileAction { Label = "Tapar", Description = "Tapar información", Icon = "eyeslash", Open = f => new RedactWindow(f).Show() }
        };
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
