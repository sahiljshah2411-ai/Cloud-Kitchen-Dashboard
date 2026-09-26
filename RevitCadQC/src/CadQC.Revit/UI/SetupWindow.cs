using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CadQC.Core.Extraction;
using CadQC.Core.Settings;

namespace CadQC.Revit.UI
{
    /// <summary>Run dialog: CAD folder, output folder, checks, key tolerances and the (optional) floor mapping.</summary>
    public sealed class SetupWindow : Window
    {
        public sealed class MappingRow
        {
            public string CadFile { get; set; }
            public string RegionTitle { get; set; }
            public string LevelName { get; set; }
            public bool Enabled { get; set; } = true;
        }

        private readonly QcSettings _s;
        private readonly string[] _levels;
        private readonly string _settingsPath;
        private readonly TextBox _cad = new TextBox { MinWidth = 420, Margin = new Thickness(0, 2, 4, 2) };
        private readonly TextBox _out = new TextBox { MinWidth = 420, Margin = new Thickness(0, 2, 4, 2) };
        private readonly ObservableCollection<MappingRow> _rows = new ObservableCollection<MappingRow>();
        private readonly CheckBox _walls, _doors, _windows, _cols, _rooms, _grids, _dims, _drafting, _extra, _links, _views, _link, _v3d, _comments, _sub;
        private readonly TextBox _thkTol, _posTol, _openTol, _minLen, _colTol;
        private readonly ComboBox _widthMode = new ComboBox { Width = 90, Margin = new Thickness(4, 2, 12, 2) };
        private readonly TextBlock _converter = Ui.Label("", 11, false, Ui.Muted);

