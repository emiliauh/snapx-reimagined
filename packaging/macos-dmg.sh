#!/bin/bash
# Usage: bash packaging/macos-dmg.sh [input.app] [output.dmg]
# Optional distribution environment:
#   SNAPX_CODESIGN_IDENTITY=<certificate-sha1>
#   SNAPX_NOTARY_PROFILE=<notarytool-keychain-profile>
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo 'macOS is required to create and verify the disk image.' >&2
    exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
app="${1:-Output/SnapX.app}"
codesign_identity="${SNAPX_CODESIGN_IDENTITY:-}"
notary_profile="${SNAPX_NOTARY_PROFILE:-}"
local_signing="${SNAPX_LOCAL_SIGNING:-}"

if [[ -n "$notary_profile" && -z "$codesign_identity" ]]; then
    echo 'SNAPX_NOTARY_PROFILE requires SNAPX_CODESIGN_IDENTITY so the disk image can be signed before notarization.' >&2
    exit 1
fi
if [[ "$local_signing" == '1' && -z "$codesign_identity" ]]; then
    echo 'SNAPX_LOCAL_SIGNING requires SNAPX_CODESIGN_IDENTITY.' >&2
    exit 1
fi
if [[ "$local_signing" == '1' && -n "$notary_profile" ]]; then
    echo 'A local signing certificate cannot be used for Apple notarization.' >&2
    exit 1
fi
if [[ "$codesign_identity" == '-' ]]; then
    echo 'Ad-hoc signing cannot be notarized or used for a distributable disk image.' >&2
    exit 1
fi

if [[ ! -d "$app" || "$app" != *.app ]]; then
    echo "Application bundle is missing or does not end in .app: $app" >&2
    exit 1
fi

info_plist="$app/Contents/Info.plist"
if ! plutil -lint "$info_plist" >/dev/null; then
    echo "Application bundle has no valid Info.plist: $info_plist" >&2
    exit 1
fi

bundle_name="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleName' "$info_plist")"
bundle_identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$info_plist")"
bundle_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$info_plist")"
full_version="$(/usr/libexec/PlistBuddy -c 'Print :SnapXFullVersion' "$info_plist" 2>/dev/null || printf '%s' "$bundle_version")"
bundle_executable="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$info_plist")"
executable="$app/Contents/MacOS/$bundle_executable"

if [[ ! "$bundle_identifier" =~ ^com\.emiliauh\.snapx([.-][A-Za-z0-9]+)*$ ||
      ! "$bundle_name" =~ ^[A-Za-z0-9._[:space:]-]+$ ]]; then
    echo "Expected a SnapX app bundle, but found: $bundle_identifier ($bundle_name)" >&2
    exit 1
fi
if [[ ! -x "$executable" ]]; then
    echo "Application executable is missing or not executable: $executable" >&2
    exit 1
