---
name: land
description: Finish the current branch — changelog entry, pull request, wait for CI, squash-merge into main. Use when the user says the work is done, "land it", "merge it", or "ship this branch". Never cuts a release.
---

# /land: merge the current branch into main

Landing is not releasing: the change joins the batch under `## Unreleased` and waits for the user to ask for `/release`.

1. **Check where you are.** `git branch --show-current` must be a feature branch, not `main`. If other agents work in this tree, stage with explicit pathspecs that exclude their folders.
2. **Changelog.** If the branch changes anything people will notice, CHANGELOG.md must have an entry under `## Unreleased` (rules in CLAUDE.md). Add it if missing. Internal-only work (tests, CI, refactors, docs for developers) gets none.
3. **Commit** everything that belongs to the change; nothing half-finished. Conventional prefix (`feat:`, `fix:`, `docs:`, `chore:`).
4. **Bring it up to date:** `git fetch origin && git rebase origin/main`. Resolve conflicts (CHANGELOG.md's Unreleased section is the usual one: keep both sides' entries).
5. **Check locally what the branch touched**, quickly: `dotnet build --disable-build-servers` / the affected tests for server changes, `npx tsc -b --noEmit && npx oxlint src` for the web, `swift test` in FlexarrKit for shared Apple code. CI runs the rest.
6. **Push and open the pull request:** `git push -u origin HEAD`, then `gh pr create` with a plain title and a short body (what changed and why, anything not yet tested on real devices), ending with the attribution lines from the session.
7. **Merge when CI passes:** `gh pr merge <number> --squash --auto`. GitHub merges it once the required `CI passed` check is green and deletes the remote branch. (`gh pr checks --watch` can return before that check even exists, so don't merge on its say-so.) Then wait for it in the background: `until [ "$(gh pr view <number> --json state --jq .state)" != OPEN ]; do sleep 20; done`, and check it says MERGED. If CI fails, read the failed job's log (`gh run view <id> --log-failed`), fix, push (auto-merge stays on), and wait again. Never use `--admin` to skip a red check.
8. **Pull main** in the main checkout if it's on `main`: `git -C <main checkout> pull --ff-only`.
9. **Report in two lines:** the PR link, and how many changes are now waiting on main (`git rev-list --count $(git describe --tags --abbrev=0 --match 'v*' origin/main)..origin/main`). Offer `/deploy` to try it on the Mini. If several features have built up since the last release, mention that a release is possible; don't start one.
10. **Worktree:** in a yolo worktree, tell the user they can exit Claude; yolo removes the worktree because the pull request is merged. In a `.claude/worktrees/agent-*` worktree, leave it for the parent session to remove.
