# SnapX macOS permissions — agent handoff

Local investigation handoff. Do not automatically include this file or private diagnostic logs in a public commit or release.

## Start here

Continue fixing the **installed macOS application**, not just compiling the code. The issue is unresolved. Read this document, inspect the current working tree, reproduce the failure from `/Applications/SnapX.app`, and carry the fix through real permission approval and successful capture. Do not treat the existing alpha.6 installer as verified or ready to publish.

The user explicitly requested Sol (`gpt-5.6-sol`) subagents at **high** reasoning working in parallel. Use them for independent bounded investigations if capacity permits. The previous task hit its agent/thread limit; only one Sol/high agent could be resumed. Do not claim a swarm ran if tools reject additional agents.

## User's requested behavior

- First installed launch walks through the permissions SnapX uses, one at a time.
- A native permission request should cause **the installed SnapX app** to appear in the corresponding System Settings permission list. The user grants access; the app must not silently grant it.
- Each permission has an explanation, a request action, and a link to the correct settings panel.
- Each step remains incomplete until macOS actually reports that permission granted. Conversely, it must not remain falsely incomplete after successful approval and any required restart.
- Setup finishes only after the required steps are satisfied. It resumes after a restart if unfinished.
- Screenshots and recordings then actually work from the installed application.
- User's latest report: Screen Recording remains “incomplete” even after enabling it, and SnapX is missing from the Microphone permission list.
- The user **does not have an Apple Developer account**, and `security find-identity -v -p codesigning` returned **0 valid identities**. Do not assume a paid account is necessary merely to make this local app work.

## Workspace and release state

- Repository: `/Users/emi-mbp/snapx-reimagined-main`
- GitHub: `https://github.com/emiliauh/snapx-reimagined`
- Branch: `main`
- Last committed/pushed SHA: `2c77cd688ff345c5cf5699959924f85da15b8327`
- Published release: `v0.5.0-alpha.5`, with an ad-hoc signed ARM64 DMG and checksum.
- **All permission-setup changes described below are uncommitted. Preserve them.** Run `git status` to inventory them.
- `/Applications/SnapX.app` is the new native alpha.6 permission test build, launched through the app UI/Launch Services.
- `Output/SnapX.app` contains the same native executable and Info.plist as the installed copy at the last comparison.
- `Output/SnapX-0.5.0-alpha.6-macOS-arm64.dmg` exists (about 94 MiB); image/mount/signature checks passed. It predates the latest signing-script changes and **has not been published**.
- Previous installed alpha.5 app backup: `/private/tmp/SnapX-installed-alpha5-backup.app`.
- Earlier test app copies were moved to `/Users/emi-mbp/.Trash/SnapX-uninstall-20260911-201220`. Do not restore or delete them casually.
- User settings, captures, and login-item choice were preserved. The user enabled launch at login themselves; do not change it as part of permission debugging.

## Confirmed findings — distinguish these from hypotheses

### 1. Original application omitted explicit permission requests

Alpha.5's capture path launched `/usr/sbin/screencapture` and only emitted an IOException telling the user to check Screen Recording permission when it failed. There was no `CGRequestScreenCaptureAccess` call. The new source adds explicit requests and capture guards.

### 2. Current native Screen Recording request reaches macOS

Sol reviewed the installed application's TCC logs. At `2026-09-11 20:27:59.564 -0500`, PID 51519 at `/Applications/SnapX.app/Contents/MacOS/snapx-ui` made a **non-preflight** `kTCCServiceScreenCapture` request. Thus the request is not merely an inert UI button or an uncalled API.

TCC rejected the stored code requirement:

- Stored old requirement: `cdhash H"26f1b8967b0f2dcabbd7ac7e175dbc5ebeb72de8"`
- Current installed requirement: `cdhash H"83a2477351c329086a5fde98a661ebaf39decc16"`
- Log text: `Failed to match existing code requirement for subject com.emiliauh.snapx and service kTCCServiceScreenCapture`.

`codesign -d -r- /Applications/SnapX.app` confirmed a CDHash-only ad-hoc designated requirement. Changing a binary changes this identity. Stable certificate signing helps with updates, but it is **not yet a complete explanation or solution for the current reset/regrant failure**.

### 3. Settings says enabled while the current app is denied

A live System Settings accessibility snapshot showed:

- Screen & System Audio Recording → `SnapX` → switch **on**.

At the same time, installed SnapX's native preflight reported denied, and its setup showed Screen access incomplete. This is a real discrepancy between the displayed permission entry and the current app's effective authorization; do not “fix” it by marking the UI complete without real access.

