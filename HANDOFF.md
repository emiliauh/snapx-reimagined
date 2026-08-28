# SnapX Scrolling Capture / ShareX-Parity Handoff

Date: 2026-08-28
Branch: codex/desktop-issue-fixes
Workspace: /home/emi/ShareX-Linux

This is a handoff script for an agent continuing the work. Read it fully before
touching anything. The worktree is intentionally and heavily dirty with
pre-existing user changes (recording UI, hardening, network/zip/fuzz, prior
ShareX-parity work). **Never run git reset or git checkout --.** Inspect diffs
before editing. Apply all file edits via apply_patch (no shell write-tricks).

---

## Goal

Complete SnapX scrolling capture and capture-workflow parity with upstream
ShareX on Linux/Wayland. The end state must satisfy all of:

1. The "Cannot access a closed file" / "Error saving ApplicationConfig /
   HotkeysConfig" spam is gone and hotkeys work.
2. Scrolling capture terminates reliably and produces a saved image.
3. A ShareX-style scrolling-capture result window with Capture... / Upload /
   Save / Options... buttons and the stitch dimensions.
4. Scrolling capture is wired into settings, the home-page capture section,
   and the tray/notification right-click menu.
5. An embedded annotate/editor toolbar lives on the capture itself (ShareX
   screenshot-editor style, user second screenshot), NOT the modal editor.

---

## Environment facts

- Linux / Wayland / Hyprland. DISPLAY=:0, WAYLAND_DISPLAY=wayland-1,
  XDG_SESSION_TYPE=wayland.
- DP-2 = 2560x1440 physical, 1600x900 logical, scale 1.6 (main, non-rotated).
  DP-3 = 1920x1080 scale 1.25, rotated, at x=-864.
- Build: dotnet build SnapX.slnx --no-restore -m:1. A running app locks
  snapx-ui.pdb and causes a spurious AVLN9999 PDB-lock build failure, so
  stop the app before building.
- Fuzz suite: dotnet run --project tests/SnapX.Core.Fuzz -c Release --no-build.
  Contains VerifyScrollingCaptureStitching and VerifyCapabilityGates.
- App binary: SnapX.Avalonia/bin/Debug/net10.0/linux-x64/snapx-ui.
- Input tools: xdotool, ydotool (no scroll subcommand), wtype (Wayland
  virtual-keyboard; supports Down/Up/Page_Down/Page_Up/Home/End, no wheel).

---

## Part 1 - DONE, verified, do not redo

### 1a. Config-save crash fix (the error spam)

SnapX.Core/Utils/SettingsBase.cs WriteTempFile now does:

    SaveToStream(fileStream, UseEncryption, leaveOpen: true);
    fileStream.Flush(flushToDisk: true);

Root cause: SaveToStream used a StreamWriter that disposed the stream when
leaveOpen=false, so Flush() threw ObjectDisposedException. leaveOpen: true
fixes it and stops the hotkey-breaking error spam. Builds clean.

### 1b. Scrolling capture input layer (Wayland scroll)

SnapX.Core/Utils/Native/InputHelpers.cs: HasInputBackend() is true on Linux
if XTest or wtype/ydotool present, false on Windows. On native Wayland,
SendMouseWheel/SendKeyPress prefer the Wayland backend first (XTest on
Wayland does not reach native Wayland windows). TrySendKeyWayland uses
wtype -k first. ToWtypeKeyName maps PageDown/PageUp/Down/Up/Home/End to
wtype names. SendMouseWheel falls back to PageDown/PageUp via wtype.

### 1c. Resolved Opus-review defect

Reverted LocalRectangle in ScreenRecorder.cs to FormatGeometry(GlobalRectangle)
because wf-recorder -g needs global logical coords. Removed dead Scale field.

### 1d. Scrolling-capture discovery wiring (builds)

- HotkeyManager: added new(HotkeyType.ScrollingCapture, Control|Shift|S).
- SettingManager: idempotently adds ScrollingCapture if missing. Do NOT
  reintroduce the HotkeysConfig.SaveAsync call (caused a save race).
- Resources.resx + Lang.cs: UI_Dropdown_ScrollingCapture, UI_Dropdown_Annotate.
- MainView + App.axaml.cs: Scrolling capture and Annotate added to Capture
  dropdown and tray menu; call OpenScrollingCapture / OpenImageEditor.

---

## Part 2 - IN PROGRESS: the scrolling-capture result window

### File to create: SnapX.Avalonia/Views/ScrollingCaptureWindow.cs

Does NOT exist yet (verified). Create it with apply_patch + -prefixed content
for *** Add File. Design:
- Ctor(SharpImage image, TaskSettings taskSettings, ScrollingCaptureOptions options).
- Title "SnapX | Scrolling capture"; dark theme (30,30,30) like the editor.
- Top toolbar Buttons: Capture..., Upload / Save, Options..., plus a label of
  image.Width x image.Height.
