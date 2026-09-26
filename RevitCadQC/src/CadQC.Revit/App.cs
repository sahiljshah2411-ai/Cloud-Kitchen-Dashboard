using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using CadQC.Revit.Commands;
using CadQC.Revit.Services;

namespace CadQC.Revit
{
    /// <summary>Adds the "CAD QC" ribbon tab.</summary>
    public sealed class App : IExternalApplication
    {
        public const string TabName = "CAD QC";
        public static string LastReportPath { get; set; }
        public static IssueNavigator Navigator { get; private set; }

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public Result OnStartup(UIControlledApplication a)
        {
            try { a.CreateRibbonTab(TabName); } catch { /* tab exists */ }
            string dll = Assembly.GetExecutingAssembly().Location;
            string avail = typeof(ProjectOpenAvailability).FullName;

            PushButtonData Big(string name, string text, Type cmd, string tip, Func<BitmapSource> icon) =>
                new PushButtonData(name, text, dll, cmd.FullName) { ToolTip = tip, LargeImage = icon(), AvailabilityClassName = avail };

            PushButtonData Small(string name, string text, Type cmd, string tip, Action<DrawingContext> draw, bool needsProject = true)
            {
                var b = new PushButtonData(name, text, dll, cmd.FullName) { ToolTip = tip, Image = Icons.Vector(draw, 16), LargeImage = Icons.Vector(draw, 32) };
                if (needsProject) b.AvailabilityClassName = avail;
                return b;
            }

            // ── PANEL 1: TECHNICAL QC ───────────────────────────────────
            var qc = a.CreateRibbonPanel(TabName, "Technical QC");
            qc.AddItem(Big("CADQC_RUN", "  Run  \n  CAD QC  ", typeof(RunQcCommand),
                "Compare the Revit model with every DWG/DXF plan in a folder: walls (missing, extra, thickness, position), doors, windows, columns, rooms, grids and dimensions. Issues are highlighted in Revit and marked up in CAD.",
                () => Icons.Vector(Icons.Run, 32)));
            qc.AddItem(Big("CADQC_BROWSER", "  Issue  \n  Browser  ", typeof(IssueBrowserCommand),
                "List of QC issues. Double-click to zoom to an issue; Accept false positives so they stay closed on the next run.",
                () => Icons.Vector(Icons.Browser, 32)));
            qc.AddItem(Big("CADQC_LAYERS", "  Layer  \n  Mapping  ", typeof(LayerMappingCommand),
                "Review which CAD layers are walls, doors, windows, columns, grids and rooms. Saved once per consultant, then used automatically.",
                () => Icons.Vector(Icons.Layers, 32)));
            qc.AddSeparator();
            qc.AddItem(Big("CADQC_CLEAR", "  Clear  \n  QC Views  ", typeof(ClearQcCommand),
                "Delete every QC view with its clouds and linked CAD, and clear QC comments.",
                () => Icons.Vector(Icons.Clear, 32)));

            // ── PANEL 2: SETUP ──────────────────────────────────────────
            var setup = a.CreateRibbonPanel(TabName, "Setup");
            setup.AddStackedItems(
                Small("CADQC_SETTINGS", "Settings", typeof(SettingsCommand), "This project's QC settings (tolerances, checks, floor mapping).", Icons.Settings),
                Small("CADQC_DICT", "Layer Dictionary", typeof(LayerDictionaryCommand), "Open CadLayerDictionary.txt - the layer-name rules (first match wins). Notepad, no rebuild.", Icons.Dictionary, false),
                Small("CADQC_SNAPSHOT", "Export Snapshot", typeof(ExportSnapshotCommand), "Save the model data used by QC to JSON (for the command-line runner).", Icons.Snapshot));
            setup.AddItem(new PushButtonData("CADQC_VER", "  Version  ", dll, typeof(VersionCommand).FullName)
            {
                ToolTip = "Revit CAD QC " + Version + ".",
                LargeImage = Icons.Vector(Icons.VersionTag, 32)
            });

            Navigator = IssueNavigator.Create();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a) => Result.Succeeded;
    }

    /// <summary>
    /// Vector ribbon icons: drawn on a 96 px canvas, then high-quality downscaled to 32 / 16 px,
    /// so they stay crisp on 4K / high-DPI screens. No image files to ship.
    /// </summary>
    internal static class Icons
    {
        private const double S = 96.0;

        public static BitmapSource Vector(Action<DrawingContext> draw, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) draw(dc);
            var rtb = new RenderTargetBitmap((int)S, (int)S, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var scaled = new TransformedBitmap(rtb, new ScaleTransform(size / S, size / S));
            scaled.Freeze();
            return scaled;
        }

