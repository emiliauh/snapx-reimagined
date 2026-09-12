# macOS validation — September 11, 2026

SnapX builds and runs on this Apple Silicon Mac. The final native app has been
launched and exercised; this is not a claim that every uploader, hardware
configuration, or application feature has been exhaustively tested.

## Deliverables

- `Output/SnapX.app`: native ARM64 app, including the tested FFmpeg executable.
- `Output/SnapX-macOS-arm64.zip`: portable distribution of that app bundle.
- `Output/snapx`: native CLI output.
- `Output/snapx-ui`: native GUI and browser messaging host output.

The application needs no .NET runtime installation. The local bundle is ad-hoc
signed and passes `codesign --verify --deep --strict`; it is not notarized for
public distribution. The temporary build SDK is .NET 10.0.100. The host is an
Apple M1 Pro running macOS 27.0 with Xcode command-line tools and three displays.
The Intel GUI, CLI and messaging host also compile as Native AOT x86_64 and
run under Rosetta on this Apple Silicon host. The Intel GUI opens, initializes
SQLite, registers hotkeys, forwards a secondary launch and saves a live region
capture. This is not physical Intel hardware validation; the Intel test snapshot
predates the follow-up uploader/OCR fixes.

## Fixes

- Replaced a Retina/multiple-display Rust capture panic with macOS native screen
  capture. Added missing monitor/window overloads, signed desktop coordinates,
  native ABI/lifetime corrections, and capturable-window filtering.
- Made the macOS screenshot picker an opaque frozen-frame overlay. It captures
  every display before mapping any selector surface and composes the selection
  from those retained pixels, so desktop content cannot change between selection
  and output. Recording-region selection remains a live geometry-only overlay.
- The annotation toolbar now appears when that frozen overlay becomes ready.
  Pen, shape, arrow, text, color, and editing tools operate live across the full
  frozen desktop before or after choosing a region; region mouse-up only commits
  the crop, and Enter or the checkmark performs the final clipped export.
- Corrected native selection coordinates against the actual window bounds,
  including macOS work-area offsets and displays above the primary display.
- Added one coordinated overlay per detected monitor. Window/monitor click
  selection and region dragging use global desktop coordinates; screenshot regions
  can cross display boundaries. Mixed-DPI output is composed at the highest backing
  scale among intersected displays. The AppKit windows use complete display bounds
  above the menu-bar level and explicitly accept pointer input, including over the
  Apple menu area.
- Implemented FFmpeg AVFoundation recording and removed the obsolete rejection
  in `ScreenRecordManager.ValidateStart`. Recording supports a region contained
  within one display, with Retina geometry and negative monitor coordinates.
- Implemented Carbon hotkey registration and Command-based defaults. Existing
  saved Print Screen shortcuts need changing or **Restore default hotkeys**.
- Replaced an incorrectly declared variadic `fcntl` call with fixed-signature
  `flock`. This fixed the native executable exiting before opening its window.
- Fixed portable configuration/cache paths and deferred Keychain access until
  an actual secret is encrypted/decrypted. Saved-settings startup fell from
  roughly 29 seconds to about 1.5–1.7 seconds in the development build; the native
  release opened in approximately 1.2 seconds.
- Re-enabled the database table using typed dynamic-column templates compatible
  with Native AOT. Fixed macOS sound playback and in-app notification handling.
- Bounded uploader-template nesting, hardened shortcut parsing, bounded browser
  message allocations, validated UTF-8, and drained child-process output pipes
  concurrently in the native messaging host.
- Follow-up: repaired Imgur/Dropbox native JSON serialization, Imgur upload retry
  stream positioning and Dropbox success reporting. Disposed OCR engine and
  internally owned images, and kept the SQL popup's Run button within its bounds.

## Verified

