#!/usr/bin/env sh

if [ -n "${BASH_SOURCE+x}" ]; then
  # shellcheck disable=SC3054
  SCRIPT_PATH="${BASH_SOURCE[0]}"
else
  SCRIPT_PATH="$0"
fi
SCRIPT_DIR=$(cd "$(dirname "$SCRIPT_PATH")" && pwd)
GITVERSION_FILE="$SCRIPT_DIR/build/obj/gitversion.json"

for arg in "$@"; do
    case "$arg" in
        --version)
            if [ -f "$GITVERSION_FILE" ]; then
                # Using grep/sed to avoid dependency on 'jq' inside minimal containers
                V=$(grep '"InformationalVersion":' "$GITVERSION_FILE" | cut -d '"' -f 4 | head -n 1)
                if [ -n "$V" ]; then
                    printf '%s\n' "$V"
                    exit 0
                fi
            fi
            V=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$SCRIPT_DIR/Directory.Build.props" | head -n 1)
            if [ -n "$V" ]; then
                printf '%s\n' "$V"
                exit 0
            fi
            ;;
    esac
done

if [ -n "$BASH_VERSION" ]; then
    bash --version | head -n1
elif [ -n "$ZSH_VERSION" ]; then
    zsh --version
else
    if [ -n "$SHELL" ]; then
        shell=$(basename "$SHELL")
    else
        shell=$(ps -p $$ -o comm= 2>/dev/null | awk -F/ '{print $NF}')
    fi
    if command -v "$shell" >/dev/null 2>&1 && "$shell" --version >/dev/null 2>&1; then
        "$shell" --version | head -n1
    else
        echo "$shell (version unknown)"
    fi
fi

set -eu

os_name=$(uname -o 2>/dev/null || echo "unknown")

if [ "$os_name" = "Msys" ] || [ "$os_name" = "Cygwin" ]; then
    SCRIPT_DIR=$(cygpath "$SCRIPT_DIR")
fi

###########################################################################
# CONFIGURATION
###########################################################################

BUILD_PROJECT_FILE="$SCRIPT_DIR/build/build.csproj"
TEMP_DIRECTORY="$SCRIPT_DIR/build/temp"

DOTNET_INSTALL_URL="https://raw.githubusercontent.com/dotnet/install-scripts/47940ac9fc30a2f2dd19167165d0bb0774625f67/src/dotnet-install.sh"
DOTNET_INSTALL_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"
DOTNET_VERSION="${DOTNET_VERSION:-$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SCRIPT_DIR/global.json" | head -n 1)}"

export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1

###########################################################################
# EXECUTION
###########################################################################


if [ "$os_name" = "Darwin" ] && [ "${SKIP_MACOS_VERSION_CHECK:-}" != "1" ]; then
    USER_MACOS_VERSION=$(sw_vers -productVersion)
    USER_MACOS_VERSION_INT=$(echo "$USER_MACOS_VERSION" | awk -F. '{ printf "%d%02d", $1, $2 }')

    REQUIRED_VERSION_INT=1203

    if [ "$USER_MACOS_VERSION_INT" -lt "$REQUIRED_VERSION_INT" ]; then
        echo "This build will most likely fail as you are running an old version of macOS. Continue anyway? (y/n)"
        read -r response
        if [ "$response" != "y" ]; then
            echo "Exiting..."
            exit 1
        fi
    fi
fi

# If dotnet CLI is installed globally and it matches requested version, use for execution
IS_CI="${CI:-}"
IS_COPR="${COPR_PROJECT:-}${COPR_USERNAME:-}${COPR_CHROOT:-}"

if [ -x "$(command -v dotnet)" ] && dotnet --version >/dev/null 2>&1; then
    DOTNET_EXE=$(command -v dotnet)
    export DOTNET_EXE
elif { [ "$IS_CI" = "true" ] || [ -n "$IS_COPR" ]; } && [ "${ALLOW_DOTNET_DOWNLOAD:-0}" != "1" ]; then
    echo "Error: CI or COPR builds are not allowed to download dotnet by default. Set ALLOW_DOTNET_DOWNLOAD=1 to override." >&2
    exit 1
else
    # Download install script
    DOTNET_INSTALL_FILE="$TEMP_DIRECTORY/dotnet-install.sh"
    mkdir -p "$TEMP_DIRECTORY"
    if command -v curl >/dev/null 2>&1; then
        curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --silent --show-error \
            "$DOTNET_INSTALL_URL" --output "$DOTNET_INSTALL_FILE"
    elif command -v wget >/dev/null 2>&1; then
        wget --https-only --secure-protocol=TLSv1_2 -qO "$DOTNET_INSTALL_FILE" "$DOTNET_INSTALL_URL"
    else
        echo "Neither curl nor wget is installed. Please install one to proceed."
        exit 1
    fi

    if command -v sha256sum >/dev/null 2>&1; then
        actual_sha256=$(sha256sum "$DOTNET_INSTALL_FILE" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        actual_sha256=$(shasum -a 256 "$DOTNET_INSTALL_FILE" | awk '{print $1}')
    else
        echo "A SHA-256 utility is required to verify the .NET installer." >&2
        exit 1
    fi
    if [ "$actual_sha256" != "$DOTNET_INSTALL_SHA256" ]; then
        echo "The downloaded .NET installer did not match its expected SHA-256." >&2
        exit 1
    fi
    chmod +x "$DOTNET_INSTALL_FILE"

    # Use the repository's exact SDK version rather than a moving channel.
    DOTNET_DIRECTORY="$TEMP_DIRECTORY/dotnet-unix"
    if [ -z "$DOTNET_VERSION" ]; then
        echo "The SDK version is missing from global.json." >&2
        exit 1
    fi
    "$DOTNET_INSTALL_FILE" --install-dir "$DOTNET_DIRECTORY" --version "$DOTNET_VERSION" --no-path
    export DOTNET_EXE="$DOTNET_DIRECTORY/dotnet"
    export PATH="$DOTNET_DIRECTORY:$PATH"
fi

echo "Microsoft (R) .NET SDK version $("$DOTNET_EXE" --version)"

env "$DOTNET_EXE" build "$BUILD_PROJECT_FILE" -nodeReuse:false -p:UseSharedCompilation=false -p:NoWarn=true -nologo -clp:NoSummary --verbosity quiet
exec env "$DOTNET_EXE" run --project "$BUILD_PROJECT_FILE" --no-build -- "$@"