        private static SolidColorBrush Br(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        private static Pen Pn(byte r, byte g, byte b, double w) => new Pen(Br(r, g, b), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        private static System.Windows.Rect R(double x, double y, double w, double h) => new System.Windows.Rect(x, y, w, h);
        private static System.Windows.Point P(double x, double y) => new System.Windows.Point(x, y);

        private static readonly SolidColorBrush Wall = Br(70, 70, 70);
        private static readonly SolidColorBrush Red = Br(217, 45, 32);
        private static readonly SolidColorBrush Orange = Br(245, 138, 7);
        private static readonly SolidColorBrush Yellow = Br(212, 165, 0);
        private static readonly SolidColorBrush Blue = Br(47, 111, 222);

        private static void Cloud(DrawingContext dc, System.Windows.Point c, double r, Pen pen)
        {
            int n = 9;
            for (int k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                var p0 = P(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
                var p1 = P(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(p0, false, false);
                    gc.ArcTo(p1, new System.Windows.Size(r * 0.4, r * 0.4), 0, false, SweepDirection.Clockwise, true, false);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }

        // RUN: wall plan with an opening; the wrong wall is clouded red
        public static void Run(DrawingContext dc)
        {
            dc.DrawRectangle(Wall, null, R(8, 14, 80, 10));      // top wall
            dc.DrawRectangle(Wall, null, R(8, 14, 10, 70));      // left wall
            dc.DrawRectangle(Wall, null, R(8, 74, 30, 10));      // bottom wall, opening
            dc.DrawRectangle(Blue, null, R(58, 44, 30, 7));      // thinner Revit wall
            dc.DrawRectangle(null, new Pen(Br(150, 150, 150), 1.5) { DashStyle = DashStyles.Dash }, R(58, 42, 30, 11)); // CAD thickness
            Cloud(dc, P(70, 48), 20, Pn(217, 45, 32, 4));
        }

        // BROWSER: issue list with severity dots
        public static void Browser(DrawingContext dc)
        {
            dc.DrawRoundedRectangle(Br(248, 248, 248), Pn(90, 90, 90, 2.5), R(12, 10, 72, 76), 5, 5);
            var rows = new[] { Red, Orange, Yellow, Blue };
            for (int i = 0; i < 4; i++)
            {
                double y = 24 + i * 17;
                dc.DrawEllipse(rows[i], null, P(26, y), 5, 5);
                dc.DrawLine(Pn(150, 150, 150, 4), P(38, y), P(i % 2 == 0 ? 72 : 64, y));
            }
        }

        // LAYERS: three stacked layer sheets
        public static void Layers(DrawingContext dc)
        {
            var cols = new[] { Br(120, 190, 245), Br(250, 200, 120), Br(70, 70, 70) };
            for (int i = 2; i >= 0; i--)
            {
                double y = 28 + i * 16;
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(P(48, y - 16), true, true);
                    gc.LineTo(P(84, y), true, false);
                    gc.LineTo(P(48, y + 16), true, false);
                    gc.LineTo(P(12, y), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(cols[i], Pn(255, 255, 255, 2.5), g);
            }
        }

        // CLEAR: cloud with a red X
        public static void Clear(DrawingContext dc)
        {
            Cloud(dc, P(44, 44), 28, Pn(150, 150, 150, 3.5));
            var x = Pn(217, 45, 32, 8);
            dc.DrawLine(x, P(60, 58), P(84, 82));
            dc.DrawLine(x, P(84, 58), P(60, 82));
        }

        // SETTINGS: three sliders
        public static void Settings(DrawingContext dc)
        {
            var line = Pn(110, 110, 110, 6);
            double[] knobs = { 30, 62, 44 };
            for (int i = 0; i < 3; i++)
            {
                double y = 24 + i * 24;
                dc.DrawLine(line, P(14, y), P(82, y));
                dc.DrawEllipse(Br(31, 58, 95), null, P(knobs[i], y), 9, 9);
            }
        }

        // DICTIONARY: text page with a regex bar
        public static void Dictionary(DrawingContext dc)
        {
            dc.DrawRoundedRectangle(Br(250, 250, 250), Pn(90, 90, 90, 3), R(18, 8, 60, 80), 4, 4);
            var t = Pn(150, 150, 150, 5);
            dc.DrawLine(t, P(28, 26), P(66, 26));
            dc.DrawLine(t, P(28, 42), P(58, 42));
            dc.DrawLine(Pn(47, 111, 222, 6), P(28, 58), P(66, 58));
            dc.DrawLine(t, P(28, 74), P(52, 74));
        }

        // SNAPSHOT: model box with a down arrow
        public static void Snapshot(DrawingContext dc)
        {
            dc.DrawRectangle(Br(220, 230, 245), Pn(70, 90, 120, 3), R(14, 40, 56, 44));
            var a = Pn(18, 161, 80, 8);
            dc.DrawLine(a, P(70, 10), P(70, 44));
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(P(56, 40), true, true);
                gc.LineTo(P(84, 40), true, false);
                gc.LineTo(P(70, 58), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Br(18, 161, 80), null, g);
        }

        // VERSION: rotated tag
        public static void VersionTag(DrawingContext dc)
        {
            dc.PushTransform(new RotateTransform(-45, 48, 48));
            dc.DrawRoundedRectangle(Br(31, 58, 95), null, R(26, 30, 52, 36), 9, 9);
            dc.DrawEllipse(Br(255, 255, 255), null, P(32, 48), 5, 5);
            dc.Pop();
            var w = Pn(255, 255, 255, 4.5);
            dc.DrawLine(w, P(46, 40), P(50, 60));
            dc.DrawLine(w, P(50, 60), P(56, 40));
        }
    }
}
