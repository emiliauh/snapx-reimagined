# SnapX

SnapX is a screen capture and recording application by [emiliauh](https://github.com/emiliauh).

The project home is [snapx-reimagined](https://github.com/emiliauh/snapx-reimagined).

The application ID is `io.emiliauh.SnapXL.SnapX`. The executable name is `snapx-ui`.

## Features

- Capture a screen, a window, or a region.
- Click a window or drag a region in the same picker.
- Record a screen or a selected region on supported systems.
- Show an outline around the recorded region.
- Show pause, stop, and abort controls during a recording.
- Run the native Wayland outline and controls as separate helper processes.
- Forward later launches to the first running process.
- Register global hotkeys through the desktop portal on Linux Wayland.
- Register global hotkeys through the X11 backend on Linux X11.
- Build Native AOT, self-contained, single-file executables.

## Install on Linux

Install the .NET 10 SDK. Install GCC and the Wayland client development files.

Install FFmpeg for X11 recording. Install `wf-recorder` for Wayland recording.

Clone this repository. Run these commands from its root directory:

```sh
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

The build places the application in `Output/snapx-ui`.

Run SnapX:

```sh
./Output/snapx-ui/snapx-ui
```

## Build on Windows

Install the .NET 10 SDK. Run these commands in PowerShell from the repository root:

```powershell
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

Run SnapX:

```powershell
.\Output\snapx-ui\snapx-ui.exe
```

## Build on macOS

Install the .NET 10 SDK and Xcode. Run these commands from the repository root:

```sh
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

Native AOT linking for macOS must run on macOS. GitHub CI builds and signs an application bundle.

Run the local output:

```sh
./Output/snapx-ui/snapx-ui
```

## Use SnapX

Start a region capture from the user interface, tray menu, hotkey, or command line.

Use this command to start the region picker:

```sh
snapx-ui -RectangleRegion
```

Use this command to start a region recording:

```sh
snapx-ui -ScreenRecorder
```

You can set hotkeys in the application. The initial hotkeys use the Print Screen key and its modifiers.

On Hyprland, you can bind any key to a SnapX command. For example:

```text
Ctrl+W  snapx-ui -RectangleRegion
Ctrl+E  snapx-ui -ScreenRecorder
```

## Platform support

| Platform | Build status | Current support |
| --- | --- | --- |
| Linux Wayland with Hyprland | Built by GitHub CI. | The combined picker, recording outline, recording controls, and single-instance forwarding are runtime verified. `wf-recorder` records video. Other Wayland compositors need runtime tests. |
| Linux X11 | Built by GitHub CI. | The combined picker and X11 hotkey backend are present. FFmpeg uses `x11grab`. CI tests launch and single-instance forwarding with Xvfb. Interactive picker and recording tests are still required. |
| Windows | Built by GitHub CI. | Capture, the combined picker, recording, overlays, hotkeys, and single-instance forwarding are present. Runtime tests on Windows are still required. |
| macOS | Built by GitHub CI. | Still capture, the combined picker, and single-instance forwarding are present. CI tests launch and forwarding. Video recording is not supported without custom commands. Interactive capture tests are still required. |

The recording outline and controls use native Wayland helpers on supported Wayland sessions.

Other systems use Avalonia overlay windows. Their behavior can differ between window managers.

## Configuration

- Set `SNAPX_TELEMETRY=1` to enable telemetry. SnapX disables telemetry by default.
- Set `SNAPX_USE_VULKAN=1` to try Vulkan first on Linux X11.
- Set `SNAPX_WAYLAND_GPU=1` to enable EGL rendering on native Wayland.
- Set `SNAPX_REGISTER_PORTAL_HOST=0` to stop host ID registration with the global shortcut portal.
- Set `SNAPX_DESKTOP_APP_ID` to change the application ID used by the portal backend.

## Development

Run the fuzz and property checks after a successful restore:

```sh
dotnet run --project tests/SnapX.Core.Fuzz --configuration Release --no-restore
```

GitHub CI runs these checks on Linux x64. CI also runs X11 and macOS launch tests.

## License

SnapX uses the [GPL-3.0-or-later license](LICENSE.md).

SnapX has lineage from [ShareX](https://github.com/ShareX/ShareX) and [SnapX](https://github.com/SnapXL/SnapX).
