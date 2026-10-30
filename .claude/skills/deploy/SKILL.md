---
name: deploy
description: Put Flexarr on the user's Mac Mini home server — main (the default, as a dev build of the next version) or a release tag. Use when the user wants to try merged work at home, or after a release.
---

# /deploy [main|v<x.y.z>]

The Mini runs `main` as the household's test server. Only `main` or a release tag goes there, never a feature branch: a branch's migration could differ from the one that finally lands, and the Mini's real database can't take it back. If the user explicitly asks to try a branch that has no new migration under `src/Flexarr.Core/Data/Migrations`, say so and ask before going ahead; if it adds one, land it first.

1. **Make a clean checkout** of what's being deployed in the scratchpad: `git fetch origin --tags && git worktree add --detach <scratchpad>/deploy <origin/main or the tag>`.
2. **Deploy in the background** (several minutes, little output): `PATH="$HOME/.dotnet:$PATH" scripts/deploy-macos.sh mini` from that checkout. It stamps the version (a tag deploys as itself; main as `<next minor>-dev.<commit>`), copies the database to `~/.flexarr/pre-deploy.db` on the Mini, installs and waits for the health check.
3. **Check it:** the last line names the version that's running. On failure, read `ssh mini 'tail -40 ~/.flexarr/logs/console.log'` and today's log (`make logs`) before trying anything else. Never restart or change the Mini's data to make a failure go away.
4. **Report in one line:** the version on the Mini. If anything under `clients/apple` or `clients/android` changed since the devices were last updated, offer `/devices`.
5. **Clean up:** `git worktree remove <scratchpad>/deploy`.
