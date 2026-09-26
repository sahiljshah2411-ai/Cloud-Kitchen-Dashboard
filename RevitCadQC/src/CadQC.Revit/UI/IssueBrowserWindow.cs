using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CadQC.Core.Report;
using CadQC.Revit.Services;

namespace CadQC.Revit.UI
{
    /// <summary>Modeless issue register inside Revit. Double-click an issue to jump to it; statuses are saved back to the QC report.</summary>
    public sealed class IssueBrowserWindow : Window
    {
        public sealed class Row : INotifyPropertyChanged
        {
            public QcIssue Issue { get; }
            public Row(QcIssue i) { Issue = i; }
            public string Id => Issue.Id;
            public string Severity => Issue.Severity.ToString();
            public string Floor => Issue.Floor;
            public string Category => Issue.Category;
            public string Type => Issue.IssueType;
            public string Title => Issue.Title;
            public string Cad => Issue.CadValue;
            public string Revit => Issue.RevitValue;
            public string Status => Issue.Status.ToString();
            public Brush SeverityBrush => Ui.SeverityBrush(Severity);
            public event PropertyChangedEventHandler PropertyChanged;
            public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }

        private static IssueBrowserWindow _instance;
        private readonly string _reportPath;
        private readonly QcReport _report;
        private readonly IssueNavigator _nav;
        private readonly List<Row> _all;
        private readonly ICollectionView _viewSrc;
        private readonly DataGrid _grid;
        private readonly ComboBox _floor = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _sev = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _cat = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 6, 0) };
        private readonly ComboBox _st = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBox _txt = new TextBox { Width = 160 };
        private readonly TextBlock _detail = Ui.Label("", 12);
        private readonly TextBlock _count = Ui.Label("", 11, false, Ui.Muted);

        public static void ShowFor(string reportPath, IssueNavigator nav)
        {
            if (_instance != null) { _instance.Close(); _instance = null; }
            var r = QcReport.LoadJson(reportPath);
            if (r == null) { MessageBox.Show("No QC report found at\n" + reportPath + "\n\nRun the QC first.", "CAD QC"); return; }
            _instance = new IssueBrowserWindow(reportPath, r, nav);
            new System.Windows.Interop.WindowInteropHelper(_instance) { Owner = Process.GetCurrentProcess().MainWindowHandle };
            _instance.Closed += (s, e) => _instance = null;
            _instance.Show();
        }

        private IssueBrowserWindow(string reportPath, QcReport report, IssueNavigator nav)
        {
            _reportPath = reportPath;
            _report = report;
            _nav = nav;
            Title = $"CAD QC issues — {report.ProjectName}  ({report.RunUtc.ToLocalTime():dd MMM yyyy HH:mm})";
            Width = 1100; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _all = report.Issues.Select(i => new Row(i)).ToList();
            _viewSrc = CollectionViewSource.GetDefaultView(_all);
            _viewSrc.Filter = o => Match((Row)o);

            Fill(_floor, "All floors", _all.Select(r => r.Floor));
            Fill(_sev, "All", new[] { "Critical", "Major", "Minor", "Info" });
            Fill(_cat, "All categories", _all.Select(r => r.Category));
            Fill(_st, "Open", new[] { "New", "Open", "Accepted", "Resolved", "Everything" });
            foreach (var c in new[] { _floor, _sev, _cat, _st }) c.SelectionChanged += (s, e) => Refilter();
            _txt.TextChanged += (s, e) => Refilter();

            _grid = new DataGrid
            {
                ItemsSource = _viewSrc,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeight = 24
            };
            var sevCol = new DataGridTemplateColumn { Header = "Severity", SortMemberPath = "Severity", Width = 80 };
            var f = new FrameworkElementFactory(typeof(TextBlock));
            f.SetBinding(TextBlock.TextProperty, new Binding("Severity"));
            f.SetBinding(TextBlock.ForegroundProperty, new Binding("SeverityBrush"));
            f.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            f.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            sevCol.CellTemplate = new DataTemplate { VisualTree = f };
            _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding("Id"), Width = 110 });
            _grid.Columns.Add(sevCol);
            _grid.Columns.Add(new DataGridTextColumn { Header = "Floor", Binding = new Binding("Floor"), Width = 110 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new Binding("Category"), Width = 90 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Issue", Binding = new Binding("Title"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "CAD", Binding = new Binding("Cad"), Width = 130 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Revit", Binding = new Binding("Revit"), Width = 150 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = 70 });
            _grid.MouseDoubleClick += (s, e) => GoTo(false);
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.KeyDown += (s, e) => { if (e.Key == Key.Enter) { GoTo(false); e.Handled = true; } };

            var filters = Ui.Row(Ui.Label("Filter: "), _floor, _sev, _cat, _st, _txt);
            var actions = Ui.Row(
                Ui.Button("Zoom to issue", () => GoTo(false), primary: true),
                Ui.Button("Select elements", () => GoTo(true)),
                Ui.Button("Accept (not an error)", () => SetStatus(IssueStatus.Accepted)),
                Ui.Button("Re-open", () => SetStatus(IssueStatus.Open)),
                Ui.Button("HTML report", () => OpenFile(Path.Combine(Path.GetDirectoryName(_reportPath), "QC_Report.html"))),
                Ui.Button("Excel", () => OpenFile(Path.Combine(Path.GetDirectoryName(_reportPath), "QC_Issues.xlsx"))),
                Ui.Button("CAD markups", () => OpenFile(Path.Combine(Path.GetDirectoryName(_reportPath), "CAD_Markup"))));

            var summary = Ui.Label($"{report.Count(Severity.Critical)} critical · {report.Count(Severity.Major)} major · {report.Count(Severity.Minor)} minor · {report.Count(Severity.Info)} info · " +
                                   $"{report.Issues.Count(i => i.Status == IssueStatus.Resolved)} resolved since last run", 12, true);
            var top = Ui.Stack(summary, filters, _count);
            var bottom = Ui.Stack(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0), Child = _detail }, actions);
            var dock = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(_grid);
            Content = dock;
            Refilter();
        }

        private static void Fill(ComboBox c, string all, IEnumerable<string> vals)
        {
            c.Items.Add(all);
            foreach (var v in vals.Where(v => !string.IsNullOrEmpty(v)).Distinct().OrderBy(v => v)) c.Items.Add(v);
            c.SelectedIndex = 0;
        }

        private bool Match(Row r)
        {
            if (_floor.SelectedIndex > 0 && r.Floor != (string)_floor.SelectedItem) return false;
            if (_sev.SelectedIndex > 0 && r.Severity != (string)_sev.SelectedItem) return false;
            if (_cat.SelectedIndex > 0 && r.Category != (string)_cat.SelectedItem) return false;
            var st = (string)_st.SelectedItem;
            if (st == "Open" && (r.Issue.Status == IssueStatus.Resolved || r.Issue.Status == IssueStatus.Accepted)) return false;
            if (st != "Open" && st != "Everything" && r.Status != st) return false;
            var t = _txt.Text?.Trim();
            if (!string.IsNullOrEmpty(t))
            {
                var hay = string.Join(" ", r.Id, r.Title, r.Type, r.Cad, r.Revit, r.Issue.Description, string.Join(" ", r.Issue.RevitElementIds));
                if (hay.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        private void Refilter()
        {
            _viewSrc.Refresh();
            _count.Text = $"{_viewSrc.Cast<object>().Count()} of {_all.Count} issues shown. Double-click to zoom to an issue in its QC plan.";
        }

        private void ShowDetail()
        {
            if (!(_grid.SelectedItem is Row r)) { _detail.Text = ""; return; }
            var i = r.Issue;
            _detail.Text = $"{i.Id} · {i.IssueType}\n{i.Description}" +
                           (i.RevitElementIds.Count > 0 ? $"\nRevit ids: {string.Join(", ", i.RevitElementIds)}" : "") +
                           (!string.IsNullOrEmpty(i.CadLayer) ? $"   CAD layer: {i.CadLayer}" : "");
        }

        private void GoTo(bool selectOnly)
        {
            if (_grid.SelectedItem is Row r) _nav.Show(r.Issue, selectOnly);
        }

        private void SetStatus(IssueStatus st)
        {
            foreach (var r in _grid.SelectedItems.Cast<Row>().ToList())
            {
                r.Issue.Status = st;
                r.Refresh();
            }
            try { _report.SaveJson(_reportPath); }
            catch (Exception ex) { MessageBox.Show(this, "Could not save the status: " + ex.Message); }
            Refilter();
        }

        private static void OpenFile(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show("Not found: " + path); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
