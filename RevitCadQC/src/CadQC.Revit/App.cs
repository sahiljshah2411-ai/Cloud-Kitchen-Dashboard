using System;
using System.Reflection;
using System.Windows;
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

        public Result OnStartup(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* tab exists */ }
            var panel = app.CreateRibbonPanel(TabName, "Technical QC");
            string asm = Assembly.GetExecutingAssembly().Location;

            PushButtonData Btn(string name, string text, Type cmd, string tip, string glyph, Color color)
            {
                return new PushButtonData(name, text, asm, cmd.FullName)
                {
                    ToolTip = tip,
                    LargeImage = Icon(glyph, color, 32),
                    Image = Icon(glyph, color, 16),
                    AvailabilityClassName = typeof(ProjectOpenAvailability).FullName
                };
            }

            var run = panel.AddItem(Btn("CadQcRun", "Run\nCAD QC", typeof(RunQcCommand),
                "Compare the Revit model with every DWG/DXF plan in a folder: walls (missing, extra, thickness, position), doors, windows, columns, rooms, grids and dimensions. Issues are highlighted in Revit and marked up in CAD.",
                "QC", Color.FromRgb(0xD9, 0x2D, 0x20))) as PushButton;
            run?.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, "https://www.opendesign.com/guestfiles/oda_file_converter"));
            panel.AddItem(Btn("CadQcBrowser", "Issue\nBrowser", typeof(IssueBrowserCommand),
                "Open the list of QC issues. Double-click to zoom to an issue; accept false positives so they stay closed next run.", "≡", Color.FromRgb(0x1F, 0x3A, 0x5F)));
            panel.AddSeparator();
            panel.AddStackedItems(
                Btn("CadQcSnapshot", "Export Snapshot", typeof(ExportSnapshotCommand), "Save the model data used by QC to JSON (for the command-line runner).", "⇩", Color.FromRgb(0x12, 0xA1, 0x50)),
                Btn("CadQcSettings", "Settings", typeof(SettingsCommand), "Open this project's QC settings (layers, tolerances, floor mapping).", "⚙", Color.FromRgb(0x66, 0x70, 0x85)),
                Btn("CadQcClear", "Clear QC Views", typeof(ClearQcCommand), "Delete the QC plans, clouds and linked CAD created by the QC.", "✕", Color.FromRgb(0x98, 0x2A, 0x2A)));

            Navigator = IssueNavigator.Create();
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>Draws a simple round icon so the add-in ships without image files.</summary>
        private static BitmapSource Icon(string glyph, Color color, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var brush = new SolidColorBrush(color);
                dc.DrawRoundedRectangle(brush, null, new Rect(1, 1, size - 2, size - 2), size / 5.0, size / 5.0);
                var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    glyph.Length > 1 ? size * 0.42 : size * 0.62, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
    }
}
