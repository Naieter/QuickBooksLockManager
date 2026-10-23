using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;
using DR = System.Drawing;
using WF = System.Windows.Forms;

namespace QBLockServiceSetup;

// The system-tray presence of the server program: a status icon (green = service running,
// red = stopped, grey = unknown/starting), a tooltip, and a small menu. It only shows while the
// service is installed. Closing the window hides it here instead of exiting.
internal sealed class TrayManager : IDisposable
{
    private readonly MainWindow _window;
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _statusItem;
    private readonly DispatcherTimer _timer;
    private readonly DR.Icon _green, _red, _grey;
    private bool _balloonShown;

    public TrayManager(MainWindow window)
    {
        _window = window;
        _green = MakeIcon(DR.Color.FromArgb(0x16, 0xA3, 0x4A));
        _red   = MakeIcon(DR.Color.FromArgb(0xDC, 0x26, 0x26));
        _grey  = MakeIcon(DR.Color.FromArgb(0x64, 0x74, 0x8B));

        _statusItem = new WF.ToolStripMenuItem("Checking...") { Enabled = false };

        var open = new WF.ToolStripMenuItem("Open");
        open.Font = new DR.Font(open.Font, DR.FontStyle.Bold);   // the default action (double-click)
        open.Click += (_, _) => _window.ShowFromTray();

        var export = new WF.ToolStripMenuItem("Export connection file...");
        export.Click += (_, _) => _window.ExportFromTray();

        var exit = new WF.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ((App)Application.Current).ExitApplication();

        var menu = new WF.ContextMenuStrip();
        menu.Items.AddRange(new WF.ToolStripItem[]
        {
            _statusItem, new WF.ToolStripSeparator(), open, export, new WF.ToolStripSeparator(), exit
        });

        _icon = new WF.NotifyIcon { ContextMenuStrip = menu, Icon = _grey, Text = "QuickBooks Lock Service", Visible = false };
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // Re-reads the service state: shows/hides the icon and updates its colour and tooltip.
    public void Refresh()
    {
        var status = LockServiceInfo.Status();
        _icon.Visible = status != null;   // nothing to show a tray icon for when it isn't installed
        if (status == null) return;

        var (text, icon) = status switch
        {
            ServiceControllerStatus.Running => ("Running", _green),
            ServiceControllerStatus.Stopped => ("Stopped", _red),
            _ => (status.ToString()!, _grey)
        };
        _icon.Icon = icon;
        _icon.Text = $"QuickBooks Lock Service - {text}";   // the tooltip is capped at 63 characters
        _statusItem.Text = $"Lock service: {text}";
    }

    // First time the window is sent to the tray: tell the admin where it went.
    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(6000, "QuickBooks Lock Service",
            "Still running here in the tray. Right-click the icon for options. The lock service keeps running either way.",
            WF.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;   // otherwise a ghost icon lingers until the mouse passes over it
        _icon.Dispose();
        _green.Dispose(); _red.Dispose(); _grey.Dispose();
    }

    // A round coloured badge with a white "Q", drawn at runtime so there is no icon file to ship.
    private static DR.Icon MakeIcon(DR.Color color)
    {
        using var bmp = new DR.Bitmap(32, 32);
        using (var g = DR.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = DR.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = DR.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(DR.Color.Transparent);
            using (var fill = new DR.SolidBrush(color)) g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new DR.Font("Segoe UI", 18, DR.FontStyle.Bold, DR.GraphicsUnit.Pixel);
            var size = g.MeasureString("Q", font);
            g.DrawString("Q", font, DR.Brushes.White, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
        }
        var hIcon = bmp.GetHicon();
        try { return (DR.Icon)DR.Icon.FromHandle(hIcon).Clone(); }   // Clone owns its own copy
        finally { DestroyIcon(hIcon); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
