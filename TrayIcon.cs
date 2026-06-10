using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Lyrictified.Settings;

using NotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsScreen = System.Windows.Forms.Screen;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

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
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly Dispatcher _dispatcher;
    private readonly ITrayIconHost _window;
    private System.Windows.Forms.NotifyIcon? _icon;
    private bool _disposed;

    private ContextMenu? _openMenu;
    private Window? _helperWindow;
    private Point _lastClickScreenPoint;

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

        if (GetCursorPos(out var cursorPoint))
        {
            _lastClickScreenPoint = new Point(cursorPoint.X, cursorPoint.Y);
        }
        else
        {
            _lastClickScreenPoint = new Point(e.X, e.Y);
        }

        _dispatcher.Invoke(() =>
        {
            if (_openMenu is { IsOpen: true })
            {
                _openMenu.IsOpen = false;
                return;
            }

            var clickPixelX = (int)_lastClickScreenPoint.X;
            var clickPixelY = (int)_lastClickScreenPoint.Y;

            var hMonitor = MonitorFromPoint(new POINT { X = clickPixelX, Y = clickPixelY }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint monitorDpiX, out uint monitorDpiY) != 0)
            {
                monitorDpiX = 96;
                monitorDpiY = 96;
            }

            var dpiScaleX = monitorDpiX / 96.0;
            var dpiScaleY = monitorDpiY / 96.0;

            var screen = WinFormsScreen.FromPoint(new System.Drawing.Point(clickPixelX, clickPixelY))
                       ?? WinFormsScreen.PrimaryScreen
                       ?? WinFormsScreen.AllScreens.FirstOrDefault();

            var workArea = screen?.WorkingArea ?? new System.Drawing.Rectangle(
                (int)SystemParameters.VirtualScreenLeft,
                (int)SystemParameters.VirtualScreenTop,
                (int)SystemParameters.VirtualScreenWidth,
                (int)SystemParameters.VirtualScreenHeight);

            var helperWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Width = 1,
                Height = 1,
                Left = 0,
                Top = 0,
            };

            helperWindow.Show();

            var helperHandle = new WindowInteropHelper(helperWindow).Handle;
            if (helperHandle != IntPtr.Zero)
            {
                SetForegroundWindow(helperHandle);
            }

            var menu = new ContextMenu
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

            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var menuSize = menu.DesiredSize;

            const int gap = 4;
            var menuWidth = (int)System.Math.Ceiling(menuSize.Width * dpiScaleX);
            var menuHeight = (int)System.Math.Ceiling(menuSize.Height * dpiScaleY);

            var x = clickPixelX - menuWidth - gap;
            var y = clickPixelY - menuHeight - gap;

            if (x + menuWidth > workArea.Right)
            {
                x = workArea.Right - menuWidth;
            }
            if (x < workArea.Left)
            {
                x = workArea.Left;
            }
            if (y + menuHeight > workArea.Bottom)
            {
                y = workArea.Bottom - menuHeight;
            }
            if (y < workArea.Top)
            {
                y = workArea.Top;
            }

            if (helperHandle != IntPtr.Zero)
            {
                SetWindowPos(helperHandle, IntPtr.Zero, x, y, 1, 1, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            menu.PlacementTarget = helperWindow;
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;

            menu.Closed += OnMenuClosed;

            _helperWindow = helperWindow;
            _openMenu = menu;
            menu.IsOpen = true;
        });
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnMenuClosed;
        }

        if (_openMenu == sender)
        {
            _openMenu = null;
        }

        if (_helperWindow is not null)
        {
            _helperWindow.Close();
            _helperWindow = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_openMenu is { IsOpen: true })
        {
            _openMenu.IsOpen = false;
        }

        _icon?.Visible = false;
        _icon?.Dispose();
        _icon = null;

        if (_helperWindow is not null)
        {
            _helperWindow.Close();
            _helperWindow = null;
        }
    }
}
