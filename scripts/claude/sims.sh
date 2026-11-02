#!/usr/bin/env bash
# Simulators, emulators and Xcode build folders for parallel sessions.
#
# Every branch (each yolo worktree) gets its own iPhone and Apple TV simulator and its own Android emulator, so
# sessions working on different features never fight over one device. Xcode builds share a small pool of
# derived-data folders (each is 5–7 GB, so one per branch filled the disk): a build takes a free folder, preferring
# the one this branch used last so it builds incrementally, and only waits when every folder is busy.
#
#   sims.sh ios | tv          UDID of this branch's simulator, created and booted if needed
#   sims.sh android           adb serial of this branch's emulator, booted if needed
#   sims.sh with-dd CMD...    run CMD holding a derived-data folder, exported as $DD (build and install inside it)
#   sims.sh stop              shut down this branch's simulators and emulator
#   sims.sh prune             delete devices of branches that no longer have a worktree, clear stale locks
#   sims.sh list              what exists, and for which branch
#
# FLEXARR_DD_SLOTS sets the pool size (default 3). The branch comes from $YOLO_BRANCH or the checkout.
set -euo pipefail

SLOTS="${FLEXARR_DD_SLOTS:-3}"
CACHE="$HOME/Library/Caches"
STATE="$CACHE/flexarr-sims"
SDK="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
ADB="$SDK/platform-tools/adb"
EMULATOR="$SDK/emulator/emulator"
AVD="${FLEXARR_AVD:-Flexarr_Phone}"
# The simulators every branch's copy is modelled on (same device type and OS).
IOS_BASE="iPhone Air"
TV_BASE="Apple TV 4K (Flexarr)"
mkdir -p "$STATE"

branch() {
  local b="${YOLO_BRANCH:-$(git branch --show-current 2>/dev/null || true)}"
  b="${b:-detached}"
  printf '%s' "${b//\//-}"
}

# Slot 1 is the original shared folder, so its lock still works for anything using the old single-folder rule.
slot_dir() { [ "$1" = 1 ] && echo "$CACHE/preview-derived-data" || echo "$CACHE/preview-derived-data-$1"; }

lock_stale() {
  local lock="$1" pid
  pid=$(cat "$lock/pid" 2>/dev/null || true)
  if [ -n "$pid" ]; then ! kill -0 "$pid" 2>/dev/null; return; fi
  # A lock from before pids were recorded: stale after 30 minutes.
  [ -n "$(find "$lock" -maxdepth 0 -mmin +30 2>/dev/null)" ]
}

