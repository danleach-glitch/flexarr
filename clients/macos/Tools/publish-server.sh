#!/bin/bash
# Builds the web app and publishes the server for the Mac app into clients/macos/build/server, split the way the
# app bundle needs it: bin/ holds only Mach-O code (Contents/Helpers), content/ everything else (Contents/Resources).
# Usage: Tools/publish-server.sh [osx-arm64|osx-x64]
set -euo pipefail

rid="${1:-osx-$(uname -m | sed 's/x86_64/x64/')}"
here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$here/build/server"
export PATH="$HOME/.dotnet:$PATH"

(cd "$repo/frontend" && npm ci --silent && npm run build --silent)
rm -rf "$out"
# Self-contained, since the sandbox can't use a .NET install elsewhere on the Mac. A single file, because macOS
# treats everything in Contents/Helpers as code and the assemblies aren't Mach-O; native libraries stay beside it.
dotnet publish "$repo/src/Flexarr.Api" --disable-build-servers -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false \
  -o "$out/publish" --nologo -v quiet

mkdir -p "$out/bin" "$out/content"
mv "$out/publish/Flexarr" "$out/publish/"*.dylib "$out/bin/"
rm -f "$out/publish/createdump" "$out/publish/appsettings.Development.json"
mv "$out/publish/"* "$out/content/"
rmdir "$out/publish"
cp -R "$repo/frontend/dist" "$out/content/wwwroot"
echo "Published $rid to $out"