        public SetupWindow(QcSettings settings, string[] levelNames, string settingsPath)
        {
            _s = settings;
            _levels = levelNames;
            _settingsPath = settingsPath;
            Title = "Revit vs CAD — Technical QC";
            Width = 780; Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            _cad.Text = settings.CadFolder;
            _out.Text = settings.OutputFolder;
            _walls = Ui.Check("Walls (missing / extra / thickness / position)", settings.CheckWalls);
            _doors = Ui.Check("Doors", settings.CheckDoors);
            _windows = Ui.Check("Windows", settings.CheckWindows);
            _cols = Ui.Check("Columns", settings.CheckColumns);
            _rooms = Ui.Check("Rooms (name / size / area)", settings.CheckRooms);
            _grids = Ui.Check("Grids", settings.CheckGrids);
            _dims = Ui.Check("CAD dimensions vs model", settings.CheckDimensions);
            _drafting = Ui.Check("CAD drafting errors", settings.CheckCadDrafting);
            _extra = Ui.Check("Report Revit elements not in CAD", settings.ReportExtraRevitElements);
            _links = Ui.Check("Include linked Revit models", settings.IncludeLinkedModels);
            _views = Ui.Check("Create QC floor plans with clouds", settings.CreateQcViews);
            _link = Ui.Check("Link the CAD into the QC plans", settings.LinkCadIntoQcViews);
            _v3d = Ui.Check("Create QC 3D view", settings.Create3dQcView);
            _comments = Ui.Check("Write issue IDs into element Comments", settings.WriteIssueIdsToComments);
            _sub = Ui.Check("Include subfolders", settings.IncludeSubfolders);
            _thkTol = Ui.Number(settings.WallThicknessTol);
            _posTol = Ui.Number(settings.WallPositionTolMinor);
            _openTol = Ui.Number(settings.OpeningWidthTol);
            _minLen = Ui.Number(settings.MinIssueLength);
            _colTol = Ui.Number(settings.ColumnSizeTol);
            foreach (var m in new[] { "Auto", "Total", "Core" }) _widthMode.Items.Add(m);
            _widthMode.SelectedItem = settings.RevitWallWidthMode ?? "Auto";
            foreach (var m in settings.FloorMappings)
                _rows.Add(new MappingRow { CadFile = m.CadFile, LevelName = m.LevelName, RegionTitle = m.RegionTitle, Enabled = m.Enabled });

            var root = new DockPanel { Margin = new Thickness(14) };
            var buttons = Ui.Row(
                Ui.Button("Advanced settings (JSON)…", OpenJson),
                new TextBlock { Width = 180 },
                Ui.Button("Cancel", () => { DialogResult = false; }),
                Ui.Button("Run QC", Run, primary: true));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var body = new StackPanel();
            body.Children.Add(Ui.Label("Compare the Revit model against the CAD plans, element by element.", 13, true));
            body.Children.Add(Ui.Label("Point to the folder with the DWG/DXF plans. DWG files are converted automatically; nothing needs to be exported by hand.", 11, false, Ui.Muted));

            body.Children.Add(Ui.Group("CAD drawings", Ui.Stack(
                Ui.Label("CAD folder"),
                Ui.Row(_cad, Ui.Button("Browse…", () => { var f = Ui.PickFolder(_cad.Text, "Folder with the CAD plans (DWG/DXF)"); if (f != null) _cad.Text = f; })),
                _sub,
                Ui.Label("Output folder (empty = <CAD folder>\\_RevitCadQC)"),
                Ui.Row(_out, Ui.Button("Browse…", () => { var f = Ui.PickFolder(_out.Text, "Where to write the QC reports"); if (f != null) _out.Text = f; })),
                _converter)));

            var checks = new WrapPanel();
            foreach (var c in new[] { _walls, _doors, _windows, _cols, _rooms, _grids, _dims, _drafting, _extra, _links }) checks.Children.Add(c);
            body.Children.Add(Ui.Group("What to check", checks));

            body.Children.Add(Ui.Group("Tolerances (mm)", Ui.Stack(
                Ui.Row(Ui.Label("Wall thickness ±"), _thkTol, Ui.Label("Wall position ±"), _posTol, Ui.Label("Min. issue length"), _minLen),
                Ui.Row(Ui.Label("Door/window width ±"), _openTol, Ui.Label("Column size ±"), _colTol, Ui.Label("Compare CAD thickness with Revit"), _widthMode))));

            var hl = new WrapPanel();
            foreach (var c in new[] { _views, _link, _v3d, _comments }) hl.Children.Add(c);
            body.Children.Add(Ui.Group("Highlight in Revit", hl));

            var grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = true,
                Height = 170,
                HeadersVisibility = DataGridHeadersVisibility.Column
            };
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Use", Binding = new Binding("Enabled") });
            grid.Columns.Add(new DataGridTextColumn { Header = "CAD file (name or part of it)", Binding = new Binding("CadFile"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Plan title in sheet (optional)", Binding = new Binding("RegionTitle"), Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Revit level", ItemsSource = _levels, SelectedItemBinding = new Binding("LevelName"), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
            body.Children.Add(Ui.Group("Floor mapping (optional)", Ui.Stack(
                Ui.Label("Floors are matched automatically from file names and plan titles (e.g. \"2ND FLOOR PLAN\" → Level 2). Add rows only to override.", 11, false, Ui.Muted),
                Ui.Row(Ui.Button("Suggest from CAD folder", Scan), Ui.Button("Clear", () => _rows.Clear())),
                grid)));

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            Loaded += (s, e) =>
            {
                var conv = CadQC.Core.Conversion.DwgConverter.FindOdaConverter() ?? CadQC.Core.Conversion.DwgConverter.FindAcCoreConsole();
                _converter.Text = conv != null
                    ? "DWG converter found: " + conv
                    : "No DWG converter found: install the free ODA File Converter (opendesign.com) or AutoCAD. DXF files work without it.";
                _converter.Foreground = conv != null ? Ui.Muted : Ui.Critical;
            };
        }

        private void Scan()
        {
            if (!Directory.Exists(_cad.Text)) { MessageBox.Show(this, "Choose the CAD folder first."); return; }
            var files = Directory.GetFiles(_cad.Text, "*.*", _sub.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) && !f.Contains("_RevitCadQC"));
            _rows.Clear();
            foreach (var f in files)
            {
                var floors = TextParsing.ParseFloors(Path.GetFileNameWithoutExtension(f));
                string level = null;
                if (floors.Count > 0)
                    level = _levels.FirstOrDefault(l => TextParsing.ParseFloors(l).SequenceEqual(floors.Take(1)))
                            ?? _levels.FirstOrDefault(l => TextParsing.ParseFloors(l).Contains(floors[0]));
                _rows.Add(new MappingRow { CadFile = Path.GetFileName(f), LevelName = level, Enabled = level != null });
            }
            if (_rows.Count == 0) MessageBox.Show(this, "No DWG/DXF files found in that folder.");
        }

        private void OpenJson()
        {
            Collect();
            _s.Save(_settingsPath);
            try { Process.Start(new ProcessStartInfo(_settingsPath) { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _settingsPath + "\"")); }
            MessageBox.Show(this, "Edit and save the JSON (layer names, tolerances, thickness equivalents…), then close this message to reload it.", "Advanced settings");
            var reloaded = QcSettings.Load(_settingsPath);
            // copy everything back (keeps this dialog in sync)
            foreach (var p in typeof(QcSettings).GetProperties().Where(p => p.CanRead && p.CanWrite)) p.SetValue(_s, p.GetValue(reloaded));
        }

        private void Collect()
        {
            _s.CadFolder = _cad.Text.Trim();
            _s.OutputFolder = _out.Text.Trim();
            _s.IncludeSubfolders = _sub.IsChecked == true;
            _s.CheckWalls = _walls.IsChecked == true;
            _s.CheckDoors = _doors.IsChecked == true;
            _s.CheckWindows = _windows.IsChecked == true;
            _s.CheckColumns = _cols.IsChecked == true;
            _s.CheckRooms = _rooms.IsChecked == true;
            _s.CheckGrids = _grids.IsChecked == true;
            _s.CheckDimensions = _dims.IsChecked == true;
            _s.CheckCadDrafting = _drafting.IsChecked == true;
            _s.ReportExtraRevitElements = _extra.IsChecked == true;
            _s.IncludeLinkedModels = _links.IsChecked == true;
            _s.CreateQcViews = _views.IsChecked == true;
            _s.LinkCadIntoQcViews = _link.IsChecked == true;
            _s.Create3dQcView = _v3d.IsChecked == true;
            _s.WriteIssueIdsToComments = _comments.IsChecked == true;
            _s.WallThicknessTol = Ui.Read(_thkTol, _s.WallThicknessTol);
            _s.WallPositionTolMinor = Ui.Read(_posTol, _s.WallPositionTolMinor);
            _s.OpeningWidthTol = Ui.Read(_openTol, _s.OpeningWidthTol);
            _s.MinIssueLength = Ui.Read(_minLen, _s.MinIssueLength);
            _s.ColumnSizeTol = Ui.Read(_colTol, _s.ColumnSizeTol);
            _s.RevitWallWidthMode = _widthMode.SelectedItem as string ?? "Auto";
            _s.FloorMappings = _rows.Where(r => !string.IsNullOrWhiteSpace(r.CadFile) && !string.IsNullOrWhiteSpace(r.LevelName))
                .Select(r => new FloorMapping { CadFile = r.CadFile, LevelName = r.LevelName, RegionTitle = r.RegionTitle, Enabled = r.Enabled }).ToList();
        }

        private void Run()
        {
            Collect();
            if (!Directory.Exists(_s.CadFolder)) { MessageBox.Show(this, "The CAD folder does not exist.", Title); return; }
            DialogResult = true;
        }
    }
}
