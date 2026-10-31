# Flexarr: notes for Claude

This repo is public. Private details (the home server's address, device IDs, signing team) belong in `CLAUDE.local.md`, which git ignores; never commit them here.

## How work flows

- **One branch: `main`.** No dev branch. Every change arrives through a pull request; main requires the `CI passed` check and blocks force-pushes. Main must always be releasable: half-built server features hide behind a setting, and a new client stays in its own folder until it ships.
- **Branches come from `yolo <branch>`** (a worktree under `.yolo/<branch>`, based on `origin/main`). Commit early: a branch with no commits is removed when the session ends.
- **Planning starts with `/brief`**: it maps a brief against the code, asks questions, suggests improvements and publishes a build checklist; once the user says go it builds the whole list, committing after each item and ticking it off.
- **Before landing, `/audit`** reviews the branch (bugs, performance, security, compatibility, UI/UX with screenshots, land readiness, parity, ideas) and publishes a checklist; the user picks what to fix.
- **Brief and audit pages all come from one template** in `scripts/claude/checklist/`: each page is only data, rendered and checked by `render.py`. Change the look there, never on a single page. A hub page lists every checklist with its progress: /brief, /audit and /land update it as they go, and `/hub` catches up ticks made by hand and deletes the pages marked there.
- **Finishing a branch is `/land`**: changelog entry, pull request, wait for CI, squash-merge. Landing never releases.
- **Releasing is `/release`, only when the user asks.** Changes pile up under `## Unreleased` in CHANGELOG.md between releases; a release turns that batch into one version. While Flexarr is 0.x, features bump the minor (0.50.0 → 0.51.0); a patch release (0.51.1) is only for fixing something already released. Once a batch has built up, say so and offer a release, but don't cut one unasked.
- **Testing at home is `/deploy`.** The user's Mac Mini runs `main` as the household test server, stamped `<next minor>-dev.<commit>`. Only `main` or a release tag goes there: a branch's database migration could differ from the one that finally lands, and the Mini's database can't take it back.
- **The household's devices (Apple TV, iPhone, Android phone) and TestFlight are `/devices`.** The App Store gets a submission only at a release.
- **Urgent fix while main holds unfinished work:** branch `release/0.51` from the `v0.51.0` tag, cherry-pick the fix, tag `v0.51.1` there, and land the fix on main too.

## Rules that apply to every change

- **Changelog:** anything people will notice gets an entry under `## Unreleased` at the top of CHANGELOG.md (add the heading if missing): a `### Area` heading and plain-language bullets about what people can now do, in the same commit as the change. Internal changes (tests, CI, refactors) get none. The server embeds the changelog and shows it in Settings › Changelog.
- **Never edit a database migration once it's on main.** Add a new one.
- **The marketing site** (`site/`, flexarr.io on Vercel) deploys whenever main changes under `site/`. A site-only change never needs a release: small copy fixes may go straight to main, and bigger ones go through a pull request for Vercel's preview link. `site/guide` is generated from `docs/guide` by `make guide`, and `site/apps.html` (flexarr.io/apps: every app, its status and version) from `site/apps.json` by `make apps`; CI checks both are current; `site/CLAUDE.md` says how to update the apps page. The iPhone app's notification relay is separate: `relay/` is its own Vercel project (flexarr-push, push.flexarr.io), deployed when `relay/` changes. Treat changes to it like server changes; the site only forwards the old `www.flexarr.io/api/push` address to it.
- **The apps page** (flexarr.io/apps) is built from `site/apps.json`: see `site/CLAUDE.md` for its fields and when to update it.
- **The Home Assistant integration** lives in its own repository, danleach-glitch/flexarr-home-assistant.
- **The Mac app** (`clients/macos`, in development, for the Mac App Store) bundles this same server and web app, so features reach it without app changes. But it runs in Apple's sandbox, so check server changes that run another program (anything but its own `ffmpeg`/`ffprobe`), read a path someone typed rather than chose (choosing goes through `FolderPicker`, which opens macOS's folder window there), or need an FFmpeg feature its LGPL build leaves out (`libx264`, `zscale`, `libass`). In the Mac app the server sees `InstallKind.MacApp` and `MacAppHost.IsAvailable`. Build it with `clients/macos/Tools/build.sh`.
- **Servers and apps of different versions meet.** A release reaches servers straight away but the App Store apps only after review, and people's apps update themselves while their servers update whenever they get round to it. So the server's API only grows: add fields, endpoints and enum values; never rename or remove one, or change what it means. A new enum value must be harmless to apps that read it as `unknown` (`ForwardCompatibleEnum` in FlexarrKit). The apps, in turn, treat anything newer than the oldest server they support as optional (`public var x: T? = nil`, noted "Absent from servers before …") and keep working without it. When something has to change incompatibly, add the new one beside the old and keep the old until the apps that need it are long gone.
- **Version numbers** are written in five places (`Directory.Build.props`, `frontend/package.json`, `clients/apple/project.yml`, `clients/macos/project.yml`, CHANGELOG.md) and `VersionTests` checks they agree. Only `scripts/release.sh` changes them.

## Toolchain

- .NET is in `~/.dotnet`, not on the default PATH: `export PATH="$HOME/.dotnet:$PATH"`.
- Always `dotnet build`/`dotnet test` with `--disable-build-servers`: a reused MSBuild node from a differently-cased path breaks `.editorconfig` matching and the analyzers fail the build.
- Frontend: npm. Never run prettier. Lint `npx oxlint src`, types `npx tsc -b --noEmit`, tests `npx vitest run`.
- Apple: edit `clients/apple/project.yml`, not the generated `.xcodeproj`. Keep `MARKETING_VERSION` quoted. Run `swift test` in `clients/apple/FlexarrKit` when FlexarrKit changes.
- Migrations: `dotnet ef migrations add X --project src/Flexarr.Core --startup-project src/Flexarr.Core --output-dir Data/Migrations`.
- Commands that run for minutes without output (UI test tours, deploys, Apple builds) go in the background.
