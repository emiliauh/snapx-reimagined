# macOS native smoke tests

Run on macOS with SnapX open and Screen Recording permission granted to the host running the probe:

```sh
dotnet run --project tests/SnapX.MacOS.Smoke -- --require-capture
```

The probe checks native cursor lifetime, nonempty window bounds and overload dispatch, display lookup, fullscreen capture, named/typed monitor capture, Retina region dimensions and capture of a live SnapX window. Captures are decoded in memory and deleted; nothing is uploaded. A terminal sandbox may hide WindowServer and permission access, so run from a normal local terminal.

Add `--clipboard` to check 128 seeded text roundtrips containing Unicode, quotes, backslashes, newlines and shell syntax, plus PNG pixel/transparency preservation. This needs Apple's command-line tools to compile the small AppKit clipboard helper. Every available representation of the original clipboard is backed up and restored. If any advertised representation cannot be read, the probe stops before changing the clipboard. Do not copy new content while this optional test runs.

Without `--require-capture`, lack of Screen Recording permission is reported as blocked without failing other checks. With it, missing permission returns a failing exit code. This is a bounded integration test, not proof that all application workflows work.

## Live overlay integration check

With a video or continuously changing clock visible on any connected display, start the macOS region picker. The content must continue changing beneath its transparent selection outline; the log must say `Live macOS selector ready across N displays; no screenshot taken before selection.` There must be no capture-background log for that selection. Drag a region and release: the saved image must contain the content visible at completion, without the selection outline or information label. The log then says `Live multi-display selection complete`.

Repeat with Escape: no image or new history item should be created. Repeat the picker immediately to exercise the selector gate. Test a recording-region selection: it must return geometry without creating a screenshot. Check the primary Retina display and a display with a negative desktop origin.

The completion capture follows removal of the overlay by one nominal compositor frame (16 ms), plus operating-system capture latency. It is a screen capture at completion, not a frame-exact video-player snapshot.

While the picker is open, `dotnet run --project tests/SnapX.MacOS.Smoke -- --overlay-alpha` captures only its native window and counts RGBA alpha values. It requires at least 90% fully transparent pixels. This distinguishes true transparency from a window-inspection tool compositing transparent pixels onto white; it does not read or save the underlying desktop.

For multiple displays, add `--all-displays` to that probe to require exactly one
overlay covering each connected display's center and check every overlay's alpha.
After completion or cancellation, `--no-overlays` checks that no selector window
remains. `--multi-monitor-capture` captures a small rectangle straddling each
touching display pair and checks the decoded dimensions without saving pixels.
Manually exercise a window click on a different display, a drag across a display
boundary, Escape from a secondary display, and immediate reopening. Screenshots
may span displays; video recording still requires a region within one display.

Use `--clipboard --clipboard-only` to run the guarded clipboard checks without requiring a visible SnapX window for the later capture checks.

Use `--ocr` to generate a two-line text image, recognize it through SnapX's local OCR engine, and check that a blank image yields no text. It downloads missing ONNX models but never uploads an image. Set `XDG_CACHE_HOME` to an isolated cache directory if desired. To test the NativeAOT runtime, publish this smoke project (`PublishAot=true`, `PublishTrimmed=true`, `SelfContained=true`).
