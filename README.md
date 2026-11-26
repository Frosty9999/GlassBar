# GlassBar

A Mac-style replacement for the Windows 11 taskbar: a floating glass dock at the bottom, a slim menu bar at
the top, the system tray, and a settings window where every look-and-layout option is adjustable live.

## Download

Get the latest `GlassBar-vX.Y.Z-win-x64.zip` from the
[Releases page](https://github.com/Frosty9999/GlassBar/releases), unzip it anywhere, and run `GlassBar.exe`.
It needs Windows 11 (most features also work on Windows 10) and the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64).
To start it with Windows, tick *Start GlassBar when I sign in* in its settings.

## Build and run

1. Install the .NET 8 SDK (once): `winget install Microsoft.DotNet.SDK.8`
2. Double-click `build.bat` (or run `dotnet publish -c Release -r win-x64 --self-contained false -o out`).
3. Run `out\GlassBar.exe`. The settings window opens on first launch.

For quick testing while editing code: `dotnet run` from this folder.

## Using it

- **Dock:** click an app to open it or bring it forward; click again to minimize; with several windows open,
  clicks cycle through them. Middle-click opens a new window. Right-click for its window list, Keep in Dock /
  Remove from Dock, and Close. Drop a program or shortcut on the dock to pin it. Drag icons left and right to reorder them;
  a running app dragged in among the pinned apps becomes pinned.
- **Window previews:** rest the mouse on a running app to see live thumbnails of its windows. Click one to
  switch to it; the x (or a middle-click) closes it.
- **Badges and progress:** the small status icons apps put on their taskbar button (unread counts and the like)
  show on the dock icon, and downloads/copies show a progress bar under it (red: error, yellow: paused).
- **Recent files:** right-click an app to see its recently opened files (the same list as its Windows jump
  list) and reopen one in that app.
- **Drop files on an app** to open them in it (a PDF on Chrome, a folder on File Explorer). Drop on an empty
  part of the dock to pin a program or folder instead.
- **Snap windows:** right-click a running app and pick a layout: halves, thirds, two-thirds, or fill the screen
  above the dock. With several windows open, the last picture puts them side by side.
- **Recycle Bin and divider:** the bin sits after a divider, with the folder stacks. Drop files on it to delete
  them; right-click to empty it. Win+1 to Win+9 open or switch to the dock's apps (Shift+Win+number: new window).
- **Several monitors:** a top bar on every monitor by default (only the one with the active window shows its
  title), and optionally a dock on every monitor (General: Dock tab).
- **Folder stacks:** right-click the dock and choose *Add Downloads stack* (or Documents, or any folder), or drop
  a folder on the dock. Click it to see its newest files; right-click it to remove it.
- **Windows near the dock:** a window you move or resize onto the dock is nudged up above it, so nothing
  ends up hidden behind the dock (turn off on the Dock tab).
