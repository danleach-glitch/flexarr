---
name: release
description: Cut a Flexarr release from main (version bump, tag, Docker image, GitHub release), then offer the Mini and the devices. Only when the user asks for a release. Takes minor (default), patch, or an exact version.
---

# /release [minor|patch|x.y.z]

Only run this because the user asked for a release in this conversation. Features → `minor` (the default); fixes to something already released and nothing else → `patch`.

1. **Make a clean checkout of main** in the scratchpad, so nothing local leaks in:
   `git fetch origin && git worktree add --detach <scratchpad>/release origin/main`, then work there.
2. **Show what's going out:** the `## Unreleased` section of CHANGELOG.md, in one short summary. If it's empty, stop.
3. **Prepare** (in the background; it waits for main's CI first): `scripts/release.sh prepare <bump>`.
   It refuses to continue on a red or missing CI run, bumps the four version files, gates on VersionTests, commits `release: x.y.z`, tags `vx.y.z` and pushes main plus that one tag. Read its output; if it stopped, report why and fix the cause rather than working around the script.
4. **Publish** (in the background): `scripts/release.sh publish vx.y.z`. It waits for the release commit's CI, the tag's Docker image and its Mac builds (`macos.yml`, which Settings › Updates installs on a Mac), prints every job's result, builds the Mac app's `Flexarr.dmg` here (`scripts/mac-player-dmg.sh`: Developer ID signed and notarised with the App Store Connect API key in `~/.flexarr-release/apple.env`), and creates the GitHub release with that changelog section as notes and the Mac builds, `SHA256SUMS` and `Flexarr.dmg` attached. If the disk image's signing fails, check the API key in `apple.env` still exists and has the Admin role (only Admin keys can cloud-sign); Xcode's own sign-in isn't used. Read every printed job result before saying it worked.
5. **Tell the user the version in one line**, then offer, in this order:
   - `/deploy v<x.y.z>` to put the release on the Mini,
   - `/devices` with that version for the Apple TV, iPhone, Android phone, TestFlight and Google Play's closed test, if anything under `clients/apple` or `clients/android` changed since the last release,
   - the Mac app on the App Store: `make mac-player-upload TEAM=… BUNDLE_ID=…` (from CLAUDE.local.md), then submit its macOS version with the iPhone and Apple TV ones,
   - an App Store submission (the user does this in App Store Connect), and, once the app has production access, promoting the Play build to production in Play Console (Test and release › Closed testing › Promote release). When a store build goes live, set its `version` (and `status`) in `site/apps.json`, run `make apps`, and land that: flexarr.io/apps shows it. The first release with `Flexarr.dmg` attached also moves the Mac entry (`mac-player`) to `available` with its version and a first link `{ "label": "Download for Mac", "href": "https://github.com/danleach-glitch/flexarr/releases/latest/download/Flexarr.dmg", "short": "Download" }`; after that, set its `version` at each release. The server and web versions there are already updated by `prepare`.
6. **Clean up:** `git worktree remove <scratchpad>/release`.

If another session may be releasing too, check `git log origin/main -3` and the tags before starting.
