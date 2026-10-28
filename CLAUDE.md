# Flexarr: notes for Claude

This repo is public. Private details (the home server's address, device IDs, signing team) belong in `CLAUDE.local.md`, which git ignores; never commit them here.

## How work flows

- **One branch: `main`.** No dev branch. Every change arrives through a pull request; main requires the `CI passed` check and blocks force-pushes. Main must always be releasable: half-built server features hide behind a setting, and a new client stays in its own folder until it ships.
- **Branches come from `yolo <branch>`** (a worktree under `.yolo/<branch>`, based on `origin/main`). Commit early: a branch with no commits is removed when the session ends.
- **Finishing a branch is `/land`**: changelog entry, pull request, wait for CI, squash-merge. Landing never releases.
- **Releasing is `/release`, only when the user asks.** Changes pile up under `## Unreleased` in CHANGELOG.md between releases; a release turns that batch into one version. While Flexarr is 0.x, features bump the minor (0.50.0 → 0.51.0); a patch release (0.51.1) is only for fixing something already released. Once a batch has built up, say so and offer a release, but don't cut one unasked.
- **Testing at home is `/deploy`.** The user's Mac Mini runs `main` as the household test server, stamped `<next minor>-dev.<commit>`. Only `main` or a release tag goes there: a branch's database migration could differ from the one that finally lands, and the Mini's database can't take it back.
- **Apple devices and TestFlight are `/devices`.** The App Store gets a submission only at a release.
- **Urgent fix while main holds unfinished work:** branch `release/0.51` from the `v0.51.0` tag, cherry-pick the fix, tag `v0.51.1` there, and land the fix on main too.

## Rules that apply to every change

- **Changelog:** anything people will notice gets an entry under `## Unreleased` at the top of CHANGELOG.md (add the heading if missing): a `### Area` heading and plain-language bullets about what people can now do, in the same commit as the change. Internal changes (tests, CI, refactors) get none. The server embeds the changelog and shows it in Settings › Changelog.
- **Never edit a database migration once it's on main.** Add a new one.
- **The marketing site** (`site/`, flexarr.io on Vercel) deploys whenever main changes under `site/`. A site-only change never needs a release: small copy fixes may go straight to main, and bigger ones go through a pull request for Vercel's preview link. `site/guide` is generated from `docs/guide` by `make guide`, and `site/apps.html` (flexarr.io/apps: every app, its status and version) from `site/apps.json` by `make apps`; CI checks both are current. When an app's status or store version changes, edit `site/apps.json`; the release script fills in the server's version. The iPhone app's notification relay is separate: `relay/` is its own Vercel project (flexarr-push, push.flexarr.io), deployed when `relay/` changes. Treat changes to it like server changes; the site only forwards the old `www.flexarr.io/api/push` address to it.
- **The Home Assistant integration** lives in its own repository, danleach-glitch/flexarr-home-assistant.
- **Version numbers** are written in four places (`Directory.Build.props`, `frontend/package.json`, `clients/apple/project.yml`, CHANGELOG.md) and `VersionTests` checks they agree. Only `scripts/release.sh` changes them.

## Toolchain

- .NET is in `~/.dotnet`, not on the default PATH: `export PATH="$HOME/.dotnet:$PATH"`.
- Always `dotnet build`/`dotnet test` with `--disable-build-servers`: a reused MSBuild node from a differently-cased path breaks `.editorconfig` matching and the analyzers fail the build.
- Frontend: npm. Never run prettier. Lint `npx oxlint src`, types `npx tsc -b --noEmit`, tests `npx vitest run`.
- Apple: edit `clients/apple/project.yml`, not the generated `.xcodeproj`. Keep `MARKETING_VERSION` quoted. Run `swift test` in `clients/apple/FlexarrKit` when FlexarrKit changes.
- Migrations: `dotnet ef migrations add X --project src/Flexarr.Core --startup-project src/Flexarr.Core --output-dir Data/Migrations`.
- Commands that run for minutes without output (UI test tours, deploys, Apple builds) go in the background.