### 4. User attempted recovery and it still failed

The user was asked to remove the old SnapX Screen Recording entry, click Grant screen access again, approve it, and reopen SnapX if requested. They reported: **“It still stays incomplete.”**

Fresh TCC evidence after that attempt still showed the old `26f1…` stored requirement versus current `83a…`:

- Two ScreenCapture Modify events around `20:32:00` / `20:32:02`.
- Mismatch persisted at `20:32:33` and `20:33:53`.
- Most recently observed running process: PID 51930, started `20:33:52`, executable `/Applications/SnapX.app/Contents/MacOS/snapx-ui`.

Do not tell the user to repeat the same reset blindly. Determine why the stored identity remains the old one.

### 5. Critical next lead: old installer is still mounted and registered

Immediately before handoff, Launch Services still listed these copies under the same bundle ID `com.emiliauh.snapx`:

| Registered path | Cached trusted signature |
| --- | --- |
| `/Volumes/SnapX Installer/SnapX.app` | old alpha.5 `26f1b896…` |
| `/Applications/SnapX.app` | current `83a24773…` |
| `/Users/emi-mbp/snapx-reimagined-main/Output/SnapX.app` | cached `2032893c…` |

All report bundle version `0.5.0` because the bundle script strips the prerelease suffix for both version fields. That may worsen ambiguity and deserves review.

`hdiutil info` confirmed an old mounted installer backed by:

`/Users/emi-mbp/Downloads/SnapX-0.5.0-alpha.5-macOS-arm64.dmg`

at `/Volumes/SnapX Installer`. It also listed the local Output alpha.5 disk image; inspect full current mount information before acting.

**Hypothesis, not yet proven:** removing/re-adding “SnapX” via settings resolves the old mounted copy or cached Launch Services identity, recreating the obsolete code requirement.

**Not executed yet:** unregister/eject the old SnapX installer, unregister the development copy, force-register the actual `/Applications/SnapX.app`, then verify that a fresh user-approved entry targets the installed copy and stores its current requirement. Do not disturb other mounted installers (Claude was also mounted).

The installed and Output executables were byte-identical despite that cached Output registration:

`SHA256 f156296b291201c23d48e05dc071baec2fdddd8e7960d7e43a1b3fead7d95a6a`

Their Info.plists also matched. Sol confirmed it did not alter either app; the Output registration may simply be stale.

### 6. Missing Microphone entry is partly explained by current UI sequencing

The installed native app returns `Microphone = NotDetermined` successfully. The UI disables **Grant microphone access** until Screen Recording is authorized. Therefore the microphone request has not run during this failed setup, and macOS has no reason to list SnapX there yet.

Review whether the sequence can deadlock unnecessarily. Keep the user's sequential setup intent, but ensure each requested permission can actually be registered and that Screen Recording restart/caching does not permanently prevent Microphone setup. The real AVFoundation approval flow remains unverified; status reads and synthetic callback tests are not equivalent to user approval.

## Current implementation

### Core

`SnapX.Core/Utils/Native/MacOSPermissions.cs` (new):

- `MacOSPermissionKind`: ScreenRecording, Microphone.
- `MacOSPermissionStatus`: Unavailable, NotDetermined, Restricted, Denied, Authorized.
- `CGPreflightScreenCaptureAccess` and `CGRequestScreenCaptureAccess` via source-generated P/Invoke with one-byte C bool marshalling.
- Screen preflight cannot distinguish not-yet-requested from denied; false maps to Denied.
- AVFoundation `AVCaptureDevice.authorizationStatusForMediaType:` using `AVMediaTypeAudio`.
- `requestAccessForMediaType:completionHandler:` with a NativeAOT-safe unmanaged Objective-C block, retained state, BOOL callback, and cleanup.
- Typed `MacOSPermissionException : UnauthorizedAccessException`, including kind and settings URL.
- Settings URLs:
  - `x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture`
  - `x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone`
- Native callback self-test invokes both BOOL outcomes without changing permissions.

Guards added to:

- `SnapX.Core/Capture/CaptureBase.cs`: before opening a selector/executing capture.
- `SnapX.Core/SharpCapture/macOS/macOSCapture.cs`: before launching screencapture.
- `SnapX.Core/Media/ScreenRecordManager.cs`: before recording; microphone checked only when FFmpeg audio source is selected.

### Avalonia

`SnapX.Avalonia/Views/Settings/Views/MacOSPermissionsView.cs` (new):

