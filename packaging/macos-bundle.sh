#!/bin/bash
# Distribution: SNAPX_CODESIGN_IDENTITY=<developer-id-sha1> bash packaging/macos-bundle.sh [publish-directory] [output.app]
# Stable local-only signing: SNAPX_CODESIGN_IDENTITY=<local-certificate-sha1> SNAPX_LOCAL_SIGNING=1 bash packaging/macos-bundle.sh ...
# Disposable local/CI fallback: SNAPX_ALLOW_ADHOC=1 bash packaging/macos-bundle.sh ...
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo 'macOS is required to assemble and sign the app bundle.' >&2
    exit 1
fi

published_dir="${1:-Output/snapx-ui}"
bundle="${2:-SnapX.app}"
script_dir="$(cd "$(dirname "$0")" && pwd)"
snapx_license="$script_dir/../LICENSE.md"
codesign_identity="${SNAPX_CODESIGN_IDENTITY:-}"
allow_adhoc="${SNAPX_ALLOW_ADHOC:-}"
local_signing="${SNAPX_LOCAL_SIGNING:-}"
entitlements="$script_dir/macos.entitlements.plist"

if [[ -z "$codesign_identity" ]]; then
    if [[ "$allow_adhoc" != '1' ]]; then
        cat >&2 <<'EOF'
No macOS signing identity was supplied.

For a distributable build, set SNAPX_CODESIGN_IDENTITY to the SHA-1 hash of a
Developer ID Application certificate. Run `security find-identity -v -p codesigning`
to list available identities.

For a local or CI development artifact only, explicitly set SNAPX_ALLOW_ADHOC=1.
Ad-hoc signatures change whenever the executable changes, so macOS privacy
permissions do not carry across rebuilt versions.
EOF
        exit 1
    fi
    codesign_identity='-'
    signing_mode='adhoc'
    signing_description='ad-hoc development'
else
    if [[ "$codesign_identity" == '-' ]]; then
        echo 'Use SNAPX_ALLOW_ADHOC=1 instead of SNAPX_CODESIGN_IDENTITY=-.' >&2
        exit 1
    fi
    if [[ "$local_signing" == '1' ]]; then
        signing_mode='local-certificate'
        signing_description='local certificate development'
    else
        signing_mode='developer-id'
        signing_description='Developer ID'
    fi
fi

if [[ "$signing_mode" == 'developer-id' ]]; then
    bundle_identifier="${SNAPX_BUNDLE_IDENTIFIER:-com.emiliauh.snapx}"
    bundle_name="${SNAPX_BUNDLE_NAME:-SnapX}"
else
    # Development copies must not impersonate the production bundle in Launch
    # Services or TCC. Their signing identity is still expected to remain stable
    # when a local certificate is used.
    bundle_identifier="${SNAPX_BUNDLE_IDENTIFIER:-com.emiliauh.snapx.local}"
    bundle_name="${SNAPX_BUNDLE_NAME:-SnapX}"
fi
if [[ ! "$bundle_identifier" =~ ^[A-Za-z0-9]+([.-][A-Za-z0-9]+)+$ ]]; then
    echo "Invalid bundle identifier: $bundle_identifier" >&2
    exit 1
fi
if [[ ! "$bundle_name" =~ ^[A-Za-z0-9._[:space:]-]+$ ]]; then
    echo "Invalid bundle name: $bundle_name" >&2
    exit 1
fi
if [[ ! -x "$published_dir/snapx-ui" ]]; then
    echo "Published executable is missing or not executable: $published_dir/snapx-ui" >&2
    exit 1
fi
if [[ ! -f "$snapx_license" ]]; then
    echo "SnapX license is missing: $snapx_license" >&2
    exit 1
fi
if [[ "$bundle" != *.app || -e "$bundle" ]]; then
    echo "Choose a new output path ending in .app: $bundle" >&2
    exit 1
fi

