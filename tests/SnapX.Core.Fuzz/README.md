# Core fuzz and regression harness

Run from the repository root with a .NET SDK matching `global.json`:

```sh
dotnet run --project tests/SnapX.Core.Fuzz -c Release
# Replay another deterministic campaign:
dotnet run --project tests/SnapX.Core.Fuzz -c Release -- --seed=1
```

To isolate thumbnail writes from the desktop user's cache, set `XDG_CACHE_HOME` to a temporary directory before starting the process. The harness creates temporary input files and SQLite databases; it does not contact upload services or exercise the real clipboard.

Coverage includes bounded/reversed screenshot regions, region cancellation and image ownership, simulated hotkey lifecycle and identity, portal accelerator formatting, uploader URI validation, escaped/nested custom-uploader templates, pathological template depth, history filtering and SQLite commit ordering, clipboard event routing, thumbnail cache concurrency and corruption recovery, localization validation, and hotkey display/parse round trips. Linux-only Hyprland binding checks are explicitly skipped elsewhere. The recorder-stop test uses an isolated child shell process, not a screen recording.

This is deterministic fuzz/property coverage and regression testing, not exhaustive application fuzzing. It does not establish macOS permission behavior, native global hotkeys, screen capture/recording correctness, UI behavior, or authenticated third-party upload compatibility. Those require separate integration/manual checks.

On macOS, `--macos-hotkey-probe` exercises real Carbon registration, duplicate conflict rejection, unregister, and re-registration. Run with desktop/WindowServer access; a sandbox may return `eventInternalErr` (-9868). It does not synthesize keys or prove event delivery. Real shortcut activation must be checked in the GUI. Carbon bindings use physical ANSI key positions; Win/Cmd/Command maps to Command and Alt/Option maps to Option. New macOS defaults are Control+Shift+Command+1 through 5; Reset shortcuts adopts these for existing installations.

`--mac-recording-probe` runs the macOS recording integration probe with `SNAPX_TEST_FFMPEG` set to an absolute FFmpeg path. This probe captures the desktop and must run with the relevant desktop/screen-recording access.
