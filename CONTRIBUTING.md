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

In development the server runs with `AuthenticationRequired: DisabledForLocalAddresses`, so the web client works on localhost without an API key. Runtime data (including the generated API key) lives in `src/Flexarr.Api/data/` and is git-ignored.

The Xcode project is generated from `clients/apple/project.yml`; edit that file rather than the `.xcodeproj`, which is not committed.

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

Versions follow [semantic versioning](https://semver.org). While Flexarr is 0.x, a release with new features bumps the minor version (0.8.0 → 0.9.0) and one with only fixes bumps the patch (0.8.0 → 0.8.1).

1. Rename `## Unreleased` in CHANGELOG.md to `## <version> · <day month year>`.
2. Set the same version in `Directory.Build.props` (`VersionPrefix`), `frontend/package.json` (`npm version <version> --no-git-tag-version` in `frontend/`) and `clients/apple/project.yml` (`MARKETING_VERSION`). A test checks that all four agree.
3. Commit, then tag the commit `v<version>` and push the tag. The Docker workflow publishes `ghcr.io/danleach-glitch/flexarr:<version>`, `:<major>.<minor>` and `:latest`.
4. Make a GitHub release from the tag, with that version's changelog section as its notes.

By contributing you agree that your contributions are licensed under the same terms as Flexarr ([PolyForm Noncommercial 1.0.0](LICENSE)), and that the maintainer may also license them to others under other terms, including commercially.
