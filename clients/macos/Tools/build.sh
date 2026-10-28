#!/bin/bash
# Builds Flexarr Server.app into clients/macos/build: publishes the server, generates the project, builds the app.
# Usage: Tools/build.sh [--skip-server]   (--skip-server reuses build/server from the last run)
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
[ "${1:-}" = "--skip-server" ] || "$here/Tools/publish-server.sh"

cd "$here"
xcodegen generate --quiet
xcodebuild -project FlexarrServer.xcodeproj -scheme FlexarrServer -configuration Release \
  -derivedDataPath build/DerivedData ${FLEXARR_MAC_BUNDLE_ID:+FLEXARR_MAC_BUNDLE_ID=$FLEXARR_MAC_BUNDLE_ID} \
  -quiet build
rm -rf "build/Flexarr Server.app"
cp -R "build/DerivedData/Build/Products/Release/Flexarr Server.app" build/
echo "Built $here/build/Flexarr Server.app"
