#!/usr/bin/env bash
# Build Flexarr and install/upgrade it as a LaunchAgent on a Mac reachable over SSH.
#
#   scripts/deploy-macos.sh <ssh-host>      e.g. scripts/deploy-macos.sh mini
#
# Layout on the target (no admin rights needed):
#   ~/.dotnet         ASP.NET Core runtime, installed on first deploy
#   ~/.flexarr/app    binaries (replaced on every deploy)
#   ~/.flexarr/data   config.json (API key, path mappings), database, caches — never touched by deploys
#   ~/.flexarr/logs   flexarr.log
set -euo pipefail

host="${1:?usage: $0 <ssh-host>}"
label="io.github.flexarr.server"
# The repo's path with each folder's real capitalisation. macOS paths are case-insensitive, but Roslyn matches
# .editorconfig sections case-sensitively: building from ~/projects when the folder is ~/Projects makes the
# analyzer settings silently not apply, failing the Release build.
repo="$(python3 - "$(cd "$(dirname "$0")/.." && pwd)" <<'PY'
import os, sys
path = "/"
for part in os.path.abspath(sys.argv[1]).strip("/").split("/"):
    names = os.listdir(path)
    path = os.path.join(path, part if part in names else next((n for n in names if n.lower() == part.lower()), part))
print(path)
PY
)"
out="$repo/artifacts/deploy"

case "$(ssh "$host" uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "unsupported architecture on $host" >&2; exit 1 ;;
esac

echo "==> Building web client"
(cd "$repo/frontend" && npm ci --silent && npm run build --silent)

echo "==> Publishing server ($rid)"
# Fresh build processes: reused MSBuild/compiler nodes can carry another working directory's path casing.
rm -rf "$out"
dotnet publish "$repo/src/Flexarr.Api" --disable-build-servers -c Release -r "$rid" --no-self-contained -p:UseAppHost=false -o "$out" --nologo -v quiet
cp -R "$repo/frontend/dist" "$out/wwwroot"

echo "==> Installing on $host"
runtime="$(sed -n 's/.*"version": "\([0-9]*\.[0-9]*\).*/\1/p' "$repo/global.json")"
if ! ssh "$host" "~/.dotnet/dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App $runtime'"; then
  echo "    installing ASP.NET Core $runtime runtime"
  # A pinned copy of Microsoft's installer, checked before it runs, rather than whatever dot.net serves today.
  # To update: pick a newer commit of github.com/dotnet/install-scripts and put its src/dotnet-install.sh hash here.
  installer_commit="5147e32300a8e908f5d737c8cff63a76b4b63531"
  installer_sha256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"
  installer="$(mktemp)"
  trap 'rm -f "$installer"' EXIT
  curl -sSfL "https://raw.githubusercontent.com/dotnet/install-scripts/$installer_commit/src/dotnet-install.sh" -o "$installer"
  if [ "$(shasum -a 256 "$installer" | cut -d' ' -f1)" != "$installer_sha256" ]; then
    echo "dotnet-install.sh doesn't match its pinned checksum; not running it" >&2
    exit 1
  fi
  ssh "$host" "bash -s -- --runtime aspnetcore --channel $runtime --install-dir ~/.dotnet >/dev/null" < "$installer"
fi
root="$(ssh "$host" 'echo $HOME/.flexarr')"
dotnet_root="$(ssh "$host" 'echo $HOME/.dotnet')"
ssh "$host" "mkdir -p '$root/app' '$root/data' '$root/logs' ~/Library/LaunchAgents"
# Keep earlier builds' web files (their names change every build), so tabs opened before this deploy can still load
# the pages they ask for; files older than a week are pruned.
rsync -a --delete --filter='P wwwroot/assets/' "$out/" "$host:.flexarr/app/"
ssh "$host" "find ~/.flexarr/app/wwwroot/assets -type f -mtime +7 -delete 2>/dev/null || true"
sed -e "s#__ROOT__#$root#g" -e "s#__DOTNET_ROOT__#$dotnet_root#g" -e "s#__DOTNET__#$dotnet_root/dotnet#g" "$repo/deploy/macos/$label.plist" | ssh "$host" "cat > ~/Library/LaunchAgents/$label.plist"

ssh "$host" bash -s <<REMOTE
set -euo pipefail
domain="gui/\$(id -u)"
plist="\$HOME/Library/LaunchAgents/$label.plist"
# Reload so plist changes apply, then wait for the health check.
# bootout is asynchronous: wait for the old instance to unload or bootstrap fails with EIO.
if launchctl print "\$domain/$label" >/dev/null 2>&1; then
  launchctl bootout "\$domain/$label" || true
  for _ in \$(seq 1 30); do
    launchctl print "\$domain/$label" >/dev/null 2>&1 || break
    sleep 0.5
  done
fi
launchctl bootstrap "\$domain" "\$plist"
for _ in \$(seq 1 30); do
  curl -sf http://localhost:8383/health >/dev/null && break
  sleep 1
done
curl -sf http://localhost:8383/health >/dev/null || { echo "Flexarr did not become healthy; see $root/logs/flexarr.log" >&2; tail -20 "$root/logs/flexarr.log" >&2; exit 1; }
REMOTE

echo "==> Flexarr is running: http://$(ssh "$host" 'scutil --get LocalHostName').local:8383"
