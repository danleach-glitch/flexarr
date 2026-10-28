#!/bin/bash
# Xcode build phase: copies the published server into the app and signs every binary in it with the helper
# entitlements (sandboxed, inheriting the app's sandbox). Run Tools/publish-server.sh first.
set -euo pipefail

src="$SRCROOT/build/server"
dest="$TARGET_BUILD_DIR/$CONTENTS_FOLDER_PATH/Helpers/Server"
entitlements="$SRCROOT/FlexarrServer/Helper.entitlements"
identity="${EXPANDED_CODE_SIGN_IDENTITY:--}"

if [ ! -x "$src/Flexarr" ]; then
  echo "error: no published server in $src; run clients/macos/Tools/publish-server.sh" >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$(dirname "$dest")"
cp -R "$src" "$dest"

# Libraries first, then the executable. Only Mach-O files are code; the rest is sealed by the app's signature.
find "$dest" -type f -name '*.dylib' -print0 | while IFS= read -r -d '' lib; do
  codesign --force --sign "$identity" --timestamp=none "$lib"
done
codesign --force --sign "$identity" --timestamp=none --entitlements "$entitlements" "$dest/Flexarr"
