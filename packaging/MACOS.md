# macOS packaging

Build a distributable app bundle from a macOS Native AOT publish directory by
supplying the SHA-1 hash of a Developer ID Application certificate:

```sh
export SNAPX_CODESIGN_IDENTITY=0123456789ABCDEF0123456789ABCDEF01234567
bash packaging/macos-bundle.sh Output/snapx-ui Output/SnapX.app
```

Find available certificate identities with
`security find-identity -v -p codesigning`. The bundle script signs nested
Mach-O files first, then seals the app with a secure timestamp and hardened
runtime. Its entitlements allow the main app and bundled FFmpeg process to use
microphone input after the user approves it. Keeping the same Developer ID
identity across releases gives macOS a stable designated requirement, so
privacy grants continue to match updated versions of SnapX.

For repeated local development without an Apple Developer account, create one
long-lived self-signed **Code Signing** certificate in Keychain Access and use
its SHA-1 identity in local mode:

```sh
export SNAPX_CODESIGN_IDENTITY=0123456789ABCDEF0123456789ABCDEF01234567
export SNAPX_LOCAL_SIGNING=1
bash packaging/macos-bundle.sh Output/snapx-ui "Output/SnapX Local.app"
bash packaging/macos-dmg.sh "Output/SnapX Local.app"
```

Local-certificate mode uses the separate `com.emiliauh.snapx.local` bundle ID
and `SnapX Local` display name. It omits Apple timestamping and notarization and
is only for this Mac. Reusing the same certificate gives rebuilt local bundles
a certificate-anchored identity; verify permission retention on the target
macOS version before relying on it. This does not make the app suitable for
public distribution.

For a local or CI development artifact without a certificate, ad-hoc signing
must be requested explicitly:

```sh
SNAPX_ALLOW_ADHOC=1 \
  bash packaging/macos-bundle.sh Output/snapx-ui "Output/SnapX Local.app"
```

Ad-hoc/local artifacts are kept separate from production by bundle ID and app
name. Ad-hoc signing is not suitable for distribution: its designated
requirement is the executable's CDHash, which changes on every rebuild. Use one
immutable artifact for permission testing; a rebuilt ad-hoc copy needs a fresh
user approval for its new identity.

Create a compressed drag-to-Applications disk image:

```sh
bash packaging/macos-dmg.sh Output/SnapX.app
```

To sign the disk image and notarize both the app and final DMG, first store
notary credentials in the login keychain, then provide the profile name:

```sh
xcrun notarytool store-credentials snapx-notary \
  --apple-id you@example.com --team-id TEAMID1234

export SNAPX_CODESIGN_IDENTITY=0123456789ABCDEF0123456789ABCDEF01234567
export SNAPX_NOTARY_PROFILE=snapx-notary
bash packaging/macos-dmg.sh Output/SnapX.app
```

`SNAPX_NOTARY_PROFILE` names a keychain profile; it is not an Apple password.
The script submits a ZIP of the app and staples its ticket before copying the
app into the image. It then signs, submits, staples, and verifies the final DMG.
Notarization rejects ad-hoc signatures.

The public version stays three-part (for example `0.5.0`), while
`CFBundleVersion` encodes the prerelease/build iteration and `SnapXFullVersion`
retains the complete version (for example `0.5.0-alpha.6`). The default DMG
filename uses that complete version plus the executable architectures. An
explicit output path can be supplied as the second argument:

```sh
bash packaging/macos-dmg.sh Output/SnapX.app Output/SnapX-release.dmg
```

The DMG script refuses to overwrite an existing file. It verifies the input app
signature, creates the compressed image, verifies the image checksum, mounts it
read-only, checks its app, Applications shortcut, and installation instructions,
then verifies the copied app signature before ejecting it. Without
`SNAPX_NOTARY_PROFILE`, it performs no notarization or notary-credential
operation. Certificate signing still reads the selected keychain identity, and
Developer ID signing requests an Apple timestamp.
On macOS versions that briefly retain a disk-image device lock after creation,
verification and read-only attachment retry that exact transient error up to
three times with a two-second delay; other errors fail immediately.

The bundle always copies the repository's `LICENSE.md` to
`Contents/Resources/Licenses`. When the publish directory contains an `ffmpeg`
executable, it must also contain `FFmpeg-LICENSE.txt` and
`FFmpeg-PROVENANCE.txt`; the bundle script moves those documents beside the
SnapX license. The DMG builder checks the same license layout before and after
mounting the image.

The Applications shortcut installs to `/Applications`; copying the app to
`~/Applications` is also supported. Development images contain `SnapX
Local.app`, so they cannot overwrite a production `SnapX.app` by name. On first
launch, a permission checklist requests Screen Recording and Microphone access
one at a time and links to their System Settings panels. Finish setup stays
disabled until macOS confirms both permissions; quitting resumes setup next
time. The app may need to be reopened after changing Screen Recording access.
After permission setup, an installed copy of SnapX offers **Enable launch at
login** and **Not now**, with **Not now** as the default. The Application
settings page later provides Enable, Disable, status, and Open Login Items
settings controls. macOS remains the source of truth when approval is pending
or the user changes the login item externally.

The disk image and bundle scripts only package files. They do not launch SnapX,
register a login item, or make any startup change.
