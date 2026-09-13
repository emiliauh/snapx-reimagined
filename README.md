<p align="center">
  <img src="SnapX.Core/Resources/SnapX_Logo.png" width="120" alt="SnapX logo">
</p>

# SnapX

SnapX captures screenshots and records your screen on Windows, macOS, and Linux.

[![Build status](https://github.com/emiliauh/snapx-reimagined/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/emiliauh/snapx-reimagined/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/emiliauh/snapx-reimagined?include_prereleases&label=release)](https://github.com/emiliauh/snapx-reimagined/releases)
[![License](https://img.shields.io/github/license/emiliauh/snapx-reimagined)](LICENSE.md)

[Download](#download) | [Features](#features) | [Platform support](#platform-support) | [Build from source](#build-from-source) | [Report a problem](#support)

> [!IMPORTANT]
> SnapX is in prerelease development. Test it before you use it for important work. Some features still need tests on real computers.

## Download

The current release is [SnapX v0.11](https://github.com/emiliauh/snapx-reimagined/releases/tag/v0.11.0).

| Platform | Available package | Requirement |
| --- | --- | --- |
| macOS | [Download the Apple Silicon DMG](https://github.com/emiliauh/snapx-reimagined/releases/download/v0.11.0/SnapX-0.11.0-macOS-arm64.dmg) | macOS 14 or later on Apple Silicon |
| Windows | Build from source | A supported Windows release |
| Linux | Build from source | A supported X11 or Wayland desktop |

The macOS download includes FFmpeg. It uses a development signature. Apple has not notarized it, so macOS can show a warning when you first open it.

### Install on macOS

1. Open the DMG.
2. Drag SnapX to the Applications shortcut.
3. Open SnapX from Applications.
4. Follow the permission checklist.
5. Allow Screen and System Audio Recording in System Settings.
6. Quit and reopen SnapX if macOS requests it.

SnapX does not request microphone access. To record sound from other applications, select **System audio (macOS)** in Screen recorder settings.

SnapX can start when you sign in. It enables this option only when you select **Enable launch at login**.

## Features

- Capture a display, window, or selected region.
- Add text, arrows, shapes, and drawings to screenshots.
- Select colors with an eyedropper.
- Move and resize annotations.
- Undo and redo edits.
- Record a display or selected region.
- Pause, stop, or cancel a recording.
- Show an outline around the recorded region.
- Start actions from the application, tray menu, keyboard shortcuts, or command line.
- Send new commands to the running SnapX process.
- Upload images, text, and files to supported services.

On supported desktop systems, the region picker lets you select a window or drag a region. On native Wayland, SnapX opens the annotation editor after you select the region.

## First use

### Capture a screenshot

1. Open SnapX.
2. Select a capture action.
3. Select a window or drag a region.
4. Add annotations if you need them.
5. Confirm the capture.

### Record the screen

1. Open the screen recorder settings.
2. Select the video and audio options.
3. Start a recording action.
4. Select a display or region.
5. Use the on-screen controls to pause, stop, or cancel.

On macOS, keep each recording region inside one display.

## Keyboard shortcuts

You can change all shortcuts in SnapX settings.

| Action | Windows and Linux default | macOS default |
| --- | --- | --- |
| Capture a region | `Control + Print Screen` | `Command + Control + Shift + 1` |
| Capture the screen | `Print Screen` | `Command + Control + Shift + 2` |
| Capture the active window | `Alt + Print Screen` | `Command + Control + Shift + 3` |
| Record video | `Shift + Print Screen` | `Command + Control + Shift + 4` |
| Record GIF | `Control + Shift + Print Screen` | `Command + Control + Shift + 5` |

## Command line

Start a region capture:

```sh
snapx-ui -RectangleRegion
```

Start a region recording:

```sh
snapx-ui -ScreenRecorder
```

## Platform support

| Platform | Current support | Known limits |
| --- | --- | --- |
| macOS | Capture, annotations, recording, system audio, global shortcuts, and launch at login | The public package supports Apple Silicon. Recording regions cannot cross displays. Apple has not notarized the package. |
| Windows | Capture, annotations, recording, overlays, and global shortcuts | More tests on Windows computers are required. A public installer is not available for v0.11. |
| Linux X11 | Capture, annotations, recording, and global shortcuts | Recording requires FFmpeg. Capture and recording need more tests on X11 desktops. |
| Linux Wayland | Region capture, recording, global shortcuts, and recording controls | Recording requires `wf-recorder`. Hyprland has the most tests. Other Wayland desktops need more tests. |

See the [build and test guide](.github/BUILDING.md) for detailed requirements and test status.

## Build from source

Install Git and the .NET 10 SDK. You also need the tools for your operating system:

- Linux: Clang, zlib development headers, FFmpeg for X11, and `wf-recorder` for Wayland.
- Windows: Visual Studio C++ build tools and Windows SDK `10.0.26100.0`.
- macOS: Xcode command line tools. Install FFmpeg if you do not use the packaged DMG.

Read the [complete build guide](.github/BUILDING.md) before you build a package.

Clone the repository:

```sh
git clone https://github.com/emiliauh/snapx-reimagined.git
cd snapx-reimagined
```

On Linux or macOS, run:

```sh
export DisableGitVersionTask=true
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

On Windows, run in PowerShell:

```powershell
$env:DisableGitVersionTask = "true"
dotnet build SnapX.slnx --no-incremental -m:1
dotnet run --project build --no-restore -- build --no-color
```

The build creates the application in `Output/snapx-ui`. Keep the complete folder because SnapX uses the native libraries in it.

Run SnapX on Linux or macOS:

```sh
./Output/snapx-ui/snapx-ui
```

Run SnapX on Windows:

```powershell
.\Output\snapx-ui\snapx-ui.exe
```

To create a macOS application bundle, read the [macOS packaging guide](packaging/MACOS.md).

## Privacy

Telemetry is off by default. SnapX sends telemetry only when you enable it. SnapX does not send captured images, videos, text, or files as telemetry.

Read the [privacy policy](packaging/PRIVACY.md) for the data list, settings, and service information.

## Security

Do not report a vulnerability in a public issue. Use the private process in the [security policy](.github/SECURITY.md).

## Support

Search the [existing issues](https://github.com/emiliauh/snapx-reimagined/issues) before you create a report.

For a bug report, include:

- The SnapX version.
- The operating system and version.
- The desktop environment on Linux.
- Clear steps that reproduce the problem.
- Relevant logs or screenshots. Remove private information first.

Use the [bug report form](https://github.com/emiliauh/snapx-reimagined/issues/new?template=bug_report.yml) or the [feature request form](https://github.com/emiliauh/snapx-reimagined/issues/new?template=feature_request.yml).

## Contribute

Read the [contribution guide](.github/CONTRIBUTING.md) and [code of conduct](.github/CODE_OF_CONDUCT.md) before you submit a change.

## License and credits

SnapX uses the [GPL-3.0-or-later license](LICENSE.md).

SnapX is based on [ShareX](https://github.com/ShareX/ShareX) and [SnapX](https://github.com/SnapXL/SnapX).
