# macOS packaging

Build an ad-hoc signed app bundle from a macOS Native AOT publish directory:

```sh
bash packaging/macos-bundle.sh Output/snapx-ui Output/SnapX.app
```

Create a compressed drag-to-Applications disk image:

```sh
bash packaging/macos-dmg.sh Output/SnapX.app
```

The default filename includes the app version and executable architectures. An
explicit output path can be supplied as the second argument:

```sh
bash packaging/macos-dmg.sh Output/SnapX.app Output/SnapX-release.dmg
```

The DMG script refuses to overwrite an existing file. It verifies the input app
signature, creates the compressed image, verifies the image checksum, mounts it
read-only, checks its app, Applications shortcut, and installation instructions,
then verifies the copied app signature before ejecting it.
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
`~/Applications` is also supported. On the first launch of an installed copy,
SnapX offers **Enable launch at login** and **Not now**, with **Not now** as the
default. The Application settings page later provides Enable, Disable, status,
and Open Login Items settings controls. macOS remains the source of truth when
approval is pending or the user changes the login item externally.

The disk image and bundle scripts only package files. They do not launch SnapX,
register a login item, or make any startup change.
