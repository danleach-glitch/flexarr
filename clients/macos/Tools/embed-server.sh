#!/bin/bash
# Xcode build phase: copies the published server and FFmpeg into the app (code into Contents/Helpers, the web app
# and settings into Contents/Resources/Server) and signs each executable with the helper entitlements, which sandbox
# it with the app's sandbox. Run Tools/build-ffmpeg.sh and Tools/publish-server.sh first (Tools/build.sh does).
set -euo pipefail

src="$SRCROOT/build/server"
contents="$TARGET_BUILD_DIR/$CONTENTS_FOLDER_PATH"
entitlements="$SRCROOT/FlexarrServer/Helper.entitlements"
identity="${EXPANDED_CODE_SIGN_IDENTITY:--}"

ffmpeg="$SRCROOT/build/ffmpeg/$(sed -n 's/^version=//p' "$SRCROOT/Tools/build-ffmpeg.sh")-universal"

if [ ! -x "$src/bin/Flexarr-arm64" ] || [ ! -x "$src/bin/Flexarr-x64" ]; then
  echo "error: no published server in $src; run clients/macos/Tools/publish-server.sh" >&2
  exit 1
fi
if [ ! -x "$ffmpeg/ffmpeg" ]; then
  echo "error: no FFmpeg in $ffmpeg; run clients/macos/Tools/build-ffmpeg.sh" >&2
  exit 1
fi

rm -rf "$contents/Helpers" "$contents/Resources/Server"
mkdir -p "$contents/Helpers" "$contents/Resources"
cp -R "$src/bin/" "$contents/Helpers/"
cp "$ffmpeg/ffmpeg" "$ffmpeg/ffprobe" "$contents/Helpers/"
cp -R "$src/content" "$contents/Resources/Server"

# Libraries first, then the executables.
for lib in "$contents/Helpers/"*.dylib; do
  codesign --force --sign "$identity" --timestamp=none "$lib"
done
for tool in Flexarr-arm64 Flexarr-x64 ffmpeg ffprobe; do
  codesign --force --sign "$identity" --timestamp=none --entitlements "$entitlements" "$contents/Helpers/$tool"
done
