#!/usr/bin/env bash
# Cloud Agent environment bootstrap for SnapX.
#
# SnapX is a .NET 10 Avalonia desktop app that publishes a Native AOT,
# single-file, self-contained "snapx-ui" executable. This script installs the
# toolchain and system libraries the Linux build and runtime need, then restores
# NuGet packages so a build can run offline afterwards.
#
# The script is idempotent: it can run repeatedly against a warm or partially
# prepared machine without re-doing completed work.
set -euo pipefail

DOTNET_CHANNEL_VERSION="10.0.100"
DOTNET_ROOT_DIR="${HOME}/.dotnet"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Installing system dependencies (apt)"
# clang + zlib + patchelf: required by the Native AOT publish/link step.
# xorg/xvfb/mesa-utils: X11 runtime + headless display for launching the GUI.
# libwayland-dev/wayland-protocols: Wayland client build + native picker helper.
# ffmpeg: X11 screen recording backend. pciutils/dmidecode: hardware probe.
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  git \
  gcc \
  clang \
  build-essential \
  zlib1g-dev \
  patchelf \
  xorg \
  xvfb \
  mesa-utils \
  pciutils \
  dmidecode \
  ffmpeg \
  libwayland-dev \
  wayland-protocols

echo "==> Installing .NET SDK ${DOTNET_CHANNEL_VERSION}"
if [ -x "${DOTNET_ROOT_DIR}/dotnet" ] && "${DOTNET_ROOT_DIR}/dotnet" --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL_VERSION} "; then
  echo "    .NET SDK ${DOTNET_CHANNEL_VERSION} already present; skipping download."
else
  tmp_installer="$(mktemp)"
  curl -Lsfo "${tmp_installer}" https://dot.net/v1/dotnet-install.sh
  chmod +x "${tmp_installer}"
  "${tmp_installer}" --version "${DOTNET_CHANNEL_VERSION}" --install-dir "${DOTNET_ROOT_DIR}" --no-path
  rm -f "${tmp_installer}"
fi

# Make `dotnet` resolvable in every shell (login and non-login) and export the
# environment SnapX's build expects. GitVersion's Mainline strategy throws on
# this repo's checkout (a known GitVersion 6.x bug), so DisableGitVersionTask
# mirrors CI and falls back to the <Version> pinned in Directory.Build.props.
echo "==> Configuring dotnet on PATH and build environment"
sudo ln -sf "${DOTNET_ROOT_DIR}/dotnet" /usr/local/bin/dotnet
sudo tee /etc/profile.d/dotnet.sh >/dev/null <<EOF
export DOTNET_ROOT="${DOTNET_ROOT_DIR}"
export PATH="${DOTNET_ROOT_DIR}:${DOTNET_ROOT_DIR}/tools:\$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export AVALONIA_TELEMETRY_OPTOUT=1
export DisableGitVersionTask=true
EOF

export DOTNET_ROOT="${DOTNET_ROOT_DIR}"
export PATH="${DOTNET_ROOT_DIR}:${DOTNET_ROOT_DIR}/tools:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DisableGitVersionTask=true

echo "==> Using .NET $(dotnet --version)"

echo "==> Restoring NuGet packages"
cd "${REPO_ROOT}"
dotnet restore SnapX.slnx
dotnet restore build/build.csproj

echo "==> SnapX environment ready."
echo "    Build:   dotnet build SnapX.slnx --no-incremental -m:1 --no-restore"
echo "    Publish: dotnet run --project build --no-restore -- build --no-color"
echo "    Run:     ./Output/snapx-ui/snapx-ui   (use Xvfb for a headless display)"
