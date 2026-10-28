#!/usr/bin/env bash
# Build Flexarr and install/upgrade it as a LaunchAgent on a Mac reachable over SSH.
#
#   scripts/deploy-macos.sh <ssh-host>      e.g. scripts/deploy-macos.sh mini
#
# A checkout of a release tag deploys as that version. Anything else (main between releases) deploys as a build of
# the next minor version, e.g. 0.51.0-dev.1a2b3c4d, so the server never claims to be a release it isn't.
#
# Layout on the target (no admin rights needed):
#   ~/.dotnet         ASP.NET Core runtime, brought up to its latest patch on every deploy (the dotnet host itself is
#                     kept: macOS's external-drive permission belongs to it)
#   ~/.flexarr/app    binaries (replaced on every deploy)
#   ~/.flexarr/data   config.json (API key, path mappings), database, caches — never touched by deploys
#   ~/.flexarr/logs   flexarr-<date>.log (the server's own, trimmed to Settings › Logs › Retention); console.log
#                     (warnings and crashes on standard output, for when the server can't start)
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

# The version this build reports.
prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo/Directory.Build.props")"
if tag="$(git -C "$repo" describe --tags --exact-match --match 'v*' HEAD 2>/dev/null)" && [ -z "$(git -C "$repo" status --porcelain)" ]; then
  flexarr_version="${tag#v}"
  version_args=(-p:Version="$flexarr_version")
else
  IFS=. read -r major minor _ <<< "$prefix"
  suffix="dev.$(git -C "$repo" rev-parse --short=8 HEAD)"
  [ -z "$(git -C "$repo" status --porcelain)" ] || suffix="$suffix.modified"
  flexarr_version="$major.$((minor + 1)).0-$suffix"
  version_args=(-p:VersionPrefix="$major.$((minor + 1)).0" -p:VersionSuffix="$suffix")
fi
echo "==> Deploying Flexarr $flexarr_version to $host"

echo "==> Building web client"
(cd "$repo/frontend" && npm ci --silent && npm run build --silent)

echo "==> Publishing server ($rid)"
# Fresh build processes: reused MSBuild/compiler nodes can carry another working directory's path casing.
rm -rf "$out"
dotnet publish "$repo/src/Flexarr.Api" --disable-build-servers -c Release -r "$rid" --no-self-contained -p:UseAppHost=false "${version_args[@]}" -o "$out" --nologo -v quiet
cp -R "$repo/frontend/dist" "$out/wwwroot"

echo "==> Installing on $host"
runtime="$(sed -n 's/.*"version": "\([0-9]*\.[0-9]*\).*/\1/p' "$repo/global.json")"
# The latest $runtime patch, every deploy: the app is framework-dependent and runs on the newest patch installed, so
# this is how it gets ASP.NET Core and Kestrel security fixes. Earlier patches are left in place. If the release
# metadata can't be read, the deploy stops rather than carrying on with whatever is there.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -sSfL "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$runtime/releases.json" -o "$work/releases.json"
# Prints "<ASP.NET Core version> <.NET runtime version> <archive url> <archive sha512>" for the latest patch.
latest="$(python3 -I - "$work/releases.json" "$rid" <<'PY'
import json, re, sys
metadata = json.load(open(sys.argv[1]))
release = next(r for r in metadata["releases"] if r["release-version"] == metadata["latest-release"])
version = release["aspnetcore-runtime"]["version"]
netcore = release["runtime"]["version"]
for v in (version, netcore):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", v):
        sys.exit(f"unexpected runtime version {v!r} in the release metadata")
file = next(f for f in release["aspnetcore-runtime"]["files"] if f["name"] == f"aspnetcore-runtime-{sys.argv[2]}.tar.gz")
if not file["url"].startswith("https://builds.dotnet.microsoft.com/") or not re.fullmatch(r"[0-9a-fA-F]{128}", file["hash"]):
    sys.exit(f"unexpected release metadata for {file['name']}")
