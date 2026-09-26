using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RevitCadQC.Core.Engine;
using RevitCadQC.Core.Settings;

namespace RevitCadQC.UI
{
    /// <summary>
    /// First time the tool sees a consultant's drawings it shows every CAD layer with its best guess.
    /// Correct anything wrong once; it is saved as that consultant's profile and never asked again.
    /// </summary>
    public sealed class LayerReviewWindow : Window
    {
        public sealed class Row
        {
            public string Name { get; set; }
            public int Entities { get; set; }
            public string Files { get; set; }
            public string Guess { get; set; }
            public string Category { get; set; }
            public Brush GuessBrush => Guess == "Unknown" ? Ui.Critical : Ui.Muted;
        }

        private static readonly string[] Categories = Enum.GetNames(typeof(LayerCategory)).ToArray();
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private readonly TextBox _name = new TextBox { Width = 260, Margin = new Thickness(6, 2, 6, 2) };
        private readonly CheckBox _hideSmall = Ui.Check("Hide layers with fewer than 5 objects", false);
        private readonly ICollectionView _view;

        public CadLayerProfile Result { get; private set; }

        public LayerReviewWindow(IEnumerable<ScannedLayer> layers, string defaultName, IEnumerable<string> files)
        {
            Title = "CAD QC - confirm the CAD layers (once per consultant)";
            Width = 900; Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            foreach (var l in layers)
            {
                var cat = l.ProfileCategory != LayerCategory.Unknown ? l.ProfileCategory : l.RuleCategory;
                _rows.Add(new Row
                {
                    Name = l.Name,
                    Entities = l.Entities,
                    Files = string.Join(", ", l.Files.Distinct().Take(4)) + (l.Files.Distinct().Count() > 4 ? " …" : ""),
                    Guess = (l.ProfileCategory != LayerCategory.Unknown ? "profile: " + l.ProfileCategory : l.RuleCategory.ToString()),
                    Category = cat.ToString()
                });
            }
            _name.Text = defaultName;
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = o => _hideSmall.IsChecked != true || ((Row)o).Entities >= 5;
            _hideSmall.Checked += (s, e) => _view.Refresh();
            _hideSmall.Unchecked += (s, e) => _view.Refresh();

            var grid = new DataGrid
            {
                ItemsSource = _view,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "CAD layer", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Objects", Binding = new Binding("Entities"), IsReadOnly = true, Width = 70 });
            var guessCol = new DataGridTemplateColumn { Header = "Tool's guess", Width = 130, SortMemberPath = "Guess" };
            var f = new FrameworkElementFactory(typeof(TextBlock));
            f.SetBinding(TextBlock.TextProperty, new Binding("Guess"));
            f.SetBinding(TextBlock.ForegroundProperty, new Binding("GuessBrush"));
            f.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            guessCol.CellTemplate = new DataTemplate { VisualTree = f };
            grid.Columns.Add(guessCol);
            grid.Columns.Add(new DataGridComboBoxColumn { Header = "Use as", ItemsSource = Categories, SelectedItemBinding = new Binding("Category") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "In files", Binding = new Binding("Files"), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });

            var header = Ui.Stack(
                Ui.Label("Check what each CAD layer is. Walls, doors, windows, columns and grids are compared with Revit; " +
                         "Ignore is never read; Unknown is skipped (never guessed).", 12, true),
                Ui.Label("Files: " + string.Join(", ", files.Take(8)) + (files.Count() > 8 ? " …" : ""), 11, false, Ui.Muted),
                Ui.Row(Ui.Label("Consultant / profile name:"), _name, _hideSmall),
                Ui.Label("Red guesses were not recognised. Fix those first - especially layers with many objects.", 11, false, Ui.Critical));
            var buttons = Ui.Row(
                Ui.Button("Cancel", () => { DialogResult = false; }),
                Ui.Button("Run without saving", () => { Result = Build(); DialogResult = true; }),
                Ui.Button("Save profile and run QC", SaveAndRun, primary: true));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(header);
            dock.Children.Add(buttons);
            dock.Children.Add(grid);
            Content = dock;
        }

        private CadLayerProfile Build()
        {
            var p = new CadLayerProfile { Name = string.IsNullOrWhiteSpace(_name.Text) ? "Consultant" : _name.Text.Trim() };
            foreach (var r in _rows) p.Layers[r.Name] = r.Category ?? LayerCategory.Unknown.ToString();
            return p;
        }

        private void SaveAndRun()
        {
            var p = Build();
            if (!p.Layers.Values.Any(v => v == LayerCategory.Wall.ToString()))
            {
                if (MessageBox.Show(this, "No layer is set to Wall, so no wall can be checked. Continue anyway?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
            }
            try
            {
                var path = CadProfileStore.Save(p);
                Result = p;
                MessageBox.Show(this, "Saved. Drawings from this consultant will use it automatically.\n\n" + path, Title);
                DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not save the profile: " + ex.Message, Title); }
        }
    }
}