| Area | Result |
| --- | --- |
| Complete solution | Compiles with zero errors. Existing nullable, obsolete API, and trimming/AOT warnings remain. |
| Native publish | GUI, CLI and browser messaging host publish successfully for `osx-arm64`. |
| Packaged native GUI | Opens, loads saved settings, shows history, and runs capture and recording without the SDK. |
| Native still capture | Full desktop 7280×5114; primary display 3456×2234; 32×24 logical Retina region yields 64×48 pixels; named monitor and real window capture pass. |
| Frozen region capture | Screenshot mode captures display frames before mapping opaque overlays, then crops/composes from those retained frames. Recording-region mode remains transparent and geometry-only. Deterministic composition coverage checks Retina/standard-DPI boundaries and transparent desktop gaps. |
| Multiple-monitor selector | One native overlay is created with each display's complete CoreGraphics bounds, including the menu bar. Cross-display output uses measured source-image backing scales. The native opacity probe supports both frozen screenshot and live recording-region modes. |
| Recording | All three displays produce decodable MP4s. Manager region start → pause → resume → stop → concatenated output passes. Final packaged GUI also starts/stops recording with bundled FFmpeg and produces a decodable MP4. |
| Hotkeys | Native registration, duplicate rejection, unregister/re-register, and GUI Apply pass. Follow-up: user physically pressed Control+Shift+Command+1, confirmed the region overlay opened, then cancelled with Escape; application log confirms dispatch and Escape. |
| Clipboard | Follow-up: 128 seeded Unicode/escaping text round-trips and PNG pixel/transparency round-trip pass. Original clipboard representations restored. |
| Legacy microphone probe | Before direct system audio was added, a packaged GUI microphone probe produced decodable AAC. Microphone capture is no longer exposed or requested by SnapX. |
| OCR | JIT and Native AOT ARM64 probes recognize synthetic two-line text exactly, return no text for a blank image, recognize file input and preserve caller-owned images. Models download locally; test images are not uploaded. |
| Authenticated upload | Supplied Porkpaste SXCU imported through the actual native JSON import path and uploaded a synthetic 16×16 PNG successfully. No configuration secrets copied into the repository or logs. Seven Imgur/Dropbox loopback integration cases also pass with JSON reflection disabled and under Native AOT. |
| Database | Captured records and dynamic columns render in the repaired table. Follow-up: repaired popup Run button executes `SELECT 1 AS Probe` and displays 1. |
| Sounds | All four embedded FLAC sounds decode through native `afplay`. |
| Core fuzz/property checks | Three campaigns passed 762,513 assertions; final run after the recording-gate regression passed 254,212 assertions. These are bounded deterministic tests, not exhaustive proofs. |
| Native message framing | 20,007 checks pass for fragmented Unicode messages, truncation, invalid UTF-8, EOF, and invalid lengths. Published host rejects malformed input with exit 1 and no protocol stdout. |
| Local IPC | Eight concurrent secondary launches exit successfully; 100 malformed/truncated frames do not kill the primary; subsequent forwarding succeeds. |
| Instance locking | JIT and Native AOT probes verify ownership, contention and release with `flock`. |

Evidence summaries are in `artifacts/macos-validation`. Screen captures and
legacy audio recordings stayed local; upload actions were disabled in capture test
profiles. Only a generated synthetic test image was sent to the user-specified
Porkpaste upload service.

## Remaining limits

- Physical activation was confirmed for the region shortcut; every shortcut
  combination and keyboard layout was not exercised.
- Live authenticated services other than the supplied Porkpaste destination,
  browser-extension integration, protected/DRM video, and every tool/settings combination were not
  exercised end-to-end. Recording across multiple displays is explicitly rejected.
- Screen Recording permission depends on the launching application and macOS
  privacy settings. Finder launch after relocation, permission-denial/regrant
  scenarios, and other macOS versions still need testing.
- Protected/DRM surfaces may be absent or black in the frozen frame because macOS
  intentionally excludes them from Screen Recording capture.
- AOT/trimming warnings remain, especially in third-party and uploader code.

## Reproduce

Install .NET 10, then build from this directory:

```sh
export DisableGitVersionTask=true # Same fallback as CI; avoids GitVersion history analysis.
AVALONIA_TELEMETRY_OPTOUT=1 dotnet build SnapX.slnx --no-incremental -m:1
AVALONIA_TELEMETRY_OPTOUT=1 dotnet run --project build --no-restore -- build --no-color
dotnet run --project tests/SnapX.Core.Fuzz --configuration Release -- --seed=23063
dotnet run --project tests/SnapX.NativeMessaging.Fuzz
dotnet run --project tests/SnapX.MacOS.Smoke
dotnet run --project tests/SnapX.Core.Fuzz -- --macos-hotkey-probe
SNAPX_TEST_FFMPEG=/path/to/ffmpeg dotnet run --project tests/SnapX.Core.Fuzz -- --mac-recording-probe
SNAPX_ALLOW_ADHOC=1 bash packaging/macos-bundle.sh Output/snapx-ui Output/SnapX.app
```

Bundle creation requires a new output path. The standard source build expects
FFmpeg to be installed or configured; this session's delivered app additionally
contains the tested binary from the PyPI `imageio-ffmpeg` 0.6.0 ARM64 wheel.
See `Output/FFmpeg-LICENSE.txt` and `Output/FFmpeg-PROVENANCE.txt`.


