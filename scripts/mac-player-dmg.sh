#!/usr/bin/env bash
# The Mac app (the player) as Flexarr.dmg, for downloading from flexarr.io rather than the App Store: archived, signed
# with Developer ID through Xcode's account (cloud signing: no certificate kept on this Mac), notarised and stapled with
# the App Store Connect API key, so it opens without a warning. `scripts/release.sh publish` attaches it to each release.
#
#   scripts/mac-player-dmg.sh [version]      writes build/apple/Flexarr.dmg
#
# Needs, in the environment or in ~/.flexarr-release/apple.env (private details stay out of this public repo):
#   TEAM          the Apple team ID
#   BUNDLE_ID     the iPhone app's bundle ID (one App ID across the platforms, as the App Store's)
#   ASC_KEY_ID, ASC_ISSUER_ID, ASC_KEY_PATH   the App Store Connect API key, for notarising
# and Xcode signed in to that team's account (Xcode › Settings › Accounts).
#
# This copy says when a newer one is out (FlexarrDistribution=direct; FlexarrMac/App/MacUpdates.swift). It has no
# notifications: those need the App Store's entitlements and profile.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

die() { echo "mac-player-dmg: $*" >&2; exit 1; }

[ -f "$HOME/.flexarr-release/apple.env" ] && . "$HOME/.flexarr-release/apple.env"
for name in TEAM BUNDLE_ID ASC_KEY_ID ASC_ISSUER_ID ASC_KEY_PATH; do
  [ -n "${!name:-}" ] || die "$name isn't set (environment or ~/.flexarr-release/apple.env)"
done

version="${1:-}"
out=build/apple
archive="$out/FlexarrMac-direct.xcarchive"
export_dir="$out/export-mac-direct"
dmg="$out/Flexarr.dmg"
mkdir -p "$out"
rm -rf "$archive" "$export_dir" "$dmg"

echo "==> Archiving the Mac app${version:+ $version}"
make -s tunnel-apple
(cd clients/apple && xcodegen generate --quiet)
# One of the shared build folders (scripts/claude/sims.sh), not a new one.
scripts/claude/sims.sh with-dd sh -c "xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrMac -configuration Release \
  -destination 'generic/platform=macOS' -archivePath '$archive' -derivedDataPath \"\$DD\" -allowProvisioningUpdates \
  DEVELOPMENT_TEAM='$TEAM' FLEXARR_MAC_PLAYER_BUNDLE_ID='$BUNDLE_ID' CODE_SIGN_STYLE=Automatic CODE_SIGN_IDENTITY='Apple Development' \
  FLEXARR_DISTRIBUTION=direct ${version:+MARKETING_VERSION=$version} CURRENT_PROJECT_VERSION=$(date -u +%Y%m%d%H%M) archive -quiet" \
  || die "the archive failed"

echo "==> Signing with Developer ID"
printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<plist version="1.0"><dict>' \
  '<key>method</key><string>developer-id</string><key>destination</key><string>export</string>' \
  "<key>teamID</key><string>$TEAM</string><key>signingStyle</key><string>automatic</string>" \
  '</dict></plist>' > "$out/ExportOptions-mac-direct.plist"
xcodebuild -exportArchive -archivePath "$archive" -exportOptionsPlist "$out/ExportOptions-mac-direct.plist" \
  -exportPath "$export_dir" -allowProvisioningUpdates -quiet \
  || die "the Developer ID export failed: is Xcode signed in to the team's account (Xcode › Settings › Accounts)?"
app="$export_dir/Flexarr.app"
codesign --verify --deep --strict "$app" || die "the exported app's signature doesn't verify"
signature=$(codesign -dvv "$app" 2>&1)
[[ "$signature" == *"Authority=Developer ID Application"* ]] || die "the app isn't signed with Developer ID"

echo "==> Making the disk image"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
ditto "$app" "$staging/Flexarr.app"
ln -s /Applications "$staging/Applications"
hdiutil create -quiet -volname Flexarr -srcfolder "$staging" -fs HFS+ -format ULFO "$dmg" || die "hdiutil couldn't make the disk image"

echo "==> Notarising (a few minutes)"
xcrun notarytool submit "$dmg" --key "$ASC_KEY_PATH" --key-id "$ASC_KEY_ID" --issuer "$ASC_ISSUER_ID" --wait --timeout 30m \
  | tee "$out/notarize.log"
[[ "$(cat "$out/notarize.log")" == *"status: Accepted"* ]] || die "notarisation wasn't accepted (xcrun notarytool log <id> says why)"
xcrun stapler staple -q "$dmg" || die "couldn't staple the ticket to the disk image"
xcrun stapler validate -q "$dmg" || die "the stapled ticket doesn't validate"

echo "==> $dmg ($(du -h "$dmg" | cut -f1))"
