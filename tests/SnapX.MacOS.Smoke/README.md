# macOS native smoke tests

Run on macOS with SnapX open and Screen Recording permission granted to the host running the probe:

```sh
dotnet run --project tests/SnapX.MacOS.Smoke -- --require-capture
```

The probe checks native cursor lifetime, nonempty window bounds and overload dispatch, display lookup, fullscreen capture, named/typed monitor capture, Retina region dimensions and capture of a live SnapX window. Captures are decoded in memory and deleted; nothing is uploaded. A terminal sandbox may hide WindowServer and permission access, so run from a normal local terminal.

Use `--tray-click-policy` for a bounded native-helper check that requires no
capture permission. It verifies ordinary left-click routing to SnapX's primary
recording action while right-click and Control-click remain native-menu input.

Add `--clipboard` to check 128 seeded text roundtrips containing Unicode, quotes, backslashes, newlines and shell syntax, plus PNG pixel/transparency preservation. This needs Apple's command-line tools to compile the small AppKit clipboard helper. Every available representation of the original clipboard is backed up and restored. If any advertised representation cannot be read, the probe stops before changing the clipboard. Do not copy new content while this optional test runs.

Without `--require-capture`, lack of Screen Recording permission is reported as blocked without failing other checks. With it, missing permission returns a failing exit code. This is a bounded integration test, not proof that all application workflows work.

The platform-neutral backing-pixel composer can be checked independently with
`dotnet run --project tests/SnapX.Core.Fuzz -- --frozen-composition-probe`. It
uses synthetic 2x/1x frames and a display gap, so it needs no screen permission.

## Frozen screenshot selector integration check

With a video or continuously changing clock visible on any connected display, start the macOS screenshot region picker. The displayed desktop must freeze as the picker appears, and the log must say `Frozen macOS selector ready across N displays; all frames were captured before overlays were mapped.` Drag a region and release: the saved image must contain the same frozen frame shown while selecting, without the selection outline or information label. The completion path must not invoke a second screen capture.

While the picker is visible, click and drag through the macOS menu-bar area, including across the Apple logo. The selector must receive the pointer input and the menu must not open. A selection may include those pixels. Repeat with Escape: no image or new history item should be created. Repeat the picker immediately to exercise the selector gate. Check the primary Retina display and a display with a negative desktop origin, then drag across a mixed-scale display boundary and verify both halves align without a missing seam.

Test a recording-region selection separately: it must remain a live transparent geometry picker, return only geometry, and never request or create a screenshot.

While a screenshot picker is open, `dotnet run --project tests/SnapX.MacOS.Smoke -- --overlay-opacity` captures only its native window and requires at least 90% fully opaque pixels. While a recording-region geometry picker is open, add `--expect-transparent` and it instead requires at least 90% fully transparent pixels. These probes inspect the selector surface in memory and do not save the underlying desktop.

For frozen-preview regression testing, first make a visibly nonblack desktop (for example, a light document occupying most of the display), open the screenshot picker, then add `--expect-content` to `--overlay-opacity`. This also requires at least 1% nonblack pixels, catching a solid black native surface that an opacity-only check would accept. Do not use this opt-in check with an intentionally black desktop.

For multiple displays, add `--all-displays` to require exactly one overlay matching
each connected display's full bounds, including its menu-bar area, and check every
overlay's opacity.
After completion or cancellation, `--no-overlays` checks that no selector window
remains. `--multi-monitor-capture` captures a small rectangle straddling each
touching display pair and checks the decoded dimensions without saving pixels.
Manually exercise a window click on a different display, a drag across a display
boundary, Escape from a secondary display, and immediate reopening. Screenshots
may span displays; video recording still requires a region within one display.

Use `--clipboard --clipboard-only` to run the guarded clipboard checks without requiring a visible SnapX window for the later capture checks.

Use `--ocr` to generate a two-line text image, recognize it through SnapX's local OCR engine, and check that a blank image yields no text. It downloads missing ONNX models but never uploads an image. Set `XDG_CACHE_HOME` to an isolated cache directory if desired. To test the NativeAOT runtime, publish this smoke project (`PublishAot=true`, `PublishTrimmed=true`, `SelfContained=true`).
