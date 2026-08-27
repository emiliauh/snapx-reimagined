# SnapX

## What is SnapX

SnapX is a desktop application for screen capture, screen recording, and file upload. It provides capture tools, upload tasks, history, and settings in one user interface. It runs on Linux, Windows, and macOS, but each platform has different support limits. The table below lists the verified support and the known gaps.

## Feature overview

- Capture a screen, window, or region.
- Record a screen or region on supported platforms.
- Edit images after capture.
- Upload files, text, and images with built-in or custom upload tasks.
- Copy results to the clipboard or save them to a file.
- Store task history and image metadata in SQLite.
- Store settings in YAML files.
- Run capture and upload tasks from hotkeys or command-line arguments on supported platforms.
- Open settings in the main window or from the tray menu.
- Keep telemetry off unless the user enables it.

## Platform support

| Platform | Status | Capture and recording | Application integration |
| --- | --- | --- | --- |
| Linux/Wayland (Hyprland) | Verified on Hyprland for these listed behaviors. | The native two-in-one picker selects a window with a click or a region with a drag; both are runtime verified on Hyprland. Native overlays show the recording outline and recording controls. `wf-recorder` records video and selects the output with the largest overlap. | Single-instance forwarding and the tray are runtime verified. Other Wayland compositors are not yet verified. |
| Linux/X11 | In progress verification | The managed window-or-region picker is source-present in this change set. Runtime verification is in progress. FFmpeg uses `x11grab` for recording. | Single-instance forwarding is present. |
| Windows | Partial; runtime verification is incomplete | Core capture and upload features are present. Recording uses DDA or FFmpeg `gdigrab`. The combined picker and recording outline are source-present but runtime-unverified. | Single-instance forwarding is present. Tray code is source-present but runtime-unverified. |
| macOS | Partial | SnapXRust provides still capture. Video recording is not supported. A clear unsupported error is planned. | Single-instance forwarding is not ported. Supported builds use CI Native AOT on macOS. Runtime verification is incomplete. |

## Build and run

Install the .NET 10 SDK. Run all commands from the repository root.

### Linux

Install `gcc`, the Wayland client development files, FFmpeg, and `wf-recorder` for a Wayland session.

```sh
dotnet build SnapX.slnx --no-incremental -m:1
```

Run the debug build:

```sh
./SnapX.Avalonia/bin/Debug/net10.0/linux-x64/snapx-ui
```

Create the release output:

```sh
dotnet run --project build --no-restore -- build --no-color
```

Run the release output:

```sh
./Output/snapx-ui/snapx-ui
```

### Windows

Use PowerShell or a terminal with the .NET 10 SDK.

```powershell
dotnet build SnapX.slnx --no-incremental -m:1
```

Run the debug build:

```powershell
.\SnapX.Avalonia\bin\Debug\net10.0-windows10.0.26100.0\win-x64\snapx-ui.exe
```

Create the release output:

```powershell
dotnet run --project build --no-restore -- build --no-color
```

Run the release output:

```powershell
.\Output\snapx-ui\snapx-ui.exe
```

### macOS

macOS builds are available only through CI Native AOT. The CI runner supplies Xcode and the Apple SDK. A Linux host cannot link the macOS Native AOT output.

The CI build step is:

```sh
dotnet build SnapX.slnx --no-incremental -m:1
```

The CI package step is:

```sh
dotnet run --project build --no-restore -- build --no-color
```

After you extract the CI artifact, run:

```sh
./snapx-ui
```

## Configuration and environment variables

- `SNAPX_TELEMETRY=1` enables telemetry. Telemetry is off when this value is not `1`.
- `SNAPX_USE_VULKAN=1` adds Vulkan as the first X11 rendering option on Linux.
- `SNAPX_WAYLAND_GPU=1` enables EGL rendering on the native Wayland user interface. The default uses software rendering.
- `SNAPX_REGISTER_PORTAL_HOST=0` disables host application ID registration for the Linux global-shortcut portal.
- `SNAPX_SANDBOX` enables sandbox mode when the variable exists. This mode uses an in-memory history database and disables system integration registration.

## License

SnapX uses the GPL-3 license. See [LICENSE.md](./LICENSE.md).

Forked from ShareX (sharex/ShareX) and the SnapX cross-platform effort.