- Image via App.SnapX.ConvertImageSharpImgToAvalonia(_image) in ScrollViewer.
- Capture() -> TaskHelpers.OpenScrollingCapture(_taskSettings).
- UploadOrSave() -> if _options.AutoUpload call UploadManager.RunImageTask; else
  TaskHelpers.SaveImageAsFile then publish NeedClipboardCopyEvent(_image).
- Options() -> modal dialog with NumericUpDown for StartDelay/ScrollDelay/
  ScrollAmount, CheckBoxes for AutoScrollTop/AutoUpload/AutoIgnoreBottomEdge.
- The window owns and disposes the image clone it receives.

### Event already added

SnapX.Core/Events.cs NeedScrollCaptureResultEvent(Image, TaskSettings,
ScrollingCaptureOptions) exists (~line 204). Plain carrier; UI owns Image.

### Already wired (worker side)

TaskHelpers.cs OpenScrollingCapture (~line 1430) publishes
NeedScrollCaptureResultEvent(resultClone, ...), saves result, then publishes
NeedClipboardCopyEvent(result).

### NOT yet done - the UI subscription

App.axaml.cs ListenForEvents() (~line 720) does NOT subscribe
NeedScrollCaptureResultEvent. Add Subscribe<NeedScrollCaptureResultEvent> and
a Dispatcher.UIThread.Post handler that new ScrollingCaptureWindow(...).Show().

---

## Part 3 - Bugs to fix

### 3a. Disposal race in OpenScrollingCapture

result == manager.Result and manager.Dispose() disposes it in finally. The
current code saves and publishes NeedClipboardCopyEvent(result) using result,
which may be disposed before the async clipboard handler reads it. Fix: use
resultClone for save and clipboard publish, hand resultClone to the window
(which owns it), and only dispose after window close AND clipboard completes.

### 3b. Scrolling capture never stops / never saves

StartCaptureAsync loops while !stopRequested && frames < MaxFrames (200).
Only stop is CompareLastTwoImages (consecutive equal) or 200 frames. Animated
or sticky-header pages never produce an identical pair, so it runs to 200 and
the user reports it never stops. Recommended: track stitched height growth and
stop when growth stops for ~3 frames; lower the practical frame cap; surface
CombineImages returning null. Do not break VerifyScrollingCaptureStitching.

### 3c. Options must persist

Make Options() write back to the shared ScrollingCaptureOptions so a later
Capture... reuses tuned values (StartDelay, AutoScrollTop, ScrollDelay,
ScrollMethod, ScrollAmount, AutoIgnoreBottomEdge, AutoUpload, ShowRegion).

---

## Part 4 - Scope decision: embedded annotate/editor

User wants the edit menu on the capture itself (ShareX screenshot-editor
toolbar in the region overlay, second screenshot), NOT the modal
CapturedImageEditorWindow. Large feature, still open. Options:
(1) inline toolbar on RegionSelectorWindow (most faithful, large);
(2) inline editor bound to captured image fullscreen with floating toolbar
(medium); (3) reuse AnnotationCanvas toolbar as in-capture overlay (MVP).
Recommend option 3 as MVP or clarify scope. Do not call the modal editor the
inline editor.

---

## Part 5 - Verification protocol

1. dotnet restore SnapX.slnx --locked-mode
2. dotnet build SnapX.slnx --no-restore -m:1 - expect 0 errors.
3. Fuzz: dotnet run --project tests/SnapX.Core.Fuzz -c Release --no-build.
4. Stop app first (locks PDB): pkill -f snapx-ui
5. Rebuild + relaunch under SnapX.Avalonia/bin/Debug/net10.0/linux-x64.
6. Confirm no closed-file errors, hotkeys work, scroll capture stops/saves and
   shows the result window with working Capture/Upload-Save/Options.

Run the Opus 5 verification subagent before and after via
multi_agent_v1__spawn_agent with model anthropic/claude-opus-5. Use sparingly.

---

## Immediate next steps

1. Write ScrollingCaptureWindow.cs (+ -prefixed Add File patch).
2. Subscribe NeedScrollCaptureResultEvent in App.axaml.cs + handler.
3. Fix the disposal race in OpenScrollingCapture.
4. Fix the scroll stop condition so it terminates and saves.
5. Make Options() persist.
6. Build + fuzz + restart; verify hotkeys, scroll stop/save, result window.
7. Scope/decide the embedded annotate/editor (Part 4).

Referenced screenshots:
- /tmp/codex-clipboard-58020549-...png - error spam.
- /tmp/codex-clipboard-ab27cedc-...png - ShareX embedded editor toolbar.
- /tmp/codex-clipboard-8eabfe55-...png - ShareX scroll-capture result window.
