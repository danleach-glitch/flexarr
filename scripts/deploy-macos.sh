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
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/artifacts/deploy"

case "$(ssh "$host" uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "unsupported architecture on $host" >&2; exit 1 ;;
esac

echo "==> Building web client"
(cd "$repo/frontend" && npm ci --silent && npm run build --silent)

echo "==> Publishing server ($rid)"
rm -rf "$out"
dotnet publish "$repo/src/Flexarr.Api" -c Release -r "$rid" --no-self-contained -p:UseAppHost=false -o "$out" --nologo -v quiet
cp -R "$repo/frontend/dist" "$out/wwwroot"

echo "==> Installing on $host"
runtime="$(sed -n 's/.*"version": "\([0-9]*\.[0-9]*\).*/\1/p' "$repo/global.json")"
if ! ssh "$host" "~/.dotnet/dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App $runtime'"; then
  echo "    installing ASP.NET Core $runtime runtime"
  curl -sSL https://dot.net/v1/dotnet-install.sh | ssh "$host" "bash -s -- --runtime aspnetcore --channel $runtime --install-dir ~/.dotnet >/dev/null"
fi
root="$(ssh "$host" 'echo $HOME/.flexarr')"
dotnet_root="$(ssh "$host" 'echo $HOME/.dotnet')"
ssh "$host" "mkdir -p '$root/app' '$root/data' '$root/logs' ~/Library/LaunchAgents"
rsync -a --delete "$out/" "$host:.flexarr/app/"
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
