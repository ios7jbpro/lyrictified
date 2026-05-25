using System.Windows.Threading;

namespace Lyrictified;

internal interface ITrayIconHost
{
    void ShowFromTray();
    void OpenSettingsFromTray();
    void ExitApp();
}

internal sealed class TrayIcon : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITrayIconHost _window;
    private System.Windows.Forms.NotifyIcon? _icon;
    private bool _disposed;

    public TrayIcon(ITrayIconHost window)
    {
        _window = window;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _icon = new System.Windows.Forms.NotifyIcon();

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            _icon.Icon = new System.Drawing.Icon(iconPath);
        }
        else
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }

        _icon.Text = "Lyrictified";
        _icon.Visible = true;

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (_, _) => _dispatcher.Invoke(() => _window.ShowFromTray()));
        contextMenu.Items.Add("Settings", null, (_, _) => _dispatcher.Invoke(() =>
        {
            _window.ShowFromTray();
            _window.OpenSettingsFromTray();
        }));
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => _dispatcher.Invoke(() => _window.ExitApp()));
        _icon.ContextMenuStrip = contextMenu;

        _icon.DoubleClick += (_, _) => _dispatcher.Invoke(() => _window.ShowFromTray());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon?.Visible = false;
        _icon?.Dispose();
        _icon = null;
    }
}