print(version, netcore, file["url"], file["hash"].lower())
PY
)"
read -r version netcore url sha512 <<< "$latest"
# Installed means every part of that patch is in place, down to the last folder the archive unpacks (host/fxr), so a
# half-finished install from an older deploy script counts as missing and is repaired.
installed="$(ssh "$host" "cd ~/.dotnet 2>/dev/null \
  && test -f shared/Microsoft.AspNetCore.App/$version/Microsoft.AspNetCore.App.deps.json \
  && test -f shared/Microsoft.NETCore.App/$netcore/Microsoft.NETCore.App.deps.json \
  && test -f shared/Microsoft.NETCore.App/$netcore/libcoreclr.dylib \
  && test -f host/fxr/$netcore/libhostfxr.dylib \
  && ./dotnet --list-runtimes >/dev/null 2>&1 && echo yes || true")"
if [ "$installed" != yes ]; then
  echo "    installing ASP.NET Core $version runtime"
  # Downloaded here and checked against the SHA-512 in Microsoft's release metadata before it's unpacked on $host.
  # Anything unexpected stops the deploy rather than installing it.
  curl -sSfL "$url" -o "$work/runtime.tar.gz"
  if [ "$(shasum -a 512 "$work/runtime.tar.gz" | cut -d' ' -f1)" != "$sha512" ]; then
    echo "$url doesn't match the SHA-512 Microsoft publishes for it; not installing it" >&2
    exit 1
  fi
  # Unpacked into a staging folder beside the runtime (same disk), then each version folder is renamed into place, so
  # an interrupted install never leaves a half-written runtime where the server would pick it up. Staging folders left
  # by an earlier interrupted deploy are removed first. Other installed versions are never touched.
  stage=".dotnet/.staging-$$"
  ssh "$host" "mkdir -p ~/.dotnet && find ~/.dotnet -maxdepth 1 -name '.staging-*' -exec rm -rf {} + && mkdir ~/$stage \
  && tar -xzf - -C ~/$stage" < "$work/runtime.tar.gz"
  ssh "$host" bash -s -- "$stage" <<'REMOTE'
set -euo pipefail
cd ~/.dotnet
stage="$HOME/$1"
# The .NET runtime before ASP.NET Core, which needs it: the server only rolls forward to an ASP.NET Core patch once
# everything under it is in place.
for dir in "$stage"/host/fxr/* "$stage"/shared/Microsoft.NETCore.App/* "$stage"/shared/*/*; do
  [ -d "$dir" ] || continue
  dest="${dir#"$stage"/}"
  mkdir -p "$(dirname "$dest")"
  # The same version already there is a leftover of an interrupted install: set it aside (removed with the staging
  # folder) and put the complete copy in its place.
  if [ -e "$dest" ]; then mv "$dest" "$stage/replaced-$(echo "$dest" | tr / _)"; fi
  mv "$dir" "$dest"
done
# The dotnet host only goes in once. macOS grants the external-drive access Flexarr needs to that very binary: a
# newer host (each runtime patch ships one) is a different app to it, and every folder Flexarr opens on the drive then
# waits on a permission prompt nobody sees, stalling scans and downloads. An older host runs newer runtimes fine.
if [ ! -e dotnet ] && [ -e "$stage/dotnet" ]; then mv "$stage/dotnet" dotnet; fi
# rename(2) replaces these whole.
for file in LICENSE.txt ThirdPartyNotices.txt; do
  if [ -e "$stage/$file" ]; then mv -f "$stage/$file" "$file"; fi
done
rm -rf "$stage"
REMOTE
else
  echo "    ASP.NET Core $version runtime is up to date"
fi
root="$(ssh "$host" 'echo $HOME/.flexarr')"
dotnet_root="$(ssh "$host" 'echo $HOME/.dotnet')"
ssh "$host" "mkdir -p '$root/app' '$root/data' '$root/logs' ~/Library/LaunchAgents"
# One rolling copy of the database as it was before this deploy (the server also keeps its own before a migration).
ssh "$host" 'if [ -f ~/.flexarr/data/flexarr.db ]; then sqlite3 ~/.flexarr/data/flexarr.db ".backup $HOME/.flexarr/pre-deploy.db"; fi'
echo "    database copied to ~/.flexarr/pre-deploy.db"
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
curl -sf http://localhost:8383/health >/dev/null || { echo "Flexarr did not become healthy; see $root/logs/console.log" >&2; tail -20 "$root/logs/console.log" >&2; exit 1; }
REMOTE

echo "==> Flexarr $flexarr_version is running: http://$(ssh "$host" 'scutil --get LocalHostName').local:8383"
