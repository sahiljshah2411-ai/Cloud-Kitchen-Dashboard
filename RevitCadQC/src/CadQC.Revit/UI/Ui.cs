using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CadQC.Revit.UI
{
    /// <summary>Small helpers for building the WPF windows in code (no XAML, so one source builds for .NET Framework 4.8 and .NET 8).</summary>
    internal static class Ui
    {
        public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F));
        public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85));
        public static readonly Brush Critical = new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20));
        public static readonly Brush Major = new SolidColorBrush(Color.FromRgb(0xF5, 0x8A, 0x07));
        public static readonly Brush Minor = new SolidColorBrush(Color.FromRgb(0xD4, 0xA5, 0x00));
        public static readonly Brush Info = new SolidColorBrush(Color.FromRgb(0x1D, 0x8C, 0xB8));

        public static TextBlock Label(string text, double size = 12, bool bold = false, Brush fg = null) => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = fg ?? Brushes.Black,
            Margin = new Thickness(0, 2, 0, 2),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        public static Button Button(string text, Action onClick, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(4),
                MinWidth = 80
            };
            if (primary) { b.Background = Accent; b.Foreground = Brushes.White; b.FontWeight = FontWeights.SemiBold; }
            b.Click += (s, e) => onClick();
            return b;
        }

        public static CheckBox Check(string text, bool value) => new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 12, 3) };

        public static TextBox Number(double v) => new TextBox { Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 70, Margin = new Thickness(4, 2, 12, 2) };

        public static double Read(TextBox t, double fallback) =>
            double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        public static GroupBox Group(string header, UIElement content) => new GroupBox
        {
            Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold },
            Content = content,
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(8)
        };

        public static StackPanel Row(params UIElement[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            foreach (var c in children) p.Children.Add(c);
            return p;
        }

        public static StackPanel Stack(params UIElement[] children)
        {
            var p = new StackPanel();
            foreach (var c in children) p.Children.Add(c);
            return p;
        }

        public static Brush SeverityBrush(string sev)
        {
            switch (sev)
            {
                case "Critical": return Critical;
                case "Major": return Major;
                case "Minor": return Minor;
                default: return Info;
            }
        }

        public static string PickFolder(string current, string title)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = title, ShowNewFolderButton = true })
            {
                if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current)) dlg.SelectedPath = current;
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
            }
        }
    }
}