- Permission descriptions, status labels, Grant buttons, settings links, Check permissions again.
- Screen request runs synchronously on the UI thread; microphone awaits the native callback.
- Opens matching settings if the request does not result in Authorized.
- Rechecks on window activation and explicit refresh.
- Microphone request disabled while screen preflight is false.
- Reports completion only when both statuses are Authorized.

`SnapX.Avalonia/Utils/MacOSPermissionSetup.cs` (new):

- First-launch FAContentDialog with Finish setup disabled until complete.
- Rechecks both native statuses before saving completion.
- Quit SnapX leaves setup incomplete and shuts down on first launch.
- After completion, existing optional launch-at-login prompt runs.
- `MacOSPermissionSetupDismissed` in ApplicationConfig actually represents completed setup, despite its name.

`App.axaml.cs`:

- Invokes first-launch setup after showing MainWindow.
- Routes typed permission errors to the setup dialog instead of generic exception UI.

`SettingsCategoryView.axaml`: includes the permissions panel in Application settings.

Potential review points (not confirmed bugs): dialog reentrancy, first launch with SilentRun/CLI capture, asynchronous settings return after native restart-required grants, user cancel/deny/retry, and whether mandatory microphone setup is the intended interpretation of “all permissions.” Do not invent other permission requirements: Carbon hotkeys do not need Accessibility/Input Monitoring, and SnapX uses custom toast notifications. Camera access has not been established as necessary for the implemented screen capture workflow.

### Packaging changes in progress

Sol was adding stable signing support when this handoff was requested. Inspect final files; do not assume all paths have been validated:

- `packaging/macos-bundle.sh`: accepts `SNAPX_CODESIGN_IDENTITY`, requires explicit `SNAPX_ALLOW_ADHOC=1` for test builds, signs native nested code and bundle appropriately.
- `packaging/macos-dmg.sh`: optional signing/notarization with `SNAPX_NOTARY_PROFILE`.
- `.github/workflows/build.yml`: explicit ad-hoc mode for CI development artifacts.
- Packaging/docs/README examples updated accordingly.

No certificate was created or imported. No real signing/notarization path was exercised. Do not weaken the designated requirement to an identifier-only match or edit TCC databases to make the test pass. Do not frame lack of Developer ID as proof that a fresh ad-hoc local installation cannot request permissions.

## Validation already performed

- Debug GUI build: passed, zero errors.
- Native ARM64 GUI publish: passed; current app contains final Core callback cleanup.
- Native installed GUI: permission checklist appears; screen incomplete, mic NotDetermined; Finish disabled.
- Core focused `--macos-permission-probe`: 5 checks passed, including native status reads and exact unmanaged callback trampoline for false/true. No host permissions changed by probe.
- Broader fuzz run: 218,959 checks before an existing thumbnail-cache assertion failed. Do not claim the full suite passed.
- Alpha.6 DMG: created, verified, mounted read-only, app/signature/license layout verified, ejected by packaging script.
- **Not passed:** actual Screen Recording authorization of the current installed binary, actual microphone approval/list entry, Finish completion, and screenshot/recording after the permission fix.
- **Not published/committed:** alpha.6 permission changes.

Earlier capture/recording/multi-monitor work was tested before this installer/TCC scenario. See MACOS-VALIDATION.md for that history, but distinguish earlier development-launch success from current Finder-installed success.

## Logs and read-only diagnostics

Application log:

`/Users/emi-mbp/Library/Application Support/SnapX/Logs/2026/09/SnapX-1120260911.log`

This log contains many cursor-position entries and old unsupported Print Screen shortcut errors. Use focused filtering. It did not provide the decisive permission failure; TCC unified logs did.

Build/package logs:

- `/tmp/snapx-dotnet/build-permissions.log`
- `/tmp/snapx-dotnet/publish-permissions.log`
- `/tmp/snapx-dotnet/bundle-permissions.log`
- `/tmp/snapx-dotnet/dmg-permissions.log`

Useful commands (read-only):

```sh
git status --short
codesign -d -r- /Applications/SnapX.app 2>&1
codesign --verify --deep --strict /Applications/SnapX.app
security find-identity -v -p codesigning
ps -axo pid,lstart,command | rg '[s]napx-ui'
hdiutil info
/usr/bin/log show --last 10m --style compact \
  --predicate '(process == "tccd") AND (eventMessage CONTAINS[c] "snapx" OR eventMessage CONTAINS[c] "ScreenCapture")'
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -dump
```

