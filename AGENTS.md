# AGENTS.md

## Project Overview

**Lyrictified** is a Windows-only desktop app that shows real-time synced lyrics for whatever music is playing on the system. It reads the current track via the Windows SMTC API (`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`), fetches time-synced lyrics from a chain of sources, and renders them in one of four display modes. It is a single-project WPF application with no test suite.

Note: this codebase is intentionally almost entirely AI-generated (see the README disclaimer). Expect inconsistent verbosity and occasional redundancy; keep changes minimal and follow the surrounding style.

## Tech Stack

- **Language:** C# with `Nullable` and `ImplicitUsings` enabled
- **Framework:** `net10.0-windows10.0.19041.0` (Windows 10 2004+ only — uses WinRT SMTC APIs, Win32 P/Invoke, DWM attributes, and the registry)
- **UI:** WPF (`UseWPF=true`) + Windows Forms (`UseWindowsForms=true`, used only for the tray `NotifyIcon`)
- **Solution:** `Lyrictified.slnx` (XML solution format) containing only `Lyrictified.csproj`
- **Installer:** Inno Setup 6 script at `Installer/Lyrictified.iss`
- **No NuGet packages** beyond the implicit SDK references; `NuGet.Config` pins nuget.org as the only source

## Build, Run, Test

Prerequisite: **.NET 10 SDK** (10.0.2xx) installed and on `PATH`. The `.dotnet-home/` folder in the repo is only a telemetry/sentinel directory, not an SDK — ignore it.

```powershell
dotnet build          # build (also builds the Inno Setup installer, see below)
dotnet run            # run the app (Debug configuration)
dotnet build -c Release
```

- **There are no tests.** Verification is manual: build must succeed with no new warnings, then run the app and check behavior with music playing. Once compiled successfully, ask the user to verify changes manually.
- **Installer side effect:** the `BuildInnoSetupInstaller` MSBuild target runs after *every* non-design-time build and invokes Inno Setup (`ISCC.exe`) if found, writing installers to `Build-outputs/`. Pass `/p:SkipInstallerBuild=true` to skip this, e.g. `dotnet build /p:SkipInstallerBuild=true`. A missing Inno Setup only produces a warning, not an error.
- **There is no CI.** `.github/workflows/` is empty.

## Repository Layout

| Path | Purpose |
| --- | --- |
| `App.xaml(.cs)` | Entry point: single-instance mutex, AppUserModelID, VM warning, autostart, `RestartDisplayWindow()` which creates the window for the configured `DisplayMode` |
| `AppBarWindow`, `TaskbarWindow`, `IslandWindow`, `WindowedWindow` (`.xaml/.cs`) | The four display modes (full-width top app bar, floating taskbar overlay, click-through island overlay, normal window). Each implements `ITrayIconHost` and owns its own `MainViewModel`, `TrayIcon`, and settings window instance |
| `SettingsWindow.xaml(.cs)` | Settings UI |
| `TrayIcon.cs` | WinForms `NotifyIcon` + WPF context menu (Show / Settings / Mode / Exit) |
| `VmWarningDialog`, `DebugStartupDialog`, `DebugServerDialog`, `DebugCacheDialog` | VM warning and DEBUG-only diagnostics dialogs |
| `ViewModels/MainViewModel.cs` | Central MVVM state machine: song changes, lyrics loading, adaptive `DispatcherTimer` refresh, playback-position estimation (SMTC timeline anchor + `Stopwatch`), word-by-word karaoke (real TTML words or estimated) |
| `Services/MediaSessionWatcher.cs` | SMTC wrapper: session selection, per-app ignore list, detected-app reporting, play/pause toggle |
| `Services/CompositeLyricsService.cs` | Lyrics source chain, in order: JSON file cache → Lyrictified Server API → lrclib.net → `syncedlyrics` Python CLI. `ForcedSource` (debug setting) restricts to one source |
| `Services/LocalLyricsService.cs` | Client for the Lyrictified Server API (base address from `App.LocalLyricsBaseAddress`, default `https://api.lyrictified.xyz/`) |
| `Services/LrcLibLyricsService.cs` | lrclib.net client + LRC parser (`ParseSyncedLyrics`) |
| `Services/SyncedLyricsCliService.cs` | Fallback that shells out to the `syncedlyrics` Python CLI; override the command with the `SYNCEDLYRICS_COMMAND` environment variable |
| `Services/TtmlLyricsParser.cs` | TTML parser (word-level timings, background vocals) and `CleanToLrc` conversion |
| `Services/LyricsCacheService.cs` | Per-song JSON cache in `%LOCALAPPDATA%\Lyrictified\cache` |
| `Services/Logger.cs` | Appends to `%LOCALAPPDATA%\Lyrictified\debug.log` (truncated on each app start); never throws |
| `Services/WindowsAutostartService.cs` | HKCU `...\CurrentVersion\Run` registry entry |
| `Services/VmDetectionService.cs` | VM detection + toast warning |
| `Services/DebugBuildHelper.cs`, `Services/LocalServerDetector.cs` | DEBUG-only: startup dialog and auto-detection of a locally running `Lyrictified.Server` process |
| `Settings/` | `AppSettings` model + `AppSettingsService` (JSON at `%LOCALAPPDATA%\Lyrictified\settings.json`) and enums: `DisplayMode`, `HideMode`, `LyricAlignment`, `IslandAnimationMode`, `AppBarAdaptMode`, `DetectedMediaApp`, `MonitorOption` |
| `DisplayModes/` | Layout constants for AppBar and Taskbar modes (heights, font sizes, animation offsets) |
| `Interop/` | Win32 P/Invoke: `AppBarManager` (`SHAppBarMessage` registration, multi-monitor), `WindowMaximizeBounds`, `WorkspaceVisibilityManager`, `NativeVirtualDesktopPinning` |
| `Styling/WindowAppearanceManager.cs` | DWM dark mode + Mica backdrop, accent-color palette |
| `Assets/` | Icons and images (copied to output via `<Content>` items in the csproj) |
| `Installer/Lyrictified.iss` | Inno Setup script; parameterized through `LYRICTIFIED_*` environment variables set by the csproj target |

