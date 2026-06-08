#!/usr/bin/env bash
# Syncs the canonical proto into the dotnet SDK's local Proto/ directory.
# Run from any directory — the script resolves paths relative to itself.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SRC="$REPO_ROOT/proto/vested/v1/connector_hub.proto"
DST="$SCRIPT_DIR/../src/VestedAI.ConnectorSdk/Proto/connector_hub.proto"

cp "$SRC" "$DST"
echo "Synced: $SRC -> $DST"
