#!/bin/bash
# Puts a Flexarr update in place on a Mac, and takes it back out if it doesn't start.
#
#   install-update.sh <root> <data> <port> <from> <to>
#
# The server (Updates/MacUpdater.cs) downloads and checks the release, unpacks it to <root>/update/app, and starts this
# as a one-shot LaunchAgent of its own (io.github.flexarr.updater), so it carries on while the server is stopped. Here:
# stop the server, copy the database aside, swap <root>/app for the new one, and start it. If it doesn't answer its
# health check, the old app and the database as it was go back, and that starts instead: a newer Flexarr may already
# have changed the database, and an older one mustn't run on that. Either way <data>/update-result.json says what
# happened, for Settings › Updates.
set -uo pipefail

root="$1" data="$2" port="$3" from="$4" to="$5"
label="io.github.flexarr.server"
domain="gui/$(id -u)"
plist="$HOME/Library/LaunchAgents/$label.plist"
stage="$root/update"
databases=(flexarr.db flexarr.db-wal flexarr.db-shm)

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

# finish <true|false> <message>: records the outcome and ends.
finish() {
  log "$2"
  local message="${2//\\/\\\\}"
  message="${message//\"/\\\"}"
  printf '{"from":"%s","to":"%s","ok":%s,"message":"%s","at":"%s"}\n' "$from" "$to" "$1" "$message" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    > "$data/update-result.json.tmp" && mv "$data/update-result.json.tmp" "$data/update-result.json"
  exit 0
}

# bootout is asynchronous: wait for the job to be gone.
stop_server() {
  launchctl bootout "$domain/$label" 2>/dev/null
  for _ in $(seq 1 60); do
    launchctl print "$domain/$label" >/dev/null 2>&1 || return 0
    sleep 0.5
  done
  return 1
}

start_server() { launchctl bootstrap "$domain" "$plist"; }

# healthy <seconds>: whether the server answers within that long. A first start after an update can be migrating the
# database, which takes a while on a big library.
healthy() {
  for _ in $(seq 1 "$1"); do
    curl -sf -m 5 "http://127.0.0.1:$port/health" >/dev/null && return 0
    sleep 1
  done
  return 1
}

log "Updating Flexarr $from to $to"
# Long enough for the server's answer to reach the browser.
sleep 2

if ! stop_server; then
  finish false "Flexarr $from didn't stop, so nothing was changed."
fi

rm -rf "$stage/database"
mkdir -p "$stage/database"
for file in "${databases[@]}"; do
  if [ -f "$data/$file" ] && ! cp -p "$data/$file" "$stage/database/$file"; then
    start_server
    finish false "Couldn't copy the database aside (is the disk full?), so nothing was changed."
  fi
done

rm -rf "$root/app.previous"
if ! mv "$root/app" "$root/app.previous" || ! mv "$stage/app" "$root/app"; then
  [ -d "$root/app" ] || mv "$root/app.previous" "$root/app"
  start_server
  finish false "Couldn't put Flexarr $to in place, so $from is still installed."
fi

start_server
if healthy 300; then
  rm -rf "$stage"
  finish true "Updated Flexarr $from to $to."
fi

log "Flexarr $to didn't start; putting $from back"
stop_server
rm -rf "$root/app.failed"
mv "$root/app" "$root/app.failed"
mv "$root/app.previous" "$root/app"
for file in "${databases[@]}"; do
  rm -f "$data/$file"
  if [ -f "$stage/database/$file" ]; then cp -p "$stage/database/$file" "$data/$file"; fi
done
rm -rf "$stage"
start_server
if healthy 120; then
  finish false "Flexarr $to didn't start, so $from was put back, with the database as it was. Its log is in $root/logs/console.log; the failed version is kept in $root/app.failed."
fi
finish false "Flexarr $to didn't start, and neither did $from after it was put back. See $root/logs/console.log."
