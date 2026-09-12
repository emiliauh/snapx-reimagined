#!/bin/bash
# Usage: bash packaging/macos-bundle.sh [publish-directory] [output.app]
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo 'macOS is required to assemble and sign the app bundle.' >&2
    exit 1
fi

published_dir="${1:-Output/snapx-ui}"
bundle="${2:-SnapX.app}"
script_dir="$(cd "$(dirname "$0")" && pwd)"
snapx_license="$script_dir/../LICENSE.md"
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

# Strip prerelease/commit information, which CFBundleShortVersionString rejects.
version="$("$published_dir/snapx-ui" --version)"
if [[ "$version" =~ ^([0-9]+\.[0-9]+\.[0-9]+) ]]; then
    version="${BASH_REMATCH[1]}"
else
    echo "Executable did not report a semantic version: $version" >&2
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
cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>com.emiliauh.snapx</string>
  <key>CFBundleName</key><string>SnapX</string>
  <key>CFBundleDisplayName</key><string>SnapX</string>
  <key>CFBundleExecutable</key><string>snapx-ui</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><false/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>SnapX uses the microphone when you choose to record audio.</string>
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
    iconutil -c icns "$icon_tmp/SnapX.iconset" -o "$bundle/Contents/Resources/SnapX.icns"
    /usr/libexec/PlistBuddy -c 'Add :CFBundleIconFile string SnapX.icns' "$bundle/Contents/Info.plist"
fi

plutil -lint "$bundle/Contents/Info.plist"
codesign --force --deep -s - "$bundle"
codesign --verify --deep --strict --verbose=2 "$bundle"
test -f "$bundle/Contents/Resources/Licenses/LICENSE.md"
if [[ -f "$bundle/Contents/MacOS/ffmpeg" ]]; then
    for license_name in "${ffmpeg_license_files[@]}"; do
        test -f "$bundle/Contents/Resources/Licenses/$license_name"
    done
fi
echo "Created ad-hoc signed app: $bundle"