with_dd() {
  [ $# -gt 0 ] || { echo "usage: sims.sh with-dd CMD..." >&2; exit 2; }
  local key last order i dir lock got=""
  key=$(branch)
  last=$(cat "$STATE/$key.slot" 2>/dev/null || echo 1)
  order="$last $(seq 1 "$SLOTS" | grep -vx "$last" | tr '\n' ' ')"
  while [ -z "$got" ]; do
    for i in $order; do
      dir=$(slot_dir "$i"); lock="$dir.lock"
      [ -d "$lock" ] && lock_stale "$lock" && rm -rf "$lock"
      if mkdir "$lock" 2>/dev/null; then echo $$ > "$lock/pid"; got=$i; break; fi
    done
    [ -n "$got" ] || { echo "sims.sh: all $SLOTS build folders busy, waiting" >&2; sleep 5; }
  done
  trap 'rm -rf "'"$lock"'"' EXIT INT TERM
  echo "$got" > "$STATE/$key.slot"
  echo "sims.sh: building in $dir" >&2
  DD="$dir" "$@"
}

udid_named() {
  xcrun simctl list devices -j | python3 -c '
import json, sys
name = sys.argv[1]
for devs in json.load(sys.stdin)["devices"].values():
    for d in devs:
        if d["name"] == name and d.get("isAvailable", True):
            print(d["udid"]); sys.exit()' "$1"
}

base_of() {
  xcrun simctl list devices -j | python3 -c '
import json, sys
name = sys.argv[1]
for rt, devs in json.load(sys.stdin)["devices"].items():
    for d in devs:
        if d["name"] == name and d.get("isAvailable", True):
            print(d["deviceTypeIdentifier"], rt); sys.exit()
sys.exit("no simulator named " + name)' "$1"
}

apple_sim() {
  local kind="$1" base name udid
  [ "$kind" = ios ] && { base="$IOS_BASE"; name="Flexarr iPhone ($(branch))"; } \
                    || { base="$TV_BASE"; name="Flexarr TV ($(branch))"; }
  udid=$(udid_named "$name")
  if [ -z "$udid" ]; then
    read -r type runtime < <(base_of "$base")
    udid=$(xcrun simctl create "$name" "$type" "$runtime")
  fi
  xcrun simctl bootstatus "$udid" -b >/dev/null 2>&1 || true
  echo "$udid"
}

serial_alive() { "$ADB" -s "$1" get-state 2>/dev/null | grep -qx device; }

# This branch's AVD: a copy of the base AVD's hardware settings with its own data, so its emulator runs beside others.
avd_name() { printf 'Flexarr_Branch_%s' "$(branch | tr -c 'A-Za-z0-9._-' '_')"; }

make_avd() {
  local name="$1" dir="$HOME/.android/avd/$1.avd" base="$HOME/.android/avd/$AVD.avd"
  [ -f "$dir/config.ini" ] && return
  [ -f "$base/config.ini" ] || { echo "sims.sh: no AVD named $AVD to copy" >&2; exit 1; }
  mkdir -p "$dir"
  sed -e "s|^AvdId=.*|AvdId=$name|" -e "s|^avd.ini.displayname=.*|avd.ini.displayname=$name|" \
    "$base/config.ini" > "$dir/config.ini"
  branch > "$dir/sims-branch"
  sed -e "s|$AVD.avd|$name.avd|g" "$HOME/.android/avd/$AVD.ini" > "$HOME/.android/avd/$name.ini"
}

android() {
  local key file serial port name pid
  key=$(branch); file="$STATE/$key.android"; name=$(avd_name)
  serial=$(cat "$file" 2>/dev/null || true)
  if [ -z "$serial" ] || ! serial_alive "$serial"; then
    make_avd "$name"
    # Emulators listen on an even console port and the next odd one for adb; take the first free pair.
    for port in $(seq 5556 2 5680); do
      lsof -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1 || lsof -iTCP:$((port + 1)) -sTCP:LISTEN >/dev/null 2>&1 \
        || break
    done
    serial="emulator-$port"
    nohup "$EMULATOR" -avd "$name" -port "$port" -no-boot-anim > "$STATE/$key.emulator.log" 2>&1 &
    pid=$!
    echo "$serial" > "$file"
    until [ "$("$ADB" -s "$serial" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ]; do
      kill -0 "$pid" 2>/dev/null || { echo "sims.sh: emulator exited; see $STATE/$key.emulator.log" >&2
                                      tail -3 "$STATE/$key.emulator.log" >&2; rm -f "$file"; exit 1; }
      sleep 3
    done
  fi
  echo "$serial"
}

stop_key() {
  local key="$1" name udid serial
  for name in "Flexarr iPhone ($key)" "Flexarr TV ($key)"; do
    udid=$(udid_named "$name"); [ -n "$udid" ] && xcrun simctl shutdown "$udid" 2>/dev/null && echo "shut down $name"
  done
  serial=$(cat "$STATE/$key.android" 2>/dev/null || true)
  if [ -n "$serial" ]; then
    serial_alive "$serial" && "$ADB" -s "$serial" emu kill >/dev/null 2>&1 && echo "shut down $serial"
    rm -f "$STATE/$key.android"
  fi
  return 0
}

live_keys() {
  local root
  root=$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null) || return 0
  git --git-dir="$root" worktree list --porcelain | sed -n 's|^branch refs/heads/||p' | tr '/' '-'
}

prune() {
  local live key name udid lock i f
  live=$(live_keys)
  while IFS=$'\t' read -r udid name; do
    key="${name#Flexarr iPhone (}"; key="${key#Flexarr TV (}"; key="${key%)}"
    grep -qxF "$key" <<<"$live" && continue
    xcrun simctl shutdown "$udid" 2>/dev/null || true
    xcrun simctl delete "$udid" && echo "deleted $name"
  done < <(xcrun simctl list devices -j | python3 -c '
import json, re, sys
for devs in json.load(sys.stdin)["devices"].values():
    for d in devs:
        if re.fullmatch(r"Flexarr (iPhone|TV) \(.+\)", d["name"]):
            print(d["udid"] + "\t" + d["name"])')
  for f in "$STATE"/*.android "$STATE"/*.slot; do
    [ -e "$f" ] || continue
    key=$(basename "$f"); key="${key%.*}"
    grep -qxF "$key" <<<"$live" && continue
    [ "${f##*.}" = android ] && stop_key "$key"
    rm -f "$f" "$STATE/$key.emulator.log"
  done
  for f in "$HOME"/.android/avd/Flexarr_Branch_*.avd/sims-branch; do
    [ -e "$f" ] || continue
    key=$(cat "$f"); name=$(basename "$(dirname "$f")" .avd)
    grep -qxF "$key" <<<"$live" && continue
    rm -rf "$HOME/.android/avd/$name.avd" "$f" && echo "deleted AVD $name"
  done
  for i in $(seq 1 "$SLOTS"); do
    lock="$(slot_dir "$i").lock"
    [ -d "$lock" ] && lock_stale "$lock" && rm -rf "$lock" && echo "cleared stale lock $lock"
  done
  return 0
}

list() {
  xcrun simctl list devices | grep -E "Flexarr (iPhone|TV) \(" | sed 's/^ *//' || true
  for f in "$STATE"/*.android; do [ -e "$f" ] && echo "$(basename "${f%.android}"): $(cat "$f")"; done
  for i in $(seq 1 "$SLOTS"); do
    local dir; dir=$(slot_dir "$i")
    printf 'build folder %s: %s%s\n' "$i" "$(du -sh "$dir" 2>/dev/null | cut -f1 || echo empty)" \
      "$([ -d "$dir.lock" ] && echo ' (busy)')"
  done
}

case "${1:-}" in
  ios|tv) apple_sim "$1" ;;
  android) android ;;
  with-dd) shift; with_dd "$@" ;;
  stop) stop_key "$(branch)" ;;
  prune) prune ;;
  list) list ;;
  *) sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
