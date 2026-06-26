using System.Windows;
using System.Windows.Controls;

namespace QBLockManager.Views;

public class UpdateProgressWindow : Window
{
    private readonly TextBlock   _status;
    private readonly ProgressBar _bar;

    public UpdateProgressWindow(string tagName)
    {
        Title  = "QuickBooks Lock Manager — Updating";
        Width  = 420;
        Height = 115;
        ResizeMode             = ResizeMode.NoResize;
        WindowStartupLocation  = WindowStartupLocation.CenterScreen;
        ShowInTaskbar          = true;
        // Prevent the user from closing mid-download
        Closing += (_, e) => e.Cancel = true;

        _status = new TextBlock
        {
            Text       = $"Downloading update {tagName}...",
            Margin     = new Thickness(20, 18, 20, 6),
            FontSize   = 13
        };

        _bar = new ProgressBar
        {
            Margin  = new Thickness(20, 0, 20, 18),
            Height  = 20,
            Minimum = 0,
            Maximum = 1,
            Value   = 0
        };

        var panel = new StackPanel();
        panel.Children.Add(_status);
        panel.Children.Add(_bar);
        Content = panel;
    }

    public void SetProgress(double fraction)
    {
        Dispatcher.Invoke(() =>
        {
            _bar.Value    = fraction;
            _status.Text  = $"Downloading update... {fraction:P0}";
        });
    }

    // Call this before Application.Current.Shutdown() — bypasses the Closing cancel.
    public void ForceClose()
    {
        Closing -= null;
        Dispatcher.Invoke(() =>
        {
            _status.Text = "Update complete. Relaunching...";
            _bar.Value   = 1;
        });
    }
}