fi
if [[ ! "$bundle_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
    echo "CFBundleShortVersionString is not a usable version: $bundle_version" >&2
    exit 1
fi
if [[ ! "$full_version" =~ ^[A-Za-z0-9.+-]+$ ]]; then
    echo "SnapXFullVersion contains unsupported characters: $full_version" >&2
    exit 1
fi
staged_app_name="$bundle_name.app"

architectures="$(lipo -archs "$executable")"
architecture_label="${architectures// /-}"
output="${2:-Output/SnapX-${full_version}-macOS-${architecture_label}.dmg}"
if [[ "$output" != *.dmg ]]; then
    echo "Output path must end in .dmg: $output" >&2
    exit 1
fi
if [[ -e "$output" ]]; then
    echo "Choose a new output path; a file already exists at: $output" >&2
    exit 1
fi

readme_source="$script_dir/macos-dmg-readme.txt"
if [[ ! -f "$readme_source" ]]; then
    echo "Installer instructions are missing: $readme_source" >&2
    exit 1
fi

run_hdiutil_with_transient_retry() {
    local attempt=1
    local command_output
    local command_status
    while true; do
        if command_output="$(hdiutil "$@" 2>&1)"; then
            if [[ -n "$command_output" ]]; then
                printf '%s\n' "$command_output"
            fi
            return 0
        else
            command_status=$?
        fi

        if [[ "$command_output" == *'Resource temporarily unavailable'* && "$attempt" -lt 3 ]]; then
            printf '%s\n' "$command_output" >&2
            printf 'Transient disk-image lock; retrying hdiutil (%d/3) in 2 seconds.\n' "$((attempt + 1))" >&2
            attempt=$((attempt + 1))
            sleep 2
            continue
        fi

        printf '%s\n' "$command_output" >&2
        return "$command_status"
    done
}

codesign --verify --deep --strict --verbose=2 "$app"
if [[ ! -f "$app/Contents/Resources/Licenses/LICENSE.md" ]]; then
    echo 'Application bundle does not contain the SnapX license.' >&2
    exit 1
fi
if [[ -f "$app/Contents/MacOS/ffmpeg" ]]; then
    for license_name in FFmpeg-LICENSE.txt FFmpeg-PROVENANCE.txt; do
        if [[ ! -f "$app/Contents/Resources/Licenses/$license_name" ]]; then
            echo "Application bundle contains FFmpeg without required license metadata: $license_name" >&2
            exit 1
        fi
    done
fi

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/snapx-dmg.XXXXXX")"
staging_dir="$work_dir/staging"
mount_dir="$work_dir/mount"
mounted=false
complete=false

cleanup() {
    if [[ "$mounted" == true ]]; then
        hdiutil detach "$mount_dir" >/dev/null 2>&1 || true
    fi
    if [[ "$complete" != true ]]; then
        rm -f "$output"
    fi
    rm -rf "$work_dir"
}
trap cleanup EXIT

if [[ -n "$notary_profile" ]]; then
    signing_details="$(codesign --display --verbose=4 "$app" 2>&1)"
    if ! grep -q '^Authority=Developer ID Application:' <<<"$signing_details" ||
       ! grep -q 'flags=.*runtime' <<<"$signing_details"; then
        echo 'Notarization requires an app signed with a Developer ID Application certificate.' >&2
        exit 1
    fi

    # Submit the app in a ZIP first so its ticket can be stapled into the copy
    # placed inside the DMG. The final signed DMG is submitted separately below.
    app_notary_zip="$work_dir/SnapX.app.zip"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$app_notary_zip"
    xcrun notarytool submit "$app_notary_zip" --keychain-profile "$notary_profile" --wait
    xcrun stapler staple "$app"
    xcrun stapler validate "$app"
    codesign --verify --deep --strict --verbose=2 "$app"
fi

mkdir -p "$staging_dir" "$mount_dir" "$(dirname "$output")"
ditto "$app" "$staging_dir/$staged_app_name"
ln -s /Applications "$staging_dir/Applications"
cp "$readme_source" "$staging_dir/Install SnapX.txt"

hdiutil create \
    -volname 'SnapX Installer' \
    -srcfolder "$staging_dir" \
    -format UDZO \
    -imagekey zlib-level=9 \
    "$output"

if [[ -n "$codesign_identity" ]]; then
    dmg_codesign_args=(--force --sign "$codesign_identity")
    if [[ "$local_signing" != '1' ]]; then
        dmg_codesign_args+=(--timestamp)
    fi
    codesign "${dmg_codesign_args[@]}" "$output"
    codesign --verify --strict --verbose=2 "$output"
fi

run_hdiutil_with_transient_retry verify "$output"

if [[ -n "$notary_profile" ]]; then
    xcrun notarytool submit "$output" --keychain-profile "$notary_profile" --wait
    xcrun stapler staple "$output"
    xcrun stapler validate "$output"
    codesign --verify --strict --verbose=2 "$output"
    run_hdiutil_with_transient_retry verify "$output"
fi

run_hdiutil_with_transient_retry attach "$output" -readonly -nobrowse -mountpoint "$mount_dir" >/dev/null
mounted=true

if [[ ! -d "$mount_dir/$staged_app_name" ]]; then
    echo "Created disk image does not contain $staged_app_name." >&2
    exit 1
fi
if [[ ! -L "$mount_dir/Applications" || "$(readlink "$mount_dir/Applications")" != '/Applications' ]]; then
    echo 'Created disk image does not contain the expected Applications symlink.' >&2
    exit 1
fi
if [[ ! -f "$mount_dir/Install SnapX.txt" ]]; then
    echo 'Created disk image does not contain its installation instructions.' >&2
    exit 1
fi
mounted_app="$mount_dir/$staged_app_name"
codesign --verify --deep --strict --verbose=2 "$mounted_app"
test -f "$mounted_app/Contents/Resources/Licenses/LICENSE.md"
if [[ -f "$mounted_app/Contents/MacOS/ffmpeg" ]]; then
    test -f "$mounted_app/Contents/Resources/Licenses/FFmpeg-LICENSE.txt"
    test -f "$mounted_app/Contents/Resources/Licenses/FFmpeg-PROVENANCE.txt"
fi

hdiutil detach "$mount_dir" >/dev/null
mounted=false
complete=true

if [[ -n "$notary_profile" ]]; then
    echo "Created, signed, notarized, stapled, and verified disk image: $output"
elif [[ -n "$codesign_identity" ]]; then
    if [[ "$local_signing" == '1' ]]; then
        echo "Created and verified local-certificate development disk image: $output"
    else
        echo "Created, signed, and verified disk image: $output"
    fi
else
    echo "Created and verified development disk image: $output"
fi
