#!/bin/bash
# Usage: bash packaging/macos-dmg.sh [input.app] [output.dmg]
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo 'macOS is required to create and verify the disk image.' >&2
    exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
app="${1:-Output/SnapX.app}"

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
bundle_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$info_plist")"
bundle_executable="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$info_plist")"
executable="$app/Contents/MacOS/$bundle_executable"

if [[ "$bundle_name" != 'SnapX' ]]; then
    echo "Expected a SnapX app bundle, but CFBundleName is: $bundle_name" >&2
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

architectures="$(lipo -archs "$executable")"
architecture_label="${architectures// /-}"
output="${2:-Output/SnapX-${bundle_version}-macOS-${architecture_label}.dmg}"
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

mkdir -p "$staging_dir" "$mount_dir" "$(dirname "$output")"
ditto "$app" "$staging_dir/SnapX.app"
ln -s /Applications "$staging_dir/Applications"
cp "$readme_source" "$staging_dir/Install SnapX.txt"

hdiutil create \
    -volname 'SnapX Installer' \
    -srcfolder "$staging_dir" \
    -format UDZO \
    -imagekey zlib-level=9 \
    "$output"

run_hdiutil_with_transient_retry verify "$output"
run_hdiutil_with_transient_retry attach "$output" -readonly -nobrowse -mountpoint "$mount_dir" >/dev/null
mounted=true

if [[ ! -d "$mount_dir/SnapX.app" ]]; then
    echo 'Created disk image does not contain SnapX.app.' >&2
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
codesign --verify --deep --strict --verbose=2 "$mount_dir/SnapX.app"
test -f "$mount_dir/SnapX.app/Contents/Resources/Licenses/LICENSE.md"
if [[ -f "$mount_dir/SnapX.app/Contents/MacOS/ffmpeg" ]]; then
    test -f "$mount_dir/SnapX.app/Contents/Resources/Licenses/FFmpeg-LICENSE.txt"
    test -f "$mount_dir/SnapX.app/Contents/Resources/Licenses/FFmpeg-PROVENANCE.txt"
fi

hdiutil detach "$mount_dir" >/dev/null
mounted=false
complete=true

echo "Created and verified read-only disk image: $output"
