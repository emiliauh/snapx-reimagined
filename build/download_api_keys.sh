#!/usr/bin/env bash
# Download the private API-key source only when its immutable digest is supplied.
# This file is compiled, so transport security alone is not an adequate integrity check.
set -euo pipefail

destination=${1:-SnapX.Core/Upload/APIKeysLocal.cs}

if [[ -z "${API_KEYS:-}" ]]; then
    exit 0
fi

if [[ ! "$API_KEYS" =~ ^https:// ]]; then
    echo "API_KEYS must be an HTTPS URL." >&2
    exit 2
fi

if [[ ! "${API_KEYS_SHA256:-}" =~ ^[A-Fa-f0-9]{64}$ ]]; then
    echo "API_KEYS_SHA256 must be the 64-character SHA-256 of the trusted API-key source." >&2
    exit 2
fi

destination_dir=$(dirname "$destination")
mkdir -p "$destination_dir"
temporary_file=$(mktemp "$destination_dir/.APIKeysLocal.XXXXXX")
trap 'rm -f "$temporary_file"' EXIT

curl --fail --location --proto '=https' --tlsv1.2 --retry 3 --silent --show-error \
    "$API_KEYS" --output "$temporary_file"
printf '%s  %s\n' "$API_KEYS_SHA256" "$temporary_file" | sha256sum --check --status
install -m 600 "$temporary_file" "$destination"