- **Top bar:** Start button, title of the window in front, tray icons (the arrow reveals the overflow icons),
  quick settings (Win+A), CPU, memory, battery, network speed, Wi-Fi/Ethernet (click for Quick Settings),
  volume (scroll over it to change, click to mute) and the clock (opens a calendar; its Notifications button,
  or turning the calendar off, opens Windows' notification center). The volume popup also lists the battery
  levels of Bluetooth devices (headphones, controllers, mice).
- **App menu:** the top bar shows the app in front in bold; click it for New window, Hide, Hide others,
  Close window, Quit, and Show program in File Explorer.
- **Hot corners:** rest the mouse in a screen corner to show the desktop, open Task View, Start, notifications
  or Quick Settings, lock the PC or turn the display off (General tab; all off by default).
- **Settings:** a search box filters the options on every tab; the General tab can export and import all
  settings and dock apps.
- **Glass:** the dock and the top bar each have their own effect, tint and border settings (on their tabs), plus
  auto-hide. Theme presets on the Appearance tab set both at once.
- **Settings:** right-click the dock or the top bar and choose *GlassBar settings*. Settings are saved to
  `%AppData%\GlassBar\settings.json`; errors are logged next to it in `error.log`.
- **Quit:** right-click either bar and choose *Quit GlassBar*. The Windows taskbar comes back.

## If the Windows taskbar does not come back

GlassBar hides the stock taskbar by switching it to auto-hide. A small watchdog copy of GlassBar
(`GlassBar.exe --watchdog <pid>`, about 5 MB) puts it back if GlassBar crashes or is ended in Task Manager.
If both are ended at once ("End process tree"), the next GlassBar start repairs it. Manual fix:
Settings > Personalization > Taskbar > Taskbar behaviors > untick "Automatically hide the taskbar".

## Where things live

| File | What it does |
|---|---|
| `Settings.cs` | Every option, its default, the theme presets, and JSON save/load |
| `SettingsWindow.xaml(.cs)` | The options window; adding an option is one line in a `Build...` method |
| `BarWindow.cs` | Shared bar behavior: a glass window sized to the panel, an invisible strip that reserves screen space, auto-hide, full-screen handling |
| `DockWindow.xaml(.cs)` | The dock: icons, hover zoom, running dots, click and right-click behavior |
| `DockManager.cs` | Groups open windows by app, merges them with pinned apps, saves drag reordering; badge and progress per app |
| `AppPaths.cs` | One stable path per app (newest Discord/Squirrel version, Steam instead of its web helper) |
| `GlassPopup.cs` | Base for the small glass popups: positioning next to a bar, blur, Windows 11 corners |
| `PreviewWindow.cs` | Live window thumbnails above a dock icon (DWM thumbnails) |
| `StackWindow.cs` | Folder stack: a grid of a pinned folder's newest files |
| `CalendarPopup.cs` | The month calendar under the clock |
| `WindowGuard.cs` | Nudges windows out from behind the dock after they are moved or resized |
| `ShellIcons.cs` | Loads each program's real icon the way Explorer does |
| `TopBarWindow.xaml(.cs)` | The top bar, including system tray click forwarding |
| `SystemStats.cs` | Clock, CPU, memory, battery, network, volume readouts |
| `NetworkStatus.cs` | Wi-Fi name and signal (Native Wifi API), Ethernet, throughput |
| `AudioVolume.cs` | Master volume and mute of the default speakers (Core Audio) |
| `JumpLists.cs` | An app's recent files (Windows jump lists), found by app ID |
| `HotCorners.cs` | Actions when the mouse rests in a screen corner |
| `BluetoothBattery.cs` | Battery levels of Bluetooth devices, read from Windows' device properties |
| `GpuUsage.cs` | GPU usage the way Task Manager computes it (performance counters) |
| `WindowTiling.cs` | Snap layouts (halves, thirds, fill) on the window's own monitor |
| `DockHotkeys.cs` | Win+number shortcuts, through a low-level keyboard hook (Explorer keeps them registered) |
| `RecycleBin.cs` | Recycle Bin state, icons, emptying, recycling dropped files |
| `VolumePopup.cs` | Volume, per-app volume and output device popup |
| `CrashGuard.cs`, `Program.cs` | The watchdog that restores the Windows taskbar after a crash |
| `Native.cs` | The Win32 calls: blur, window shape, system stats |

The window tracking, system tray hosting and taskbar hiding come from
[ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache-2.0), the library behind RetroBar and Cairo.

## Known limits

- The Start menu, Quick Settings and notification center are Windows' own; the buttons open them.
- Smooth corners come from Windows 11 and have a fixed size (8 px, or 4 px for small roundness). Turning
  *Smooth corners* off allows any roundness, clipped with a window region whose edge is not anti-aliased.
- Several monitors are supported but were built on a single-monitor PC and not yet tried with two.

## License

GlassBar is free software under the [GNU General Public License v3.0](LICENSE): you may use, change and share it,
and versions you share must stay under the GPL. It uses [ManagedShell](https://github.com/cairoshell/ManagedShell),
which is Apache-2.0 licensed.
