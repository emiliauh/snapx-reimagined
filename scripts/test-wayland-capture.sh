#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp/snapx-wayland-runtime}"
MOCK_BIN="$RUNTIME_DIR/mock-bin"
WESTON_LOG="$RUNTIME_DIR/weston.log"
SNAPX_BIN="$ROOT/SnapX.Avalonia/bin/Debug/net10.0/linux-x64/snapx-ui"
SNAPX_LOG="$RUNTIME_DIR/snapx-ui.log"

mkdir -p "$MOCK_BIN" "$RUNTIME_DIR"
chmod 700 "$RUNTIME_DIR"

cat >"$MOCK_BIN/hyprctl" <<'EOF'
#!/usr/bin/env bash
case "${1:-}" in
  monitors)
    if [[ "${2:-}" == "-j" ]]; then
      cat <<'JSON'
[
  {
    "id": 0,
    "name": "WL-1",
    "x": 0,
    "y": 0,
    "width": 1920,
    "height": 1080,
    "scale": 1,
    "transform": 0,
    "focused": true,
    "reserved": [26, 53, 0, 0]
  }
]
JSON
      exit 0
    fi
    ;;
  layers)
    cat <<'TXT'
Monitor WL-1:
	Layer level 2 (top layer):
		Layer 7b1c: xywh: 0 0 1920 26
TXT
    exit 0
    ;;
  dispatch)
    exit 0
    ;;
  version)
    echo "Hyprland 0.39.1 mock"
    exit 0
    ;;
  cursorpos)
    if [[ "${2:-}" == "-j" ]]; then
      echo '{"x":960,"y":600}'
      exit 0
    fi
    echo "960 600"
    exit 0
    ;;
esac
echo "mock hyprctl: unsupported args: $*" >&2
exit 1
EOF
chmod +x "$MOCK_BIN/hyprctl"

export XDG_RUNTIME_DIR="$RUNTIME_DIR"
export XDG_SESSION_TYPE=wayland
export PATH="$MOCK_BIN:$PATH"

if ! pgrep -f "sway" >/dev/null 2>&1; then
  sway --unsupported-gpu >"$WESTON_LOG" 2>&1 &
  sleep 3
fi

if [[ -z "${WAYLAND_DISPLAY:-}" ]]; then
  for socket in "$RUNTIME_DIR"/wayland-*; do
    if [[ -S "$socket" ]]; then
      export WAYLAND_DISPLAY="${socket##*/}"
      break
    fi
  done
fi

echo "WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-unset}"
echo "XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR"

if [[ ! -x "$SNAPX_BIN" ]]; then
  echo "snapx-ui binary not found at $SNAPX_BIN" >&2
  exit 1
fi

: >"$SNAPX_LOG"
"$SNAPX_BIN" -RectangleRegion >>"$SNAPX_LOG" 2>&1 &
SNAPX_PID=$!
echo "Started snapx-ui pid=$SNAPX_PID"

for _ in $(seq 1 30); do
  if rg -q "Live annotate toolbar initialized|Selector toolbar offset|Selector Ready" "$SNAPX_LOG"; then
    echo "Capture session started."
    rg "Live annotate toolbar initialized|Selector toolbar offset|Selector Ready|Hyprland overlay" "$SNAPX_LOG" || true
    exit 0
  fi
  if ! kill -0 "$SNAPX_PID" 2>/dev/null; then
    echo "snapx-ui exited early:" >&2
    cat "$SNAPX_LOG" >&2
    exit 1
  fi
  sleep 0.5
done

echo "Timed out waiting for capture session:" >&2
cat "$SNAPX_LOG" >&2
kill "$SNAPX_PID" 2>/dev/null || true
exit 1
