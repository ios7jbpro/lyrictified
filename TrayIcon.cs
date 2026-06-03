using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lyrictified.Settings;

namespace Lyrictified;

internal interface ITrayIconHost
{
    void ShowFromTray();
    void OpenSettingsFromTray();
    void ExitApp();
    DisplayMode CurrentDisplayMode { get; }
    void SwitchDisplayMode(DisplayMode mode);
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

        _icon.MouseClick += OnTrayIconMouseClick;
        _icon.DoubleClick += (_, _) => _dispatcher.Invoke(() => _window.ShowFromTray());
    }

    private void OnTrayIconMouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left)
        {
            _dispatcher.Invoke(() => _window.ShowFromTray());
            return;
        }

        if (e.Button != System.Windows.Forms.MouseButtons.Right)
        {
            return;
        }

        _dispatcher.Invoke(() =>
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                Style = (Style)System.Windows.Application.Current.FindResource("CustomContextMenuStyle")
            };

            var showItem = new System.Windows.Controls.MenuItem
            {
                Header = "Show",
                Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuItemStyle")
            };
            showItem.Click += (_, _) => _window.ShowFromTray();

            var settingsItem = new System.Windows.Controls.MenuItem
            {
                Header = "Settings",
                Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuItemStyle")
            };
            settingsItem.Click += (_, _) =>
            {
                _window.ShowFromTray();
                _window.OpenSettingsFromTray();
            };

            var modeItem = new System.Windows.Controls.MenuItem
            {
                Header = "Mode",
                Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuItemStyle")
            };

            var currentMode = _window.CurrentDisplayMode;
            foreach (DisplayMode mode in System.Enum.GetValues(typeof(DisplayMode)).Cast<DisplayMode>())
            {
                var subItem = new System.Windows.Controls.MenuItem
                {
                    Header = mode.ToString(),
                    Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuItemStyle"),
                    IsEnabled = mode != currentMode,
                    IsChecked = mode == currentMode
                };
                var capturedMode = mode;
                subItem.Click += (_, _) => _window.SwitchDisplayMode(capturedMode);
                modeItem.Items.Add(subItem);
            }

            var separator = new System.Windows.Controls.Separator
            {
                Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuSeparatorStyle")
            };

            var exitItem = new System.Windows.Controls.MenuItem
            {
                Header = "Exit",
                Style = (Style)System.Windows.Application.Current.FindResource("CustomMenuItemStyle")
            };
            exitItem.Click += (_, _) => _window.ExitApp();

            menu.Items.Add(showItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(modeItem);
            menu.Items.Add(separator);
            menu.Items.Add(exitItem);

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.PlacementTarget = System.Windows.Application.Current.MainWindow;
            menu.IsOpen = true;
        });
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
