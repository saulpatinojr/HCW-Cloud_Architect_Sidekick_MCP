#!/usr/bin/env bash
# Creates dist/adr-mcp-<date>.zip with sources, infra, docs, samples, scripts (no build output, no local settings).
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p dist
NAME="adr-mcp-$(date -u +%Y%m%d-%H%M%S).zip"
zip -qr "dist/$NAME" . -x "*/bin/*" "*/obj/*" "dist/*" ".azurite/*" "*/TestResults/*" ".git/*" "*/local.settings.json" ".azure/*" "*.user" ".vs/*" ".idea/*"
echo "dist/$NAME"