## Installer and launch at login

The `0.5.0-alpha.5` Apple Silicon Native AOT publish completed successfully. The
installed bundle in a temporary `~/Applications` location showed the optional
launch-at-login dialog with **Not now** focused by default. Declining persisted
`MacOSLoginPromptDismissed: true`; relaunch did not show the dialog again.
Application settings still reported an unregistered login item, with Enable
available and Disable unavailable. The temporary installation was removed.

The startup policy/native ABI probe passed 25 checks, including fresh-install
`NotFound`, explicit opt-in, decline, pending approval, external removal, and
installed-path eligibility. The fresh-install behavior follows
[Apple DTS guidance](https://developer.apple.com/forums/thread/719862): `NotFound`
can mean the system has never seen the service. Actual login-item registration,
OS approval, and a logout/login cycle were not exercised on the user's account;
no real login items were changed during validation.

The bundle includes SnapX and FFmpeg license/provenance documents. Packaging
checks verify the app signature, compressed DMG, mounted contents, Applications
shortcut, and copied app signature. Release builds in CI also produce DMG and
SHA-256 artifacts. The locally published installer is Apple Silicon only,
ad-hoc signed, and not notarized.

```sh
dotnet run --project tests/SnapX.MacOS.LoginItems -p:DisableGitVersionTask=true -- --native-status
bash packaging/macos-dmg.sh Output/SnapX.app Output/SnapX-0.5.0-alpha.5-macOS-arm64.dmg
```

## First-launch privacy setup and signing identity

The alpha.7 permission work adds native Core Graphics screen authorization and
AVFoundation audio-input authorization requests, a first-launch checklist,
matching System Settings links, and capture/recording permission guards. Screen
Recording is required; audio-input access is optional and used only for an
explicitly selected system-audio loopback device. Statuses are read from macOS;
Finish setup stays disabled until Screen Recording is authorized. A failed
permission check routes to setup before capture.
The installed Native AOT app correctly showed denied screen access and
undetermined microphone access, with the second request and Finish disabled.
After the user approved both entries, an installed app capture completed and
stayed local. The focused permission probe passed seven native status, routing,
guard, and block-callback checks without changing host permissions. A broader
fuzz run passed 254,219 checks. The final Native AOT executable also reported a
denied headless capture without trying to parent a dialog to a missing window.

The alpha.8 follow-up makes screenshots and recordings headless, preserves the
OCR/QR tool window when it needs a selected image, supports detected macOS
loopback audio devices in generated FFmpeg commands, and moves live selector
windows above the menu-bar level with full CoreGraphics display bounds. The
updated property suite passed 254,228 checks. An ad-hoc arm64 NativeAOT bundle
exited in 0.19 seconds for a warm AppleEvent Quit and 0.06 seconds for SIGTERM;
a first-run permission-dialog Quit took 2.52 seconds. Both paths produced no
new crash report. The packaged app was reduced from 210,723,197 to 185,856,400
bytes (11.8%) by architecture thinning and symbol stripping. This VM had no
installed loopback device and did not grant the new ad-hoc identity Screen
Recording access, so real audio samples and menu-bar hit testing still require
manual validation on an approved installation.

The alpha.9 follow-up replaces loopback/input-device discovery with direct
ScreenCaptureKit playback-audio capture. Video and system audio share one
SCStream timestamp domain and are written to an H.264/AAC intermediate before
FFmpeg applies the selected final codec. The microphone permission UI, usage
description, entitlement, and AVFoundation input enumeration were removed.
SnapX explicitly disables microphone capture on macOS 15 and never requests
microphone access. Native notifications now deliver completed screenshot and
recording results through Notification Center while SnapX runs headlessly.
The native bridge and managed lifecycle probes pass; non-silent playback-audio
and notification delivery still require packaged validation under the installed
app's approved identity.

An installed-upgrade test exposed a separate signing problem: TCC rejected a
previously stored code requirement after the ad-hoc executable changed. Local
and CI builds use a separate bundle ID while retaining the visible `SnapX`
name, and the tested local copy worked after the user approved that identity. Ad-hoc rebuilds still
need fresh approval. A stable Developer ID Application signature is needed for
dependable identity across distributed updates. A persistent self-signed Code
Signing identity is also a viable local-only strategy that does not need an
Apple Developer account, but it must be verified on the target Mac before
claiming permission retention. The app does not reset or edit TCC permissions
itself.
