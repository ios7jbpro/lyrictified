using System.Runtime.InteropServices;

namespace Lyrictified.Interop;

internal static class NativeVirtualDesktopPinning
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ClsidVirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

    private static readonly Lazy<Services> ShellServices = new(CreateServices);

    public static PinResult PinWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return PinResult.Failed("HWND is zero.");
        }

        var services = ShellServices.Value;
        var viewResult = TryGetApplicationView(services, hwnd, out var view);
        if (!viewResult.Succeeded || view is null)
        {
            return viewResult;
        }

        var pinnedWindow = false;
        try
        {
            if (!services.PinnedApps.IsViewPinned(view))
            {
                services.PinnedApps.PinView(view);
            }

            pinnedWindow = services.PinnedApps.IsViewPinned(view);
        }
        catch (Exception ex)
        {
            return PinResult.Failed($"PinView failed: {ex.Message}");
        }

        return new PinResult(
            pinnedWindow,
            pinnedWindow,
            false,
            null,
            null);
    }

    private static PinResult TryGetApplicationView(Services services, IntPtr hwnd, out IApplicationView? view)
    {
        view = null;

        try
        {
            var hr = services.ApplicationViews.GetViewForHwnd(hwnd, out view);
            return hr == 0 && view is not null
                ? PinResult.Success(viewPinned: false, appPinned: false, appUserModelId: null)
                : PinResult.Failed($"GetViewForHwnd failed: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            return PinResult.Failed($"GetViewForHwnd threw: {ex.Message}");
        }
    }

    private static Services CreateServices()
    {
        var shellType = Type.GetTypeFromCLSID(ClsidImmersiveShell, throwOnError: true)!;
        var shell = (IServiceProvider)Activator.CreateInstance(shellType)!;

        var applicationViewCollectionGuid = typeof(IApplicationViewCollection).GUID;
        var pinnedAppsGuid = typeof(IVirtualDesktopPinnedApps).GUID;

        var applicationViews = (IApplicationViewCollection)shell.QueryService(
            applicationViewCollectionGuid,
            applicationViewCollectionGuid);

        var pinnedApps = (IVirtualDesktopPinnedApps)shell.QueryService(
            ClsidVirtualDesktopPinnedApps,
            pinnedAppsGuid);

        return new Services(applicationViews, pinnedApps);
    }

    public static PinResult PinApplicationId(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return PinResult.Failed("AppUserModelID is empty.");
        }

        try
        {
            var pinnedApps = ShellServices.Value.PinnedApps;
            if (!pinnedApps.IsAppIdPinned(appUserModelId))
            {
                pinnedApps.PinAppID(appUserModelId);
            }

            var isPinned = pinnedApps.IsAppIdPinned(appUserModelId);
            return new PinResult(isPinned, false, isPinned, appUserModelId, isPinned ? null : "PinAppID returned without pinning.");
        }
        catch (Exception ex)
        {
            return PinResult.Failed($"PinAppID failed: {ex.Message}");
        }
    }

    public static PinResult UnpinApplicationId(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return PinResult.Failed("AppUserModelID is empty.");
        }

        try
        {
            var pinnedApps = ShellServices.Value.PinnedApps;
            if (pinnedApps.IsAppIdPinned(appUserModelId))
            {
                pinnedApps.UnpinAppID(appUserModelId);
            }

            var isPinned = pinnedApps.IsAppIdPinned(appUserModelId);
            return new PinResult(!isPinned, false, isPinned, appUserModelId, isPinned ? "UnpinAppID returned without unpinning." : null);
        }
        catch (Exception ex)
        {
            return PinResult.Failed($"UnpinAppID failed: {ex.Message}");
        }
    }

    internal sealed record PinResult(
        bool Succeeded,
        bool WindowPinned,
        bool ApplicationPinned,
        string? AppUserModelId,
        string? Error)
    {
        public static PinResult Success(bool viewPinned, bool appPinned, string? appUserModelId)
        {
            return new PinResult(true, viewPinned, appPinned, appUserModelId, null);
        }

        public static PinResult Failed(string error)
        {
            return new PinResult(false, false, false, null, error);
        }
    }

    private sealed record Services(
        IApplicationViewCollection ApplicationViews,
        IVirtualDesktopPinnedApps PinnedApps);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    private interface IApplicationViewCollection
    {
        int GetViews(out IObjectArray array);
        int GetViewsByZOrder(out IObjectArray array);
        int GetViewsByAppUserModelId(string id, out IObjectArray array);
        int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
        int GetViewForApplication(object application, out IApplicationView view);
        int GetViewForAppUserModelId(string id, out IApplicationView view);
        int GetViewInFocus(out IntPtr view);
        int Unknown1(out IntPtr view);
        void RefreshCollection();
        int RegisterForApplicationViewChanges(object listener, out int cookie);
        int UnregisterForApplicationViewChanges(int cookie);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    private interface IVirtualDesktopPinnedApps
    {
        bool IsAppIdPinned(string appId);
        void PinAppID(string appId);
        void UnpinAppID(string appId);
        bool IsViewPinned(IApplicationView applicationView);
        void PinView(IApplicationView applicationView);
        void UnpinView(IApplicationView applicationView);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    private interface IApplicationView
    {
        int SetFocus();
        int SwitchTo();
        int TryInvokeBack(IntPtr callback);
        int GetThumbnailWindow(out IntPtr hwnd);
        int GetMonitor(out IntPtr immersiveMonitor);
        int GetVisibility(out int visibility);
        int SetCloak(ApplicationViewCloakType cloakType, int unknown);
        int GetPosition(ref Guid guid, out IntPtr position);
        int SetPosition(ref IntPtr position);
        int InsertAfterWindow(IntPtr hwnd);
        int GetExtendedFramePosition(out RectNative rect);
        int GetAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int SetAppUserModelId(string id);
        int IsEqualByAppUserModelId(string id, out int result);
        int GetViewState(out uint state);
        int SetViewState(uint state);
        int GetNeediness(out int neediness);
        int GetLastActivationTimestamp(out ulong timestamp);
        int SetLastActivationTimestamp(ulong timestamp);
        int GetVirtualDesktopId(out Guid guid);
        int SetVirtualDesktopId(ref Guid guid);
        int GetShowInSwitchers(out int flag);
        int SetShowInSwitchers(int flag);
        int GetScaleFactor(out int factor);
        int CanReceiveInput(out bool canReceiveInput);
        int GetCompatibilityPolicyType(out ApplicationViewCompatibilityPolicy flags);
        int SetCompatibilityPolicyType(ApplicationViewCompatibilityPolicy flags);
        int GetSizeConstraints(IntPtr monitor, out SizeNative size1, out SizeNative size2);
        int GetSizeConstraintsForDpi(uint dpi, out SizeNative size1, out SizeNative size2);
        int SetSizeConstraintsForDpi(ref uint dpi, ref SizeNative size1, ref SizeNative size2);
        int OnMinSizePreferencesUpdated(IntPtr hwnd);
        int ApplyOperation(IntPtr operation);
        int IsTray(out bool isTray);
        int IsInHighZOrderBand(out bool isInHighZOrderBand);
        int IsSplashScreenPresented(out bool isSplashScreenPresented);
        int Flash();
        int GetRootSwitchableOwner(out IApplicationView rootSwitchableOwner);
        int EnumerateOwnershipTree(out IObjectArray ownershipTree);
        int GetEnterpriseId([MarshalAs(UnmanagedType.LPWStr)] out string enterpriseId);
        int IsMirrored(out bool isMirrored);
        int Unknown2(out int unknown);
        int Unknown3(out int unknown);
        int Unknown4(out int unknown);
        int Unknown5(out int unknown);
        int Unknown6(out int unknown);
        int Unknown7(int unknown);
        int Unknown8();
        int Unknown9(out int unknown);
        int Unknown10(int unknown);
        int Unknown11(int unknownX, int unknownY);
        int Unknown12(int unknown);
        int Unknown13(out SizeNative size);
    }

    private enum ApplicationViewCloakType
    {
        None = 0,
        Default = 1,
        VirtualDesktop = 2
    }

    private enum ApplicationViewCompatibilityPolicy
    {
        None = 0,
        SmallScreen = 1,
        TabletSmallScreen = 2,
        VerySmallScreen = 3,
        HighScaleFactor = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
