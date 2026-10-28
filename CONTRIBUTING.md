# Contributing to Flexarr

Thanks for helping build Flexarr! Bug reports, feature ideas, docs and code are all welcome.

## Before you start

- Check [open issues](https://github.com/danleach-glitch/flexarr/issues) and the [roadmap](docs/roadmap.md).
- For anything larger than a small fix, open an issue (or comment on an existing one) first so we can agree on the approach.
- Be kind — see the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

| Component | Requirements | Run | Test |
| --- | --- | --- | --- |
| Server | .NET 10 SDK | `make dev-api` | `dotnet test` |
| Web | Node.js 24+ | `make dev-web` | `cd frontend && npm test` |
| Apple | Xcode 26+, XcodeGen | `make apple`, then open `clients/apple/Flexarr.xcodeproj` | `make test-apple` |
| Mac app (in development) | Xcode 26+, XcodeGen | `clients/macos/Tools/build.sh` | |

In development the server runs with `AuthenticationRequired: DisabledForLocalAddresses`, so the web client works on localhost without an API key. Runtime data (including the generated API key) lives in `src/Flexarr.Api/data/` and is git-ignored.

The Xcode projects are generated from `clients/apple/project.yml` and `clients/macos/project.yml`; edit those rather than the `.xcodeproj`, which is not committed.

The Mac app bundles this same server and web app inside Apple's sandbox, so check server changes that run another program, read a typed-in path, or need an FFmpeg feature its LGPL build leaves out (`libx264`, `zscale`, `libass`).

## Conventions

- **C#:** follow `.editorconfig`. The build treats warnings (including analyzers) as errors.
- **TypeScript:** `npm run lint` (oxlint) must pass; prefer function components and hooks.
- **Swift:** Swift 6 strict concurrency; keep networking and models in `FlexarrKit` so every Apple platform shares them.
- **API:** REST under `/api/v1`, JSON camelCase, enums as strings — the same conventions as the \*arr apps.
- **Tests:** new behaviour comes with tests. Server endpoints get an integration test via `WebApplicationFactory`.
- **Changelog:** anything people will notice gets a short entry in [CHANGELOG.md](CHANGELOG.md), under `## Unreleased` at the top (add the heading if it isn't there). Write what they can now do, in plain words, not how it was built. The changelog is shown in Settings › Changelog.

## Pull requests

1. Fork, then branch from `main`.
2. Keep PRs focused; describe what and why, and link the issue.
3. Make sure CI is green (server, web, Apple, Docker, CodeQL).
4. Use clear commit messages; [Conventional Commits](https://www.conventionalcommits.org) (`feat:`, `fix:`, `docs:`) are appreciated.

## Releases

Flexarr works from one branch, `main`, which must always be releasable. Pull requests land on it whenever they're ready; their changelog entries collect under `## Unreleased`. A release is cut when a batch is worth shipping, not after every merge.

Versions follow [semantic versioning](https://semver.org). While Flexarr is 0.x, a release with new features bumps the minor version (0.50.0 → 0.51.0) and one with only fixes to a released version bumps the patch (0.51.0 → 0.51.1).

`scripts/release.sh` does the steps and stops at the first failing check:

1. `scripts/release.sh prepare minor` (or `patch`, or an exact version) from a clean checkout of `main`: waits for main's CI to pass, renames `## Unreleased` in CHANGELOG.md to `## <version> · <day month year>`, sets the version in `Directory.Build.props` (`VersionPrefix`), `frontend/package.json`, and `clients/apple/project.yml` and `clients/macos/project.yml` (`MARKETING_VERSION`, quoted), runs the test that checks all five agree, then commits, tags `v<version>` (signed: git needs `gpg.format ssh` and `user.signingkey` set to an SSH public key that's also a signing key on your GitHub account) and pushes.
2. `scripts/release.sh publish v<version>`: waits for that commit's CI, the Docker workflow, which publishes `ghcr.io/danleach-glitch/flexarr:<version>`, `:<major>.<minor>` and `:latest`, and the Mac workflow, which builds the Apple silicon and Intel downloads that Settings › Updates installs on a Mac. Then it makes the GitHub release with those downloads attached and that version's changelog section as its notes.

An urgent fix while `main` holds unfinished work goes on a `release/<major>.<minor>` branch made from the last tag, tagged there as a patch release, and lands on `main` too.

Builds from `main` between releases report the next version with a `-dev.<commit>` suffix (`scripts/deploy-macos.sh` does this), so they never pass for a release.

By contributing you agree that your contributions are licensed under the same terms as Flexarr ([PolyForm Noncommercial 1.0.0](LICENSE)), and that the maintainer may also license them to others under other terms, including commercially.
