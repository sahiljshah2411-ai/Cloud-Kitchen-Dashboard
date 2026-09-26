using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RevitCadQC.Core.Engine;

namespace RevitCadQC.UI
{
    /// <summary>Modal progress dialog that runs pure .NET work (the QC engine) on a worker thread. Never touches the Revit API.</summary>
    public sealed class ProgressWindow : Window
    {
        private readonly ProgressBar _bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 8) };
        private readonly TextBlock _msg = Ui.Label("Starting…");
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Exception _error;
        private object _result;

        private ProgressWindow(string title)
        {
            Title = title;
            Width = 520; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            var cancel = Ui.Button("Cancel", () => _cts.Cancel());
            cancel.HorizontalAlignment = HorizontalAlignment.Right;
            Content = new StackPanel { Margin = new Thickness(16), Children = { _msg, _bar, cancel } };
        }

        public static T Run<T>(string title, Func<IProgress<QcProgress>, CancellationToken, T> work)
        {
            var w = new ProgressWindow(title);
            var progress = new Progress<QcProgress>(p => { w._bar.Value = p.Percent; w._msg.Text = p.Message; });
            w.Loaded += async (s, e) =>
            {
                try { w._result = await Task.Run(() => work(progress, w._cts.Token)); }
                catch (Exception ex) { w._error = ex; }
                w.DialogResult = w._error == null;
            };
            w.ShowDialog();
            if (w._error is OperationCanceledException) return default;
            if (w._error != null) throw new InvalidOperationException(w._error.Message, w._error);
            return (T)w._result;
        }
    }
}
