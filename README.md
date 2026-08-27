<p align="center">
  <a href="https://github.com/emiliauh/snapx-reimagined">
    <img src="./.github/Linux.png" alt="SnapX Reimagined banner" />
  </a>
</p>
<h1 align="center">SnapX Reimagined</h1>
<h3 align="center">Capture, record, and share your screen. Built on ShareX, rebuilt for Linux.</h3>

> [!NOTE]
> This is a fork of [SnapX](https://github.com/SnapXL/SnapX), which is a cross-platform port of [ShareX](https://getsharex.com).
> It keeps the ShareX capture-and-upload design and adds a native Wayland capture stack, and many UI changes.

## What this fork changes

- A native Wayland capture stack: layer-shell overlays draw the region selector, the recording outline, and the recording controls. No XWayland is needed.
- A two-in-one selector: press the region hotkey, hover to pick a window, or drag to pick an area. One flow covers both.
- Correct multi-monitor recording: the recorder picks the correct output for any region, including regions that cross two monitors.
- Fast overlays: the overlays repaint only the pixels that change. Hover and drag stay smooth, and video playback on the desktop is not affected.
- Settings inside the main window: settings open as a page in the app, with a Back button. The separate settings window still exists for the tray menu.
- One instance only: a second start forwards its command to the running instance and exits. The forwarding socket accepts only the owner of the session (0700 directory, 0600 socket), with timeouts and client limits.
- One tray icon: the tray registers a single StatusNotifier item.
- Telemetry off by default. Set `SNAPX_TELEMETRY=1` to turn it on.
- Clean, uniformly aligned menus and an in-app settings layout.

## Features from upstream SnapX

- High-DPI screens, including HDR screenshots that keep correct colors on KDE Plasma Wayland.
- OCR on all platforms, powered by PaddleOCR.
- About 95% of the ShareX uploaders. The custom uploader format (`.sxcu`) works.
- Image formats: PNG (also animated), WEBP (also animated), AVIF, JPEG, GIF, TIFF, and BMP.
- GPU-accelerated UI on .NET 10. Users do not need to install .NET.
- Full configuration from the command line, with flags and environment variables.
- History and image metadata in SQLite. Configuration in YAML, with auto migration from JSON.
- XDG base-directory layout on Linux and macOS.

## Platform support

| Platform | Capture | This fork adds |
| --- | --- | --- |
| Linux (Wayland) | XDG portals, plus native layer-shell overlays | Region/window picker, recording outline, native recording controls |
| Linux (X11) | Direct X11 capture | The standard SnapX behavior |
| Windows | Direct3D 11 and WinRT | The standard SnapX behavior |
| macOS | XCap | The standard SnapX behavior |

The native Wayland work targets wlroots-based compositors and is tested on Hyprland. KDE Plasma and GNOME use the portal paths from upstream.

## Build

You need .NET 10 SDK, `gcc`, and the Wayland client libraries (`libwayland-client`).

```sh
dotnet build SnapX.slnx --no-incremental -m:1
```

The build compiles the native helpers and puts them next to the app binary.

To make a release package:

```sh
dotnet run --project build --no-restore -- build --no-color
```

## Configuration

- `SNAPX_TELEMETRY=1` turns telemetry on. The default is off.
- `SENTRY_DSN` sets a Sentry endpoint for maintainers. It works only when telemetry is on.

## License

GPL-3.0. See [LICENSE.md](./LICENSE.md). This project keeps the license of ShareX and SnapX.

## Credits

- [ShareX](https://getsharex.com) - the original Windows tool.
- [SnapX](https://github.com/SnapXL/SnapX) and its team - the cross-platform base of this fork.
- The upstream README lists the upstream contributors and backers.
