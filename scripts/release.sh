#!/usr/bin/env bash
# Cut a Flexarr release from origin/main, in two steps that each stop at the first problem:
#
#   scripts/release.sh prepare minor|patch|<x.y.z>   check main's CI, bump the version everywhere, commit, tag, push
#   scripts/release.sh publish v<x.y.z>               wait for that commit's CI, the tag's Docker image and Mac
#                                                     builds, then publish the GitHub release with the changelog
#                                                     section as notes and the Mac builds attached
#
# Run it from a clean checkout of main (the /release skill makes a throwaway worktree for it). Never release a red build:
# every job's result is printed, and anything but success or skipped stops the script.
set -euo pipefail

export PATH="$HOME/.dotnet:$PATH"
cd "$(git rev-parse --show-toplevel)"

die() { echo "release: $*" >&2; exit 1; }

# Waits for the run of <workflow file> for <full sha> (or a tag's push), prints every job's result, fails unless all
# succeeded or were skipped. gh needs the FULL sha: a short one silently matches nothing. Leaves the run's id in $run_id.
run_id=""
wait_for_run() {
  local workflow="$1" filter="$2" value="$3" id="" status conclusion
  for _ in $(seq 1 60); do
    id=$(gh run list --workflow "$workflow" "$filter" "$value" --limit 1 --json databaseId --jq '.[0].databaseId // empty')
    [ -n "$id" ] && break
    sleep 5
  done
  [ -n "$id" ] || die "no $workflow run found for $value"
  echo "==> Waiting for $workflow run $id ($value)"
  until [ "$(gh run view "$id" --json status --jq .status)" = completed ]; do sleep 15; done
  gh run view "$id" --json jobs --jq '.jobs[] | "    \(.conclusion)\t\(.name)"'
  conclusion=$(gh run view "$id" --json jobs --jq '[.jobs[].conclusion | select(. != "success" and . != "skipped")] | length')
  [ "$conclusion" = 0 ] || die "$workflow run $id is not green: https://github.com/danleach-glitch/flexarr/actions/runs/$id"
  run_id="$id"
}

current_version() { sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props; }

prepare() {
  local bump="${1:?usage: $0 prepare minor|patch|<x.y.z>}" sha current version major minor patch today
  [ -z "$(git status --porcelain)" ] || die "the working tree has changes; run this from a clean checkout of main"
  git fetch -q origin main --tags
  sha=$(git rev-parse origin/main)
  [ "$(git rev-parse HEAD)" = "$sha" ] || die "HEAD isn't origin/main ($sha); check out origin/main first"
  grep -q '^## Unreleased' CHANGELOG.md || die "CHANGELOG.md has no '## Unreleased' section: nothing to release"

  current=$(current_version)
  IFS=. read -r major minor patch <<<"$current"
  case "$bump" in
    minor) version="$major.$((minor + 1)).0" ;;
    patch) version="$major.$minor.$((patch + 1))" ;;
    [0-9]*.[0-9]*.[0-9]*) version="$bump" ;;
    *) die "unknown bump '$bump' (minor, patch or x.y.z)" ;;
  esac
  # Version files can already say the next version without a tag (a pull request that bumped them itself).
  if ! git rev-parse -q --verify "refs/tags/v$current" >/dev/null && [ "$bump" != "$current" ]; then
    echo "note: v$current was never tagged; pass '$current' to release it as is" >&2
  fi
  git rev-parse -q --verify "refs/tags/v$version" >/dev/null && die "v$version already exists"

  echo "==> Releasing $version (main at ${sha:0:8}, was $current)"
  wait_for_run ci.yml --commit "$sha"

  today=$(LC_ALL=en_GB.UTF-8 date '+%-d %B %Y')
  sed -i '' "s|^## Unreleased\$|## $version · $today|" CHANGELOG.md
  sed -i '' "s|<VersionPrefix>$current</VersionPrefix>|<VersionPrefix>$version</VersionPrefix>|" Directory.Build.props
  # Quoted: VersionTests only reads the quoted form.
  sed -i '' -E "s|MARKETING_VERSION: \"?[0-9.]+\"?|MARKETING_VERSION: \"$version\"|" clients/apple/project.yml clients/macos/project.yml
  (cd frontend && npm version "$version" --no-git-tag-version --allow-same-version >/dev/null)
  # flexarr.io/apps shows the server and web app's version.
  node scripts/apps/build.mjs --server-version "$version" >/dev/null

  echo "==> Checking the version is the same everywhere"
  dotnet test tests/Flexarr.Api.Tests --disable-build-servers --nologo -v quiet --filter 'FullyQualifiedName~VersionTests' \
    || die "VersionTests failed; nothing was committed (git checkout . to undo the edits)"

  git commit -qam "release: $version"
  # Signed (with the SSH key git is set to sign with), so GitHub shows the release as Verified and a tag nobody made here
  # stands out.
  git tag -s -m "Flexarr $version" "v$version" || die "couldn't sign the tag; set git's gpg.format and user.signingkey (see CONTRIBUTING.md)"
  # Main and exactly this one tag: pushing several tags at once starts no workflows.
  git push -q origin HEAD:main "v$version"
  echo "==> Pushed release: $version and v$version. Next: $0 publish v$version"
}

publish() {
  local tag="${1:?usage: $0 publish v<x.y.z>}" version sha notes builds
  version="${tag#v}"
  git fetch -q origin main --tags
  sha=$(git rev-list -n 1 "$tag") || die "no tag $tag"
  wait_for_run ci.yml --commit "$sha"
  wait_for_run docker.yml --branch "$tag"
  # The Mac builds Settings › Updates installs on a Mac (.github/workflows/macos.yml).
  wait_for_run macos.yml --branch "$tag"
  builds="$(mktemp -d)"
  gh run download "$run_id" --name mac-builds --dir "$builds"
  for rid in osx-arm64 osx-x64; do
    [ -f "$builds/flexarr-$version-$rid.tar.gz" ] || die "the Mac builds have no flexarr-$version-$rid.tar.gz"
  done
  (cd "$builds" && shasum -a 256 -c SHA256SUMS) || die "the Mac builds don't match their SHA256SUMS"
  # The changelog section for this version, without its heading.
  notes=$(awk -v v="$version" '$0 ~ "^## " v " " {on=1; next} /^## / {if (on) exit} on' CHANGELOG.md)
  [ -n "$notes" ] || die "no CHANGELOG.md section for $version"
  gh release create "$tag" --title "$version" --notes "$notes" --verify-tag "$builds"/flexarr-*.tar.gz "$builds/SHA256SUMS"
  rm -rf "$builds"
  echo "==> Released $version: https://github.com/danleach-glitch/flexarr/releases/tag/$tag"
}

case "${1:-}" in
  prepare) shift; prepare "$@" ;;
  publish) shift; publish "$@" ;;
  *) echo "usage: $0 prepare minor|patch|<x.y.z>  |  $0 publish v<x.y.z>" >&2; exit 2 ;;
esac
