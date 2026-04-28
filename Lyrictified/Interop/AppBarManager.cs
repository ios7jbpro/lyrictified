using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lyrictified.Interop;

public sealed class AppBarManager : IDisposable
{
    private readonly Window _window;
    private readonly List<DisplayMonitor> _monitors = [];
    private int _height;
    private uint _callbackMessageId;
    private bool _registered;
    private bool _hookAdded;
    private HwndSource? _source;

    public AppBarManager(Window window, int height)
    {
        _window = window;
        _height = height;
        RefreshMonitors();
    }

    public int MonitorCount => _monitors.Count;

    public int CurrentMonitorIndex { get; private set; }

    public int PrimaryMonitorIndex => _monitors.FindIndex(m => m.IsPrimary) is var index && index >= 0
        ? index
        : 0;

    public string? CurrentMonitorDeviceName => _monitors.Count == 0 ? null : _monitors[Math.Clamp(CurrentMonitorIndex, 0, _monitors.Count - 1)].DeviceName;

    public bool IsAttached => _registered;

    public IReadOnlyList<DisplayMonitor> Monitors => _monitors;

    public void SetHeight(int height)
    {
        if (height <= 0 || height == _height)
        {
            return;
        }

        _height = height;
        if (_registered)
        {
            Reposition();
        }
    }

    public void Attach()
    {
        _source ??= PresentationSource.FromVisual(_window) as HwndSource;
        if (_source is null)
        {
            return;
        }

        if (_callbackMessageId == 0)
        {
            _callbackMessageId = RegisterWindowMessage("Lyrictified.AppBarMessage");
        }

        if (!_hookAdded)
        {
            _source.AddHook(WndProc);
            _hookAdded = true;
        }

        RegisterBar();
        Reposition();
    }

    public void Detach()
    {
        if (_source is null || !_registered)
        {
            return;
        }

        var data = CreateBaseData(_source.Handle);
        SHAppBarMessage(AppBarMessage.Remove, ref data);
        _registered = false;
    }

    public void RefreshMonitors()
    {
        var previousDeviceName = _monitors.Count > 0 && CurrentMonitorIndex < _monitors.Count
            ? _monitors[CurrentMonitorIndex].DeviceName
            : null;

        _monitors.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumProc, IntPtr.Zero);

        if (_monitors.Count == 0)
        {
            _monitors.Add(new DisplayMonitor(
                new RectNative { left = 0, top = 0, right = (int)SystemParameters.PrimaryScreenWidth, bottom = (int)SystemParameters.PrimaryScreenHeight },
                "PRIMARY",
                true));
        }

        if (!string.IsNullOrWhiteSpace(previousDeviceName))
        {
            var matchedIndex = _monitors.FindIndex(m => string.Equals(m.DeviceName, previousDeviceName, StringComparison.OrdinalIgnoreCase));
            CurrentMonitorIndex = matchedIndex >= 0 ? matchedIndex : PrimaryMonitorIndex;
        }
        else
        {
            CurrentMonitorIndex = Math.Clamp(CurrentMonitorIndex, 0, _monitors.Count - 1);
        }
    }

    public bool SetCurrentMonitor(string? deviceName)
    {
        RefreshMonitors();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            CurrentMonitorIndex = Math.Clamp(CurrentMonitorIndex, 0, _monitors.Count - 1);
            if (_registered)
            {
                ReRegisterBar();
                Reposition();
            }

            return false;
        }

        var index = _monitors.FindIndex(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        if (index == CurrentMonitorIndex)
        {
            if (_registered)
            {
                Reposition();
            }

            return true;
        }

        CurrentMonitorIndex = index;
        if (_registered)
        {
            ReRegisterBar();
            Reposition();
        }

        return true;
    }

    public bool SetCurrentMonitorToPrimary()
    {
        RefreshMonitors();
        if (_monitors.Count == 0)
        {
            return false;
        }

        var primaryIndex = PrimaryMonitorIndex;
        if (primaryIndex == CurrentMonitorIndex)
        {
            if (_registered)
            {
                Reposition();
            }

            return true;
        }

        CurrentMonitorIndex = primaryIndex;
        if (_registered)
        {
            ReRegisterBar();
            Reposition();
        }

        return true;
    }

    public bool MoveToNextMonitor()
    {
        RefreshMonitors();
        if (_monitors.Count <= 1)
        {
            CurrentMonitorIndex = 0;
            Reposition();
            return false;
        }

        CurrentMonitorIndex = (CurrentMonitorIndex + 1) % _monitors.Count;
        if (_registered)
        {
            ReRegisterBar();
            Reposition();
        }

        return true;
    }

    public void Reposition()
    {
        if (_source is null || !_registered)
        {
            return;
        }

        RefreshMonitors();
        var monitor = _monitors[Math.Clamp(CurrentMonitorIndex, 0, _monitors.Count - 1)];

        var data = CreateBaseData(_source.Handle);
        data.uEdge = AppBarEdge.Top;
        data.rc.left = monitor.Bounds.left;
        data.rc.top = monitor.Bounds.top;
        data.rc.right = monitor.Bounds.right;
        data.rc.bottom = monitor.Bounds.top + _height;

        SHAppBarMessage(AppBarMessage.QueryPos, ref data);
        data.rc.bottom = data.rc.top + _height;
        SHAppBarMessage(AppBarMessage.SetPos, ref data);

        _window.Left = data.rc.left;
        _window.Top = data.rc.top;
        _window.Width = data.rc.right - data.rc.left;
        _window.Height = data.rc.bottom - data.rc.top;
    }

    private void ReRegisterBar()
    {
        if (_source is null || !_registered)
        {
            return;
        }

        var data = CreateBaseData(_source.Handle);
        SHAppBarMessage(AppBarMessage.Remove, ref data);
        _registered = false;
        RegisterBar();
    }

    private void RegisterBar()
    {
        if (_source is null || _registered)
        {
            return;
        }

        var data = CreateBaseData(_source.Handle);
        data.uCallbackMessage = _callbackMessageId;
        SHAppBarMessage(AppBarMessage.New, ref data);
        _registered = true;
    }

    public void Dispose()
    {
        Detach();

        if (_source is not null && _hookAdded)
        {
            _source.RemoveHook(WndProc);
            _hookAdded = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessageId && wParam.ToInt32() == (int)AppBarNotification.PosChanged)
        {
            Reposition();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData)
    {
        var monitorInfo = new MonitorInfoEx();
        monitorInfo.cbSize = Marshal.SizeOf<MonitorInfoEx>();

        if (!GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            return true;
        }

        _monitors.Add(new DisplayMonitor(monitorInfo.rcMonitor, monitorInfo.szDevice, (monitorInfo.dwFlags & 0x1) != 0));
        return true;
    }

    private static AppBarData CreateBaseData(IntPtr hwnd)
    {
        return new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = hwnd
        };
    }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(AppBarMessage dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private enum AppBarMessage : uint
    {
        New = 0x00000000,
        Remove = 0x00000001,
        QueryPos = 0x00000002,
        SetPos = 0x00000003
    }

    private enum AppBarNotification
    {
        StateChange = 0,
        PosChanged = 1,
        FullScreenApp = 2,
        WindowArrange = 3
    }

    private enum AppBarEdge : uint
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public AppBarEdge uEdge;
        public RectNative rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectNative
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}

public sealed record DisplayMonitor(AppBarManager.RectNative Bounds, string DeviceName, bool IsPrimary);