# CFBundleShortVersionString is the public three-part version. Keep a distinct,
# numeric CFBundleVersion so prerelease builds do not all register as the same
# application version. SNAPX_BUNDLE_VERSION can supply a CI build number.
full_version="$("$published_dir/snapx-ui" --version)"
if [[ "$full_version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+) ]]; then
    version_major="${BASH_REMATCH[1]}"
    version_minor="${BASH_REMATCH[2]}"
    version_patch="${BASH_REMATCH[3]}"
    short_version="$version_major.$version_minor.$version_patch"
else
    echo "Executable did not report a semantic version: $full_version" >&2
    exit 1
fi
bundle_version="${SNAPX_BUNDLE_VERSION:-}"
if [[ -z "$bundle_version" ]]; then
    stage=9
    sequence=0
    if [[ "$full_version" =~ -([A-Za-z]+)[.-]([0-9]+) ]]; then
        case "${BASH_REMATCH[1]}" in
            [Aa][Ll][Pp][Hh][Aa]) stage=1 ;;
            [Bb][Ee][Tt][Aa]) stage=2 ;;
            [Rr][Cc]) stage=3 ;;
            *) stage=4 ;;
        esac
        sequence="${BASH_REMATCH[2]}"
    fi
    if ((10#$sequence > 99999)); then
        echo "Prerelease sequence is too large for CFBundleVersion encoding: $sequence" >&2
        exit 1
    fi
    encoded_patch=$((10#$version_patch * 1000000 + stage * 100000 + 10#$sequence))
    bundle_version="$version_major.$version_minor.$encoded_patch"
fi
if [[ ! "$bundle_version" =~ ^[0-9]+(\.[0-9]+){0,2}$ ]]; then
    echo "CFBundleVersion must contain one to three numeric components: $bundle_version" >&2
    exit 1
fi
if [[ ! "$full_version" =~ ^[A-Za-z0-9.+-]+$ ]]; then
    echo "Executable version contains unsupported characters: $full_version" >&2
    exit 1
fi

ffmpeg_license_files=(FFmpeg-LICENSE.txt FFmpeg-PROVENANCE.txt)
if [[ -f "$published_dir/ffmpeg" ]]; then
    for license_name in "${ffmpeg_license_files[@]}"; do
        if [[ ! -f "$published_dir/$license_name" ]]; then
            echo "Bundled FFmpeg requires its license metadata in the publish directory: $published_dir/$license_name" >&2
            exit 1
        fi
    done
fi

mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources/Licenses"
cp -R "$published_dir/." "$bundle/Contents/MacOS/"
install -m 0644 "$snapx_license" "$bundle/Contents/Resources/Licenses/LICENSE.md"
if [[ -f "$published_dir/ffmpeg" ]]; then
    for license_name in "${ffmpeg_license_files[@]}"; do
        rm -f "$bundle/Contents/MacOS/$license_name"
        install -m 0644 "$published_dir/$license_name" "$bundle/Contents/Resources/Licenses/$license_name"
    done
fi

# Debug symbols are published separately and must not become nested executable
# code or add hundreds of megabytes to the application bundle.
find "$bundle/Contents/MacOS" -depth -type d -name '*.dSYM' -exec rm -rf {} +
find "$bundle/Contents/MacOS" -type f -name '*.pdb' -delete

# NuGet native assets commonly ship as universal binaries even when the app's
# NativeAOT executable targets one architecture. Thin those nested Mach-O files
# to the executable architecture and remove local/debug symbols before signing.
# This keeps all features while avoiding roughly 20 MiB of unused release data.
main_arches="$(lipo -archs "$bundle/Contents/MacOS/snapx-ui")"
if [[ "$main_arches" == 'arm64' || "$main_arches" == 'x86_64' ]]; then
    while IFS= read -r -d '' candidate; do
        if [[ "$candidate" == "$bundle/Contents/MacOS/snapx-ui" ]]; then
            continue
        fi
        if candidate_arches="$(lipo -archs "$candidate" 2>/dev/null)"; then
            if [[ " $candidate_arches " == *" $main_arches "* && "$candidate_arches" == *' '* ]]; then
                temporary="$candidate.snapx-thin"
                original_mode="$(stat -f '%Lp' "$candidate")"
                lipo "$candidate" -thin "$main_arches" -output "$temporary"
                chmod "$original_mode" "$temporary"
                mv "$temporary" "$candidate"
            fi
            strip -S -x "$candidate" 2>/dev/null || true
        fi
    done < <(find "$bundle/Contents/MacOS" -depth -type f -print0)
fi

# Shared/downloaded workspaces can attach quarantine or stale detached-signature
# attributes to resource files. A newly assembled bundle must be signed from a
# clean resource tree.
xattr -cr "$bundle"

cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>$bundle_identifier</string>
  <key>CFBundleName</key><string>$bundle_name</string>
  <key>CFBundleDisplayName</key><string>$bundle_name</string>
  <key>CFBundleExecutable</key><string>snapx-ui</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$bundle_version</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>SnapXFullVersion</key><string>$full_version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><false/>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF

icon_source="$script_dir/usr/share/icons/hicolor/256x256/apps/io.emiliauh.SnapXL.SnapX.png"
if [[ -f "$icon_source" ]]; then
    icon_tmp="$(mktemp -d)"
    trap 'rm -rf "$icon_tmp"' EXIT
    mkdir "$icon_tmp/SnapX.iconset"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$icon_source" --out "$icon_tmp/SnapX.iconset/icon_${size}x${size}.png" >/dev/null
        retina_size=$((size * 2))
        sips -z "$retina_size" "$retina_size" "$icon_source" --out "$icon_tmp/SnapX.iconset/icon_${size}x${size}@2x.png" >/dev/null
    done
    if ! iconutil -c icns "$icon_tmp/SnapX.iconset" -o "$bundle/Contents/Resources/SnapX.icns" 2>/dev/null; then
        # Some macOS releases reject an otherwise conventional iconset. SIPS can
        # still create a valid ICNS from the source image on those hosts.
        sips -s format icns "$icon_source" --out "$bundle/Contents/Resources/SnapX.icns" >/dev/null
    fi
    /usr/libexec/PlistBuddy -c 'Add :CFBundleIconFile string SnapX.icns' "$bundle/Contents/Info.plist"
fi

plutil -lint "$bundle/Contents/Info.plist"

codesign_args=(--force --sign "$codesign_identity")
if [[ "$signing_mode" == 'developer-id' ]]; then
    # Developer ID distribution requires a secure timestamp and hardened runtime.
    codesign_args+=(--timestamp --options runtime)
    if [[ ! -f "$entitlements" ]]; then
        echo "macOS hardened-runtime entitlements are missing: $entitlements" >&2
        exit 1
    fi
fi

# Sign runtime nested code before sealing the outer bundle. The main executable
# is signed by the final bundle operation, and debug-symbol Mach-O files have
# already been excluded.
while IFS= read -r -d '' candidate; do
    if [[ "$candidate" == "$bundle/Contents/MacOS/snapx-ui" ]]; then
        continue
    fi
    # Every regular file below Contents/MacOS is treated as nested code by the
    # bundle verifier, including the browser-host JSON manifests. codesign uses
    # an extended signature for those non-Mach-O files.
    if [[ "$signing_mode" == 'developer-id' && "$(basename "$candidate")" == 'ffmpeg' ]]; then
        codesign "${codesign_args[@]}" --entitlements "$entitlements" "$candidate"
    else
        codesign "${codesign_args[@]}" "$candidate"
    fi
done < <(find "$bundle/Contents/MacOS" -depth -type f -print0)

if [[ "$signing_mode" == 'developer-id' ]]; then
    codesign "${codesign_args[@]}" --entitlements "$entitlements" "$bundle"
else
    codesign "${codesign_args[@]}" "$bundle"
fi
codesign --verify --deep --strict --verbose=2 "$bundle"
test -f "$bundle/Contents/Resources/Licenses/LICENSE.md"
if [[ -f "$bundle/Contents/MacOS/ffmpeg" ]]; then
    for license_name in "${ffmpeg_license_files[@]}"; do
        test -f "$bundle/Contents/Resources/Licenses/$license_name"
    done
fi
echo "Created $signing_description signed app: $bundle"
