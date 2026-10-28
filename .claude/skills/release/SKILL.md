---
name: release
description: Cut a Flexarr release from main (version bump, tag, Docker image, GitHub release), then offer the Mini and Apple devices. Only when the user asks for a release. Takes minor (default), patch, or an exact version.
---

# /release [minor|patch|x.y.z]

Only run this because the user asked for a release in this conversation. Features → `minor` (the default); fixes to something already released and nothing else → `patch`.

1. **Make a clean checkout of main** in the scratchpad, so nothing local leaks in:
   `git fetch origin && git worktree add --detach <scratchpad>/release origin/main`, then work there.
2. **Show what's going out:** the `## Unreleased` section of CHANGELOG.md, in one short summary. If it's empty, stop.
3. **Prepare** (in the background; it waits for main's CI first): `scripts/release.sh prepare <bump>`.
   It refuses to continue on a red or missing CI run, bumps the four version files, gates on VersionTests, commits `release: x.y.z`, tags `vx.y.z` and pushes main plus that one tag. Read its output; if it stopped, report why and fix the cause rather than working around the script.
4. **Publish** (in the background): `scripts/release.sh publish vx.y.z`. It waits for the release commit's CI and the tag's Docker image, prints every job's result, and creates the GitHub release with that changelog section as notes. Read every printed job result before saying it worked.
5. **Tell the user the version in one line**, then offer, in this order:
   - `/deploy v<x.y.z>` to put the release on the Mini,
   - `/devices` with that version for the Apple TV, iPhone and TestFlight, if anything under `clients/apple` changed since the last release,
   - an App Store submission (the user does this in App Store Connect).
6. **Clean up:** `git worktree remove <scratchpad>/release`.

If another session may be releasing too, check `git log origin/main -3` and the tags before starting.
