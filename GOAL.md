# GOAL — SnapX Scrolling-Capture & Inline Annotate (ShareX parity)

Status: Parts 1–4 implemented. Part 4 inline annotate is reachable on native
Wayland/Hyprland/NVIDIA via the software-rendering Avalonia selector path (see
Part 4 notes below).

## Goal

Ripen SnapX's scrolling capture and capture workflow into ShareX parity on
Linux/Wayland:

1. No "Cannot access a closed file" / "Error saving ApplicationConfig /
   HotkeysConfig" spam; hotkeys work.
2. Scrolling capture terminates reliably and produces a saved (stitched) image.
3. A ShareX-style scrolling-capture result window with Capture... / Upload /
   Save / Options... and the stitch dimensions.
4. Scrolling capture wired into settings, the home-page capture section, and
   the tray/notification menu.
5. An embedded (non-modal) annotate toolbar on the capture itself — NOT the
   modal CapturedImageEditorWindow.

## Done & verified

- **Config-save crash fix** (`SettingsBase.WriteTempFile` uses `leaveOpen: true`
  so `Flush(flushToDisk:)` no longer throws after `SaveToStream` disposes the
  stream). Confirmed on relaunch: no closed-file errors, config saves success.
- **ScrollingCaptureWindow** (`SnapX.Avalonia/Views/ScrollingCaptureWindow.cs`):
  ShareX-style result window; dark theme; Capture... / Upload / Save / Options...
  toolbar and dimensions label; image in a ScrollViewer. Owns and disposes the
  image clone (and its display bitmap) on `Closed`.
- **Event wiring** (`App.axaml.cs`): `ListenForEvents` subscribes
  `NeedScrollCaptureResultEvent`; the handler posts on `Dispatcher.UIThread.Post`
  and shows the window. Image is disposed if the ctor throws.
- **Disposal/ownership** (`TaskHelpers.OpenScrollingCapture`): `manager.Result`
  is saved synchronously; a dedicated clone is published to
  `NeedClipboardCopyEvent` and its `Completion` awaited before release; the
  window receives its own clone; `result` is disposed in `finally` after
  `manager.Dispose()`; no use-after-dispose / double-dispose in the normal flow.
- **Reliable stop condition** (`ScrollingCaptureManager.StartCaptureAsync`):
  tracks stitched-height growth and stops after `StagnancyLimit` (3) stagnant
  frames; `MaxFrames` lowered to 100; `CombineImages` returning null surfaces as
  a graceful break. This keeps animated/sticky-header pages from running to the
  frame cap.
- **Options persistence**: the result window's Options dialog mutates the shared
  `ScrollingCaptureOptions` instance obtained from
  `ScrollingCaptureManager.GetSharedOptions`, so the next capture reuses tuned
  values. `ShowRegion` is now honored (the reviewer flagged it as a dead option);
  when disabled and no auto-capture region is set, capture cancels instead of
  silently showing a selector.
- **Scroll input** (`InputHelpers`): `SendMouseWheel` prefers the X11 XTest wheel
  (reaches XWayland windows under the pointer without keyboard focus); the
  wheel fallback now uses the small `Down` key (overlap-preserving) instead of a
  full-viewport `PageDown`, so consecutive frames still overlap and the stitcher
  can follow the page to the bottom.
- **Fuzz**: `SnapX.Core.Fuzz` passes 242,194 checks. `dotnet build SnapX.slnx
  --no-restore -m:1` yields 0 errors.

## Part 4 — inline annotate (NOT the modal editor)

Implemented in `RegionSelectorWindow` (`SnapX.Avalonia/Views/RegionSelectorWindow.axaml.cs`):
after the region is dragged and its image cropped, an inline annotation toolbar
(Rect / Redact / Freehand / Arrow / Text / Crop / Undo + text field, Save /
Cancel) appears over the capture **before it is committed**. Save composites the
marks onto the captured image; Cancel commits the plain image. It reuses the
same `ImageAnnotation` primitives and canvas behavior as the modal
`CapturedImageEditorWindow`, but is a distinct, non-modal surface.

Scope and correctness (post-review):
- The toolbar only renders when `RegionCaptureOptions.AnnotateCapture` is true
  (default) and the selection is not a window/monitor/region picker mode.
- Scrolling capture sets `AnnotateCapture=false` for its region selection, since
  it needs only the rectangle, not an annotated image — avoiding a wasted
  annotation step.
- The commit path ALWAYS restores the main window and closes the selector
  (fixing a hang where the silent core selector returned before `Close()` and
  left the caller's `closedTask` uncompleted).
- The annotation `WriteableBitmap` is disposed on finish; crop annotations are
  applied after other marks so their pixel coordinates stay consistent;
  `IsSilentMode` only suppresses the after-capture upload task.

### Native Wayland reachability (fixed)

**Root cause (evidence):** On native Wayland/Hyprland/NVIDIA, Avalonia.Wayland
builds GPU render surfaces through `WaylandEglWsiPlatformGraphics` (EGL WSI).
With `SNAPX_WAYLAND_GPU=1` (or before the empty-`GlProfiles` default),
`libnvidia-eglcore` SIGSEGVs on the `AvaloniaWayland` thread during
`eglMakeCurrent`, especially when a full-screen selector maps while the main
toplevel is hidden and later remapped. `Program.cs` already mitigates this by
defaulting to software `wl_shm` rendering (empty `GlProfiles`); transient EGL
surfaces (popups, `OverlayLayer`, `FAContentDialog`) remain disabled on native
Wayland for the same reason.

**Fix:** Route only annotated free-form region capture (`AnnotateCapture=true`,
not a picker mode) to the Avalonia `RegionSelectorWindow` on native Wayland;
all other captures keep slurp / the native picker. The selector is allowed only
while `SNAPX_WAYLAND_GPU` is unset (software rendering). OCR/QR utility
selectors pass `AnnotateCapture=false` so they stay on slurp.

Known limitations:
- Committed annotations are not re-rendered live on the surface (the canvas
  draws the drag preview only); this mirrors the modal editor's existing
  `AnnotationCanvas`, which shares the behavior.
- Setting `SNAPX_WAYLAND_GPU=1` disables the annotated Avalonia selector on
  native Wayland (EGL crash returns); use the default software path for inline
  annotate.

## Open decision

Resolved: inline annotate stays in the region selector (not post-capture). Native
Wayland uses the Avalonia selector for annotated free-form capture when software
rendering is active; slurp remains for non-annotated and picker flows.

## Verification note

The Opus 5 verification subagent (`multi_agent_v1__spawn_agent`,
`anthropic/claude-opus-5`) is not exposed in this harness; the available
`reviewer` agent was used instead. Two reviews were run: the first validated
ownership/disposal, stop condition, options persistence, and input changes as
sound, and flagged `ShowRegion` as a dead option (now wired); the second
reviewed the Part 4 annotation flow and found a hang + three correctness issues
(picker-mode gating, bitmap leak, crop ordering), all now fixed. Live
scroll-to-bottom was partially verified (result window renders, hotkey fires,
capture stops/saves, no closed-file errors) but the automated Wayland capture is
hampered by the global hotkey double-firing under the test driver; the
scroll-correctness fix is in place and build/fuzz-verified.
