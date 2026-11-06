#!/usr/bin/env bash
# Checks which GitHub Actions run scripts/release.sh trusts: `bash scripts/release.test.sh` (needs jq). A pretend `gh`
# answers from canned runs, so nothing here talks to GitHub. The Mac builds of the run release.sh picks are signed with
# the update key, so it must only ever pick the run this repository started for the tag's own push.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
command -v jq >/dev/null || { echo "release.test: needs jq" >&2; exit 1; }
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"

# The pretend gh: `gh api …/workflows/<file>/runs?…` prints $RUNS (every run, whatever the query asked for, so the
# script's own checks are what's tested), then $RUNS.2 if there is one and --paginate asked for every page, and
# `gh run view <id> --json …` prints $work/run-<id>.json. All through the caller's --jq filter.
cat >"$work/bin/gh" <<'GH'
#!/usr/bin/env bash
set -euo pipefail
filter=. pages=1
args=()
for a in "$@"; do
  if [ "$a" = --paginate ]; then pages=all; else args+=("$a"); fi
done
set -- "${args[@]}"
for i in "${!args[@]}"; do
  [ "${args[$i]}" = --jq ] && filter="${args[$((i + 1))]}"
done
case "$1 $2" in
  "api repos/{owner}/{repo}/actions/workflows/"*)
    jq -r "$filter" "$RUNS"
    if [ "$pages" = all ] && [ -f "$RUNS.2" ]; then jq -r "$filter" "$RUNS.2"; fi ;;
  "run view") jq -r "$filter" "$WORK/run-$3.json" ;;
  *) echo "pretend gh: unexpected: $*" >&2; exit 1 ;;
esac
GH
chmod +x "$work/bin/gh"
export PATH="$work/bin:$PATH" WORK="$work" RUNS="$work/runs.json"

# shellcheck source=release.sh
source "$here/release.sh"

sha=1111111111111111111111111111111111111111
other=2222222222222222222222222222222222222222
tag=v0.63.0
us=danleach-glitch/flexarr

# run <id> <head sha> <event> <head repository, or null> <head branch>
run() {
  jq -n --argjson id "$1" --arg sha "$2" --arg event "$3" --arg from "$4" --arg branch "$5" --arg us "$us" \
    '{id: $id, head_sha: $sha, event: $event, head_branch: $branch, repository: {full_name: $us},
      head_repository: (if $from == "null" then null else {full_name: $from} end)}'
}
runs() { jq -s '{workflow_runs: .}' >"$RUNS"; }
jobs() { local id="$1"; shift; jq -n --arg status completed '{status: $status, jobs: $ARGS.positional | map({name: "job", conclusion: .})}' --args "$@" >"$work/run-$id.json"; }

failed=0
expect() {
  local what="$1" want="$2" got="$3"
  if [ "$got" = "$want" ]; then echo "ok    $what"; else echo "FAIL  $what: wanted '$want', got '$got'"; failed=1; fi
}
# Runs a function of release.sh that may `die` (which exits) and prints ok or failed.
outcome() { if ("$@") >/dev/null 2>&1; then echo ok; else echo failed; fi; }

# The newest run is a fork's pull request whose branch is named like the tag; the tag's own push is older.
{ run 30 "$other" pull_request mallory/flexarr "$tag"; run 20 "$sha" push "$us" "$tag"; } | runs
expect "a fork's pull request named like the tag is passed over" 20 "$(find_run macos.yml "$sha" "$tag")"

# Only the fork's run exists (the tag's own hasn't started, or the query came back unfiltered).
run 30 "$other" pull_request mallory/flexarr "$tag" | runs
expect "a fork's pull request alone is no run" "" "$(find_run macos.yml "$sha" "$tag")"

# Even at the very same commit, and even claiming to be a push, a run from another repository doesn't count.
{ run 31 "$sha" pull_request mallory/flexarr "$tag"; run 32 "$sha" push mallory/flexarr "$tag"; run 33 "$sha" push null "$tag"; } | runs
expect "a run from another repository at the same commit is no run" "" "$(find_run macos.yml "$sha" "$tag")"

# A run by hand, or this repository's own pull request, at the tag's commit isn't the tag's push.
{ run 34 "$sha" workflow_dispatch "$us" "$tag"; run 35 "$sha" pull_request "$us" "$tag"; run 36 "$sha" push "$us" main; } | runs
expect "only the push of the tag counts for a tag's workflow" "" "$(find_run macos.yml "$sha" "$tag")"

# Two pushes of the same tag (deleted and pushed again): the newest, which is listed first.
{ run 41 "$sha" push "$us" "$tag"; run 40 "$sha" push "$us" "$tag"; } | runs
expect "the newest of the tag's runs is picked" 41 "$(find_run macos.yml "$sha" "$tag")"

# A hundred pull requests from forks pointing at the tag's commit fill the first page of runs; the tag's own is on the
# next.
for i in $(seq 1 100); do run "$((1000 + i))" "$sha" pull_request mallory/flexarr "$tag"; done | runs
run 20 "$sha" push "$us" "$tag" | jq -s '{workflow_runs: .}' >"$RUNS.2"
expect "the tag's run is found on a later page" 20 "$(find_run macos.yml "$sha" "$tag")"
rm "$RUNS.2"

# ci.yml is matched on the commit alone (it runs for the push to main), but still only from this repository.
{ run 50 "$sha" pull_request mallory/flexarr main; run 51 "$sha" push "$us" main; } | runs
expect "a commit's CI run is this repository's" 51 "$(find_run ci.yml "$sha")"
run 52 "$other" push "$us" main | runs
expect "a run for another commit is no run" "" "$(find_run ci.yml "$sha")"

jobs 60 success skipped
expect "success and skipped jobs are green" ok "$(outcome check_jobs macos.yml 60)"
jobs 61
expect "a run with no jobs is not green" failed "$(outcome check_jobs macos.yml 61)"
jobs 62 skipped skipped
expect "a run that skipped everything is not green" failed "$(outcome check_jobs macos.yml 62)"
jobs 63 success failure
expect "a failed job is not green" failed "$(outcome check_jobs macos.yml 63)"
jobs 64 success cancelled
expect "a cancelled job is not green" failed "$(outcome check_jobs macos.yml 64)"

# All the way through: the tag's run is found, waited for and left in $run_id; the fork's never is.
{ run 30 "$other" pull_request mallory/flexarr "$tag"; run 20 "$sha" push "$us" "$tag"; } | runs
jobs 20 success
jobs 30
wait_for_run macos.yml "$sha" "$tag" >/dev/null
expect "wait_for_run leaves the tag's run in \$run_id" 20 "$run_id"
run 30 "$other" pull_request mallory/flexarr "$tag" | runs
expect "wait_for_run gives up when only a fork's run exists" failed "$(FLEXARR_RELEASE_POLL=0 outcome wait_for_run macos.yml "$sha" "$tag")"

[ "$failed" = 0 ] && echo "release.test: all passed" || { echo "release.test: FAILED" >&2; exit 1; }
