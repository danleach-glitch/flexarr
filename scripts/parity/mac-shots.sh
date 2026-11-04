#!/bin/sh
# The Mac app's nine parity screens (see compare.py): scripts/parity/mac-shots.sh <server url> <api key> <out dir>
# Builds the Mac app (debug), opens it IN FRONT for about a minute (macOS doesn't draw a covered window, so the pictures
# need it showing; the app takes them of its own window, never the screen), and copies mac-<nn>-<name>.png to <out dir>.
# Plays without history (-FlexarrNoHistory) and silently (-FlexarrMuted).
set -eu
server=$1 key=$2 out=$3
root=$(cd "$(dirname "$0")/../.." && pwd)
shots="$HOME/Library/Containers/io.github.flexarr.mac/Data/tmp/parity"
api() { curl -fsS -H "X-Api-Key: $key" "$server/api/v1$1"; }
first() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(next(l["id"] for l in d if l["type"]==sys.argv[1]))' "$1"; }
item() { python3 -c 'import json,sys; d=json.load(sys.stdin); print((d["items"] if isinstance(d,dict) else d)[0]["id"])'; }
movies=$(api /libraries | first Movies)
shows=$(api /libraries | first TvShows)
movie=$(api "/libraries/$movies/items?take=1" | item)
show=$(api "/libraries/$shows/items?take=1" | item)

dd=$(mktemp -d)
trap 'rm -rf "$dd"' EXIT
"$root/scripts/claude/sims.sh" with-dd sh -c "cd '$root/clients/apple' && xcodegen generate --quiet && xcodebuild -project Flexarr.xcodeproj -scheme FlexarrMac -destination 'platform=macOS,arch=arm64' -derivedDataPath \"\$DD\" CODE_SIGNING_ALLOWED=NO build -quiet && cp -R \"\$DD/Build/Products/Debug/Flexarr.app\" '$dd/'"
codesign --force --deep --options runtime -s "Apple Development" --entitlements "$root/clients/apple/FlexarrMac/FlexarrMac.entitlements" "$dd/Flexarr.app" 2>/dev/null

run() {
  open -W -n "$dd/Flexarr.app" --args -FlexarrServer "$server" -FlexarrApiKey "$key" -FlexarrNoHistory YES -FlexarrMuted YES \
    -FlexarrWelcomed YES -FlexarrSnapshots parity -FlexarrSnapshotWait 5 -FlexarrSnapshotQuit YES "$@"
}
rm -rf "$shots"
run -FlexarrSnapshotPlaces "01-home=home,09-browse=library:$movies,02-library=library:$movies,03-movie=item:$movie,04-show=item:$show,05-player=play:$movie,06-search=search,07-settings=settings"
run -FlexarrAskProfile YES -FlexarrSnapshotPlaces "08-profiles=home"
mkdir -p "$out"
cp "$shots"/mac-*.png "$out/"
ls "$out"/mac-*.png
