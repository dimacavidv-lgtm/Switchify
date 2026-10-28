// Imprimir desde el anillo. Se muestra una vez el cuadro de impresión de Windows (impresora, copias, páginas)
// y después cada archivo se imprime según su tipo:
//  - imágenes: ajustadas a la página, con 1 cm de margen y en horizontal si son apaisadas;
//  - documentos (MD, TXT, DOCX, HTML): con la misma maquetación que los PDF;
//  - PDF: cada página dibujada por Windows (sin Adobe ni otro lector);
//  - otros (Excel, PowerPoint…): con el programa que los abre, como «Imprimir» del Explorador.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static class PrintOps
{
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" };
    static readonly string[] Never = { ".zip", ".exe", ".dll", ".lnk", ".msi" };

    static string Ext(string path) { return Path.GetExtension(path).ToLowerInvariant(); }
    static bool IsImage(string path) { return ImageExts.Contains(Ext(path)); }
    static bool IsDocument(string path) { return Documents.Extensions.Contains(Ext(path)); }
    static bool IsPdf(string path) { return Ext(path) == ".pdf"; }

    public static bool CanPrint(string path)
    {
        if (!File.Exists(path) || Never.Contains(Ext(path)) || MediaOps.IsVideo(path) || MediaOps.IsAudio(path)) return false;
        return IsImage(path) || IsDocument(path) || IsPdf(path) || ShellVerb(path) != null;
    }

    // Verbo de impresión que ofrece el programa asociado: «printto» deja elegir impresora, «print» usa la predeterminada.
    static string ShellVerb(string path)
    {
        try
        {
            var verbs = new ProcessStartInfo(path).Verbs;
            if (verbs.Contains("printto", StringComparer.OrdinalIgnoreCase)) return "printto";
            if (verbs.Contains("print", StringComparer.OrdinalIgnoreCase)) return "print";
        }
        catch { }
        return null;
    }

    // Se llama en el hilo de la interfaz: el cuadro de impresión es una ventana.
    public static void PrintFiles(List<string> files)
    {
        var printable = files.Where(CanPrint).ToList();
        if (printable.Count == 0)
        {
            Notify("Switchify", "Ninguno de estos archivos se puede imprimir.", true);
            return;
        }

        // Elegir páginas solo tiene sentido con un único documento o PDF.
        bool pages = printable.Count == 1 && (IsDocument(printable[0]) || IsPdf(printable[0]));
        PrinterSettings settings;
        // Ventana invisible y siempre delante como dueña del cuadro: así no se queda escondido detrás de otras.
        using (var owner = new Form
        {
            ShowInTaskbar = false, TopMost = true, FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1)
        })
        using (var dialog = new PrintDialog { UseEXDialog = true, AllowSomePages = pages, AllowCurrentPage = false, AllowSelection = false })
        {
            dialog.PrinterSettings = new PrinterSettings();
            if (pages)
            {
                dialog.PrinterSettings.MinimumPage = 1;
                dialog.PrinterSettings.MaximumPage = 9999;
                dialog.PrinterSettings.FromPage = 1;
                dialog.PrinterSettings.ToPage = 9999;
            }
            owner.Show();
            owner.Activate();
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            settings = dialog.PrinterSettings;
        }

        Task.Factory.StartNew(() =>
        {
            var errors = new List<string>();
            foreach (var file in printable)
            {
                try { PrintOne(file, (PrinterSettings)settings.Clone()); }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
            if (errors.Count > 0) Notify("Switchify – error al imprimir", string.Join("\n", errors.Take(3)), true);
            else Notify("Enviado a la impresora ✓",
                        (printable.Count == 1 ? Path.GetFileName(printable[0]) : printable.Count + " archivos") + " → " + settings.PrinterName, false);
        });
    }

    static void Notify(string title, string text, bool error)
    {
        if (EditorForm.Notify != null) EditorForm.Notify(title, text, error);
    }

    // Imprime un archivo con la configuración elegida. Público para poder probarlo con «Microsoft Print to PDF».
    public static void PrintOne(string file, PrinterSettings settings)
    {
        string name = Path.GetFileName(file);
        if (IsImage(file)) PrintImage(file, settings, name);
        else if (IsPdf(file)) PrintPdf(file, settings, name);
        else if (IsDocument(file))
        {
            using (var layout = new DocumentLayout(Documents.LoadBlocks(file), Path.GetDirectoryName(file)))
                PrintLayout(layout, settings, name);
        }
        else PrintWithShell(file, settings);
    }

    static PrintDocument NewDocument(PrinterSettings settings, string name)
    {
        return new PrintDocument
        {
            PrinterSettings = settings,
            DocumentName = name,
            PrintController = new StandardPrintController()   // sin ventanas de «imprimiendo…»
        };
    }

    // Páginas a imprimir según lo elegido en el cuadro (todas, o «de X a Y»), en base 0.
    static void Range(PrinterSettings settings, int count, out int from, out int to)
    {
        from = 0;
        to = count - 1;
        if (settings.PrintRange == PrintRange.SomePages)
        {
            from = Math.Max(0, settings.FromPage - 1);
            to = Math.Min(count - 1, settings.ToPage - 1);
        }
        if (from > to) throw new InvalidOperationException("Las páginas elegidas no existen en el documento.");
    }

    // Documento maquetado (A4). Si el papel es otro (Carta, por ejemplo) se escala para que quepa entero.
    public static void PrintLayout(DocumentLayout layout, PrinterSettings settings, string name)
    {
        using (var doc = NewDocument(settings, name))
        {
            var a4 = settings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
            if (a4 != null) doc.DefaultPageSettings.PaperSize = a4;
            doc.DefaultPageSettings.Landscape = false;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            int from, to;
            Range(settings, layout.Pages.Count, out from, out to);
            int index = from;
            doc.PrintPage += (s, e) =>
            {
                var g = e.Graphics;
                g.PageUnit = GraphicsUnit.Point;
                // El origen está en la zona imprimible: se lleva a la esquina del papel.
                g.TranslateTransform(-e.PageSettings.HardMarginX * 0.72f, -e.PageSettings.HardMarginY * 0.72f);
                float pw = e.PageBounds.Width * 0.72f, ph = e.PageBounds.Height * 0.72f;
                float k = Math.Min(1f, Math.Min(pw / DocumentLayout.PageWidth, ph / DocumentLayout.PageHeight));
                if (k < 0.999f)
                {
                    g.TranslateTransform((pw - DocumentLayout.PageWidth * k) / 2, (ph - DocumentLayout.PageHeight * k) / 2);
                    g.ScaleTransform(k, k);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                foreach (var op in layout.Pages[index]) op(g);
                index++;
                e.HasMorePages = index <= to;
            };
            doc.Print();
        }
    }

    // Rectángulo de `size` encajado (sin deformar) y centrado en `area`.
    static RectangleF Fit(SizeF size, RectangleF area)
    {
        float k = Math.Min(area.Width / size.Width, area.Height / size.Height);
        float w = size.Width * k, h = size.Height * k;
        return new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    static void PrintImage(string file, PrinterSettings settings, string name)
    {
        using (var img = ImageOps.Load(file))
        using (var doc = NewDocument(settings, name))
        {
            doc.DefaultPageSettings.Landscape = img.Width > img.Height;
            doc.DefaultPageSettings.Margins = new Margins(39, 39, 39, 39);   // 1 cm (en centésimas de pulgada)
            doc.PrintPage += (s, e) =>
            {
                var g = e.Graphics;
                // Márgenes medidos desde el borde del papel; el dibujo empieza en la zona imprimible.
                var area = new RectangleF(e.MarginBounds.X - e.PageSettings.HardMarginX, e.MarginBounds.Y - e.PageSettings.HardMarginY,
                                          e.MarginBounds.Width, e.MarginBounds.Height);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, Fit(img.Size, area));
                e.HasMorePages = false;
            };
            doc.Print();
        }
    }

    static void PrintPdf(string file, PrinterSettings settings, string name)
    {
        var pdf = Pdf.OpenDocument(file);
        int count = Pdf.PageCount(pdf);
        int from, to;
        Range(settings, count, out from, out to);
        int index = from;
        using (var doc = NewDocument(settings, name))
        {
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            // Cada página en la orientación que tenga en el PDF.
            doc.QueryPageSettings += (s, e) =>
            {
                var size = Pdf.PageSize(pdf, index);
                e.PageSettings.Landscape = size.Width > size.Height;
            };
            doc.PrintPage += (s, e) =>
            {
                var size = Pdf.PageSize(pdf, index);
                // ~200 ppp: nítido en papel sin que cada página pese demasiado.
                using (var page = Pdf.RenderPage(pdf, index, 200.0 / 96.0))
                {
                    var g = e.Graphics;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(page, Fit(size, g.VisibleClipBounds));
                }
                index++;
                e.HasMorePages = index <= to;
            };
            doc.Print();
        }
    }

    // Otros tipos: el programa asociado imprime (en la impresora elegida si admite «printto»).
    static void PrintWithShell(string file, PrinterSettings settings)
    {
        string verb = ShellVerb(file);
        if (verb == null) throw new NotSupportedException("Ningún programa de este equipo sabe imprimir este tipo de archivo.");
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Verb = verb,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (verb == "printto") psi.Arguments = "\"" + settings.PrinterName + "\"";
        using (Process.Start(psi)) { }
    }
}