## Architecture Notes

- **Startup flow:** `App.OnStartup` acquires the `Lyrictified_SingleInstance` mutex (a second instance posts `Lyrictified_ShowSettings` to the first and exits), sets the AppUserModelID, shows the VM warning, shows the DEBUG server-picker dialog, applies autostart, then calls `RestartDisplayWindow()` to create the window matching `settings.DisplayMode`. Switching modes re-runs `RestartDisplayWindow()` (new window, old one closed).
- **Data flow:** `MediaSessionWatcher` (SMTC events) → `MainViewModel.HandleSongAsync` → `CompositeLyricsService.GetTimedLyricsAsync` (cache → server API → lrclib → CLI) → lyrics stored on the view model → a `DispatcherTimer` with an adaptive interval (15–750 ms, computed from the distance to the next lyric timestamp) updates current/next line, word highlight, and progress. Playback position = last SMTC timeline anchor + elapsed `Stopwatch` time, re-anchored on play/pause/session changes.
- **TTML vs LRC:** TTML results carry per-word timings (`LyricLine.Words`) and background lines; `CompositeLyricsService.WrapResult` also produces `CleanedLrcLines` (line-level only) used by the Taskbar mode. Word-by-word highlighting falls back to evenly estimated word timings when only LRC is available.
- **Settings:** every display window loads `AppSettings` through `AppSettingsService`; saving also applies the autostart registry key. `AppSettings.LegacyAppBarAdaptToContent` is a JSON migration shim — keep such shims when renaming persisted settings.
- **Debug-only code:** the three `Debug*Dialog` files are excluded from non-Debug builds by an `<ItemGroup Condition="'$(Configuration)' != 'Debug'">` in the csproj. `DebugBuildHelper` and `LocalServerDetector` are wrapped in `#if DEBUG`. Do not reference them from release-safe code.

## Coding Conventions

- File-scoped namespaces; one public type per file named after the file.
- Models are `sealed record`s (`SongInfo`, `LyricLine`, `WordInfo`); services and windows are `sealed class`es; view models implement `INotifyPropertyChanged` by hand (no MVVM toolkit).
- Async all the way for I/O; `async void` only for event handlers, always wrapped in try/catch.
- Error handling is deliberately forgiving: catch broadly, log via `Logger.Log` or `Debug.WriteLine`, and fall back to a safe UI state (`ApplySongFallbackState` pattern). Empty `catch { }` blocks are common and intentional for best-effort P/Invoke/SMTC calls.
- All Win32/WinRT interop lives in `Interop/` or as private `[DllImport]` declarations inside the consuming class; use `OperatingSystem.IsWindowsVersionAtLeast` guards for newer DWM features.
- WPF/WinForms type collisions are resolved with `using` aliases at the top of the file (e.g. `using WpfBrush = System.Windows.Media.Brush;`, `using Application = System.Windows.Application;`). Follow that pattern rather than fully qualifying inline.
- Layout numbers for the AppBar/Taskbar modes go into the constant classes in `DisplayModes/`, not inline in XAML/code-behind.
- XAML code-behind is heavy by design in this project (animations, window chrome, P/Invoke). Keep window-specific logic in the window; keep song/lyrics state in `MainViewModel`; keep lyrics fetching/parsing in `Services/`.

## Gotchas

- The app writes to `%LOCALAPPDATA%\Lyrictified\` (`settings.json`, `cache\`, `debug.log`). Delete that folder to reset state when testing, ONLY WHEN REQUESTED TO RESET.
- `dotnet build` triggers the Inno Setup installer build when Inno Setup 6 is installed — surprising if you only wanted a compile check. Use `/p:SkipInstallerBuild=true`.
- `App.LocalLyricsBaseAddress` and `App.IgnoreLocalCache` are mutable statics set during startup (and by the debug dialogs); `LocalLyricsService` captures the base address in its constructor, so it must be set before the first lyrics request.
- SMTC, tray icon, AppBar registration, and window positioning all require a real Windows desktop session — none of this can be exercised in a headless/CI environment.
- `AssemblyInfo.cs` and `app.manifest` hold version/identity info; the app version lives in `Lyrictified.csproj` (`<Version>`) and the installer defaults in `Lyrictified.iss` — bump both together.
