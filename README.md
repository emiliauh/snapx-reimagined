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
export DisableGitVersionTask=true
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

## Install on macOS

Download the Apple Silicon DMG from [GitHub Releases](https://github.com/emiliauh/snapx-reimagined/releases), then drag SnapX into Applications. Requires macOS 14 or later. The published DMG includes FFmpeg. This prerelease is ad-hoc signed and is not notarized.

On first launch, grant the required Screen Recording access through the permission checklist. Reopen SnapX if macOS requests a restart. System-audio recording is optional: install and route output through a virtual loopback device such as BlackHole or Loopback, refresh the **System audio source** list in Screen recorder settings, and explicitly allow its audio-input access. SnapX does not select the physical microphone. Then choose **Enable launch at login** or **Not now**. macOS manages any required approval. You can disable startup in SnapX's Application settings or in **System Settings > General > Login Items & Extensions > Open at Login**.

See [packaging instructions](packaging/MACOS.md) and [validation results](MACOS-VALIDATION.md).

## Build on macOS

Install the .NET 10 SDK and Xcode. Run these commands from the repository root (the explicit fallback avoids a GitVersion mainline-history error):

```sh
export DisableGitVersionTask=true
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

Native AOT linking for macOS must run on macOS. GitHub CI builds an ad-hoc signed development bundle. Distributed updates should use a stable Developer ID Application certificate; ad-hoc rebuilds can invalidate saved macOS permissions. See [signing instructions](packaging/MACOS.md).

Run the local output:

```sh
./Output/snapx-ui/snapx-ui
```

To launch as a macOS application:

```sh
SNAPX_ALLOW_ADHOC=1 bash packaging/macos-bundle.sh Output/snapx-ui "SnapX.app"
open "SnapX.app"
```

Local and CI bundles use the visible `SnapX` name with the distinct
`com.emiliauh.snapx.local` identity by
default, so they do not collide with an installed production copy. A stable
local code-signing certificate can preserve one machine's permission identity
across rebuilds without an Apple Developer account; see the packaging guide.

Install FFmpeg for screen recording and allow capture in macOS Privacy &
Security settings. See the [build guide](.github/BUILDING.md) for native tests
and recording setup.

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
| macOS | Builds locally and in GitHub CI; app bundles can be ad-hoc signed. | Still capture, the combined picker, single-instance forwarding, and Carbon global hotkeys are implemented. FFmpeg AVFoundation records regions within one display, including Retina and rotated displays. Local native probes verify capture, hotkey registration, and recording; see the [macOS build and test instructions](.github/BUILDING.md). |

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
