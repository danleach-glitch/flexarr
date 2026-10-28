#!/bin/bash
# Builds the web app and publishes the server for the Mac app into clients/macos/build/server, split the way the
# app bundle needs it: bin/ holds only Mach-O code (Contents/Helpers), content/ everything else (Contents/Resources).
# The server is published for Apple silicon and Intel. A single-file server can't be joined with lipo (the .NET part
# is appended to the executable), so bin/ has Flexarr-arm64 and Flexarr-x64 and the app starts the one that fits;
# the native libraries are joined into universal ones.
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$here/build/server"
export PATH="$HOME/.dotnet:$PATH"

(cd "$repo/frontend" && npm ci --silent && npm run build --silent)
rm -rf "$out"
mkdir -p "$out/bin"
for arch in arm64 x64; do
  # Self-contained, since the sandbox can't use a .NET install elsewhere on the Mac. A single file, because macOS
  # treats everything in Contents/Helpers as code and the assemblies aren't Mach-O; native libraries stay beside it.
  dotnet publish "$repo/src/Flexarr.Api" --disable-build-servers -c Release -r "osx-$arch" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false \
    -o "$out/publish-$arch" --nologo -v quiet
  mv "$out/publish-$arch/Flexarr" "$out/bin/Flexarr-$arch"
  rm -f "$out/publish-$arch/createdump" "$out/publish-$arch/appsettings.Development.json"
done

for lib in "$out/publish-arm64/"*.dylib; do
  name="$(basename "$lib")"
  lipo -create "$lib" "$out/publish-x64/$name" -output "$out/bin/$name"
  rm "$lib" "$out/publish-x64/$name"
done
# The rest (settings and such) is the same for both.
diff -rq "$out/publish-arm64" "$out/publish-x64" || { echo "error: the arm64 and x64 publishes differ beyond the executable" >&2; exit 1; }
mv "$out/publish-arm64" "$out/content"
rm -rf "$out/publish-x64"
cp -R "$repo/frontend/dist" "$out/content/wwwroot"
echo "Published osx-arm64 and osx-x64 to $out"
