#!/bin/bash
# Xcode build phase: copies the published server into the app (its code into Contents/Helpers, the web app and
# settings into Contents/Resources/Server) and signs each binary with the helper entitlements, which sandbox it
# with the app's sandbox. Run Tools/publish-server.sh first.
set -euo pipefail

src="$SRCROOT/build/server"
contents="$TARGET_BUILD_DIR/$CONTENTS_FOLDER_PATH"
entitlements="$SRCROOT/FlexarrServer/Helper.entitlements"
identity="${EXPANDED_CODE_SIGN_IDENTITY:--}"

if [ ! -x "$src/bin/Flexarr" ]; then
  echo "error: no published server in $src; run clients/macos/Tools/publish-server.sh" >&2
  exit 1
fi

rm -rf "$contents/Helpers" "$contents/Resources/Server"
mkdir -p "$contents/Helpers" "$contents/Resources"
cp -R "$src/bin/" "$contents/Helpers/"
cp -R "$src/content" "$contents/Resources/Server"

# Libraries first, then the executable.
for lib in "$contents/Helpers/"*.dylib; do
  codesign --force --sign "$identity" --timestamp=none "$lib"
done
codesign --force --sign "$identity" --timestamp=none --entitlements "$entitlements" "$contents/Helpers/Flexarr"