Filter Launch Services records by exact identifier `com.emiliauh.snapx`; avoid dumping unrelated personal data. Never read/share uploader tokens or credentials. A diagnostic file accidentally named `:-` from a codesign command was moved to `/tmp/snapx-codesign-requirement-diagnostic.txt`; it is not a source file.

## Build environment

macOS 27.0, Apple M1 Pro ARM64. Temporary .NET 10 SDK:

```sh
export PATH=/tmp/snapx-dotnet/sdk:$PATH
export DOTNET_ROOT=/tmp/snapx-dotnet/sdk
export DOTNET_CLI_HOME=/tmp/snapx-dotnet/home
export NUGET_PACKAGES=/tmp/snapx-dotnet/packages
export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build SnapX.Avalonia/SnapX.Avalonia.csproj --no-restore \
  -p:DisableGitVersionTask=true -m:1

dotnet run --project tests/SnapX.Core.Fuzz -p:DisableGitVersionTask=true \
  -- --macos-permission-probe

dotnet publish SnapX.Avalonia/SnapX.Avalonia.csproj -c Release -r osx-arm64 \
  --no-restore -p:DisableGitVersionTask=true -p:Version=0.5.0-alpha.6 \
  -o Output/snapx-ui -m:1
```

Native builds can stall silently under the sandbox; prior builds needed the normal tool escalation for native macOS tools. `DisableGitVersionTask=true` avoids a GitVersion mainline-history bug and matches CI's fallback. Existing AOT/trimming warnings remain.

Do not run the top-level build tool casually: it cleans Output, including bundled FFmpeg/license artifacts. Bundler requires a **new** .app path and DMG builder refuses existing output. Preserve final binaries while testing. Bundled FFmpeg plus `FFmpeg-LICENSE.txt` and `FFmpeg-PROVENANCE.txt` are in `Output/snapx-ui`; licensing documents are copied into bundle Resources/Licenses.

## Recommended next investigation, in order

1. Refresh live process, TCC, signature, and mount evidence. The user may have changed settings after this snapshot.
2. Resolve the duplicate old DMG/Launch Services lead. Eject only the old SnapX installer, unregister obsolete SnapX bundle copies, register the exact installed copy, and ensure any user re-add targets `/Applications/SnapX.app` explicitly. This was **not yet attempted**. Do not change permissions on the user's behalf without appropriate authorization.
3. Trace the next request and user approval in TCC logs. Confirm stored/current code requirements actually match, rather than trusting the toggle label. If still stale, investigate the supported app-specific recovery mechanism and explain the exact scope before any permission reset; never reset all apps or modify the database directly.
4. If screen authorization succeeds, verify effective status after restart and whether the UI advances. Then execute the real microphone request and confirm SnapX is listed there. Diagnose that path independently if necessary.
5. If permission is truly effective but Core Graphics preflight remains false, investigate macOS 27 behavior with supported capture APIs and actual capture results. Do not simply bypass permission checks or mark setup complete.
6. Review packaging identity/versioning so development duplicates and rebuilt binaries cannot silently masquerade as the approved installed app. Stable signing remains a distribution improvement, not a substitute for the local failure diagnosis.
7. Test from the installed app, not only a terminal-launched probe: native request, deny/retry, settings link, restart/resume, completion, actual screenshot and short recording. Preserve user settings and captures. The current AfterCapture settings are CopyImageToClipboard + SaveImageToFile, not upload; recheck before any capture test and avoid transmitting screen contents.
8. Only after runtime success, rebuild/reverify the final DMG and commit/push/release as appropriate to the ongoing user request. If using ad-hoc signing, each rebuild may invalidate the just-approved identity: test the actual final artifact and disclose limitations honestly.

## Collaboration and safety boundaries

- The user authorizes fixing/building/testing SnapX and previously authorized committing/pushing and putting the DMG in GitHub Releases. No alpha.6 release has been created.
- Native UI automation is through `mcp__cua_repl`; do not use AppleScript/JXA/CGEvent to drive UI. On a resumed computer-use context, restore tool documentation first.
- Ask the user to perform system approval toggles. Do not silently grant access, reset TCC, disable SIP/Gatekeeper, or manufacture weaker signing requirements.
- Preserve personal settings, screenshots, login-item choice, and other mounted apps. No credentials should enter source, logs shared externally, or release assets.
- This handoff records a failed runtime test. Be candid about what remains unverified and do not announce the permissions problem fixed based only on compilation or synthetic checks.
