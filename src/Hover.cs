// Qué archivo hay bajo el cursor en el Escritorio o en una ventana del Explorador (para abrir el anillo con
// un toque de Shift sin arrastrar). La accesibilidad de Windows (UI Automation) dice el nombre del elemento
// en ese punto, y el Explorador (Shell.Application) da la ruta del archivo con ese nombre en esa carpeta.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

static class HoverFiles
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);

    // ¿Hay bajo el punto una ventana donde puede haber archivos (Escritorio o Explorador)? Es inmediato.
    public static bool IsFileWindowAt(int x, int y)
    {
        IntPtr root = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2);   // GA_ROOT
        var cls = new StringBuilder(64);
        GetClassName(root, cls, cls.Capacity);
        string c = cls.ToString();
        return c == "Progman" || c == "WorkerW" || c == "CabinetWClass";
    }

    // Debe llamarse en un hilo STA (el objeto Shell.Application es COM de un solo hilo).
    public static List<string> Get(int x, int y)
    {
        var result = new List<string>();
        IntPtr root = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2);   // GA_ROOT
        var cls = new StringBuilder(64);
        GetClassName(root, cls, cls.Capacity);
        bool desktop = cls.ToString() == "Progman" || cls.ToString() == "WorkerW";
        bool explorer = cls.ToString() == "CabinetWClass";
        if (!desktop && !explorer) return result;

        string name = ItemNameAt(x, y);
        if (string.IsNullOrEmpty(name))
        {
            // La accesibilidad a veces no responde a la primera (por ejemplo, justo al arrancar): un reintento.
            System.Threading.Thread.Sleep(150);
            name = ItemNameAt(x, y);
        }
        if (string.IsNullOrEmpty(name)) return result;

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        if (desktop)
        {
            // El Escritorio junta el del usuario y el público: el espacio de nombres 0 los incluye a los dos.
            string path = FindByName(shell.NameSpace(0).Items(), name);
            if (path != null) result.Add(path);
            return result;
        }

        // Explorador: la ventana con ese identificador (con pestañas puede haber varias: vale la que tenga el archivo).
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                if (new IntPtr((long)window.HWND) != root) continue;
                string path = FindByName(window.Document.Folder.Items(), name);
                if (path == null) continue;

                // Si el archivo forma parte de una selección de varios, el anillo actúa sobre toda la selección.
                var selection = new List<string>();
                foreach (dynamic item in window.Document.SelectedItems()) selection.Add((string)item.Path);
                if (selection.Count > 1 && selection.Contains(path, StringComparer.OrdinalIgnoreCase))
                    result.AddRange(selection.Where(p => File.Exists(p) || Directory.Exists(p)));
                else
                    result.Add(path);
                return result;
            }
            catch { }
        }
        return result;
    }

    // La primera consulta de accesibilidad de un proceso es lenta (carga y conecta todo): se hace al arrancar,
    // en segundo plano, para que el primer toque de Shift responda igual de rápido que los demás. No basta con
    // preguntar por cualquier punto: hay que llegar hasta un icono del Explorador (el Escritorio), que es lo que
    // carga la conexión con su proceso, y recorrer sus ventanas abiertas.
    public static void Warm()
    {
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var root = AutomationElement.RootElement;
                AutomationElement.FromPoint(new System.Windows.Point(0, 0));
                IntPtr desktop = FindWindow("Progman", null);
                if (desktop != IntPtr.Zero)
                {
                    var item = AutomationElement.FromHandle(desktop).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    if (item != null)
                    {
                        var name = item.Current.Name;
                        TreeWalker.ControlViewWalker.GetParent(item);
                    }
                }
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                foreach (dynamic window in shell.Windows()) { try { var hwnd = window.HWND; } catch { } }
                shell.NameSpace(0).Items().Count.ToString();
            }
            catch { }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    // Nombre (tal como se ve) del icono o la fila bajo el cursor; null si no es un elemento de la lista.
    static string ItemNameAt(int x, int y)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            // Sobre el texto de una columna, el elemento es un hijo de la fila: se sube hasta el elemento de la lista.
            for (int i = 0; element != null && i < 5; i++)
            {
                if (element.Current.ControlType == ControlType.ListItem) return element.Current.Name;
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch { }
        return null;
    }

    // Ruta del elemento de la carpeta cuyo nombre visible es `name` (solo archivos y carpetas reales).
    static string FindByName(dynamic items, string name)
    {
        foreach (dynamic item in items)
        {
            try
            {
                if ((string)item.Name != name) continue;
                string path = item.Path;
                if (File.Exists(path) || Directory.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }
}
