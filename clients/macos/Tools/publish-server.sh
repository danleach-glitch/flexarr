#!/bin/bash
# Builds the web app and publishes the server self-contained for the Mac app, into clients/macos/build/server.
# Usage: Tools/publish-server.sh [osx-arm64|osx-x64]
set -euo pipefail

rid="${1:-osx-$(uname -m | sed 's/x86_64/x64/')}"
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$here/build/server"
export PATH="$HOME/.dotnet:$PATH"

(cd "$repo/frontend" && npm ci --silent && npm run build --silent)
rm -rf "$out"
# Self-contained: the sandbox can't reach a .NET install elsewhere on the Mac, and App Review wants nothing installed.
dotnet publish "$repo/src/Flexarr.Api" --disable-build-servers -c Release -r "$rid" --self-contained \
  -p:UseAppHost=true -o "$out" --nologo -v quiet
cp -R "$repo/frontend/dist" "$out/wwwroot"
echo "Published $rid to $out"
