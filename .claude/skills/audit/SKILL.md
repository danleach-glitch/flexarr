---
name: audit
description: Review the current branch before /land — bugs, performance, security, compatibility, UI/UX (with screenshots), land readiness, parity across clients and ideas — and publish the findings as a checklist artifact. Use when the user says "audit", "review this branch", or the feature is about ready to land. Reports only; fixes wait for the user to pick.
---

# /audit: review a branch before it lands

An audit reads what the branch changed, finds what's wrong or missing, and publishes it as a checklist. It never fixes anything on its own: the user picks what to fix, and items get ticked as those fixes are committed. Run it from the branch's worktree, after the feature works and before `/land`. Running it again on the same branch is a follow-up audit (step 0), not a new page.

0. **Already audited?** Look for a project memory `audit-<branch>`. If there is one, this is a follow-up: save the existing page with `read_artifact` and get its data back with `python3 scripts/claude/checklist/render.py extract <page.html> <scratchpad>/audit/data.json`, read its `checks` collection (`db_list`), and note the commit it last audited. Then run steps 1–7 on `git diff <audited commit>...HEAD` only, and in step 8 update that page instead of publishing a new one:
   - **Open items** whose code the delta touched: check the fix against the source and tick them (`{done: true, note: "<commit>"}`).
   - **Ticked items** whose code changed again: check they're still fixed; if not, untick (`{done: false, note: "reopened: <why>"}`).
   - **New findings** go after the existing ones with the next free id in their section; never renumber or reuse an id.
   - Every tick, untick and reopen above is mirrored to the hub's `ticks/<artifact slug>--<id>` (`{done, note, at}`).
   - Update `commit` and `date`, add a `history` entry saying what the follow-up covered, re-render and republish to the same url (the ticks live in the db, so they survive). A page from before the template has no data block: rebuild its data from the page, keeping every id.
   Report what got ticked, reopened and added.

1. **Scope.** `git branch --show-current` must be a feature branch, not `main`. `git fetch origin`, then the change is `git diff origin/main...HEAD` plus anything uncommitted (`git status`). List the changed files and sort them into areas: server (`src/`), web (`frontend/`), Apple (`clients/apple`, `FlexarrKit`), Mac (`clients/macos`), Android (`clients/android`), site (`site/`), relay (`relay/`). If there's nothing to review, say so and stop.

2. **Understand the feature** before judging it: the commit messages, the open pull request if there is one (`gh pr view`), a plan in `docs/` for it, its memory note, and its `## Unreleased` changelog entry. Write a five-line summary of what it's meant to do and for whom; every reviewer gets it.

3. **Cheap checks**, only for the areas touched: `dotnet build --disable-build-servers` and the affected test projects; `npx tsc -b --noEmit`, `npx oxlint src`, `npx vitest run` in `frontend/`; `swift test` in FlexarrKit; `./gradlew :app:testDebugUnitTest` in `clients/android`. Anything that runs for minutes goes in the background. Failures become findings.

4. **Reviewers.** Start these subagents in one message, so they run in parallel. Each gets the diff range, the file list, the summary, and where the rules live (CLAUDE.md, plus `docs/design.md` for UI). Tell them to read every changed file whole, plus the callers and the code it replaces, and not to edit anything. Each returns findings as `severity (High/Medium/Low/Info) · title · file:line · what goes wrong, as a concrete scenario · suggested fix · confident or uncertain`, plus a few "done well" bullets.
   - **Bugs:** logic errors, edge cases (empty, huge, missing, offline), races and cancellation, leaks, error handling that hides failures, wrong watch state or progress, crashes on data from older or newer servers.
   - **Performance:** work on the main thread, N+1 queries and missing indexes, unbounded lists, polling that never stops, extra round trips, re-renders and recompositions, memory held by images or buffers, FFmpeg work that could be skipped.
   - **Security:** auth on every new endpoint, admin-only versus profile, PIN and hidden-title boundaries, path and input handling, secrets in logs or URLs, what other apps or the LAN can reach.
   - **Compatibility and platform rules:** the API only grows (no renamed or removed fields, endpoints or enum values, and no changed meanings); new enum values are harmless as `unknown` (`ForwardCompatibleEnum`); app fields newer than the oldest supported server are optional with an "Absent from servers before …" note; no edited migration that's already on main; the Mac app's sandbox (no other programs besides its own ffmpeg/ffprobe, chosen rather than typed paths, no `libx264`/`zscale`/`libass`).
   - **UI and UX, from the code:** loading, empty, error and offline states; copy brevity (fewest words, detail in the guide); the Settings style; tvOS focus and remote navigation; touch targets, VoiceOver and TalkBack labels, Dynamic Type and large text; consistency with the screens beside it.
   - **Ideas and parity:** what would make the feature better or more complete, ranked by value for effort (S/M/L), and a table of which clients have it (web, iPhone, Apple TV, Android, Android TV, Mac), with what's missing on each.

5. **Screenshots.** While the reviewers run, use `/preview` for each client the branch's UI touched and screenshot the new or changed screens: web through Playwright, simulators with `xcrun simctl io <device> screenshot`, the emulator with `adb -s <serial> exec-out screencap -p`. Use the no-history launch flags, never trigger requests or searches, and never drive the user's own Mac. Shrink each image (`sips -Z 1400`) before it goes on the page. Look at every screenshot for UI problems (clipping, alignment, contrast, dark mode, truncated text), and add those to the UX findings with the image attached. If a client can't be previewed, say which one and why in the report.

6. **Verify.** Hand the bug, performance, security and compatibility findings to one fresh subagent with the code, not the reviewers' reasoning, and ask it to confirm or refute each one against the source. Drop the refuted ones; mark the rest confirmed or uncertain. Ideas and parity aren't verified.

7. **Land readiness**, checked yourself: a plain-language changelog entry under `## Unreleased` if people will notice the change; `docs/guide` updated for new screens and settings (and `make guide` run); `site/apps.json` if an app's status changed; tests covering the new code paths; half-built work hidden behind a setting so main stays releasable. Each gap is a `land-` item.

8. **Publish the checklist.** Every audit page comes from one fixed template, `scripts/claude/checklist/audit.html`, in the same style as the /brief pages: its look and tick script never change, and a page differs only by its data. Never hand-write the page or edit the template for one audit. Read the artifacts guide (`pages` topic) before the first publish of the session.
   - **Write the data** as `audit/data.json` in the scratchpad. `scripts/claude/checklist/example-audit.json` shows every field:
     - `title` (`<Feature> Audit`), `branch`, `commit` (the one audited), `date`, `pr` if there is one, `summary` (the five-line summary from step 2, as a paragraph), `history` (`{date, commit, text}` per audit run, newest last);
     - `mustFix`: the fix-before-landing list, `{ids, title, why}` per line; a line ticks itself when all its ids are done;
     - `sections`: keys `bug`, `perf`, `sec`, `compat`, `ux`, `land`, `idea`, each `{items, doneWell}`. A finding is `{id, sev, title, where, what, fix, verified, shots}`: `sev` is High, Medium, Low or Info (ideas: S, M or L), `where` is `file:line`, `verified` is `confirmed` or `uncertain`, `shots` is `[{src, caption}]`;
     - `parity`: `rows` of `{mark, client, what}`, marks ✅ 🟡 ❌; `screenshots`: `{intro, items: [{src, caption}]}`.
     Ids are numbered in page order and never renumbered. Text can use `**bold**`, `*em*` and `` `code` ``; nothing else is HTML.
   - **Screenshots are assets**, never inline images. Publish the page first with no screenshots, `declare_capabilities` `{db: {}, user: {}, assets: {}}`, upload the shrunk images with `upload_asset`, then put their urls in `src` and republish.
   - **Render** with `python3 scripts/claude/checklist/render.py audit <data.json> <scratchpad>/audit/index.html`. It refuses bad data (an id outside its section, an unknown severity, an id used twice, a gap in the numbering, a fix-before-landing id that isn't a finding); fix the data, never the check.
   - **Publish** the file with `publish_files` (title = the data's `title`, icon `checklist`).
   - **Register it on the checklist hub** (slug in CLAUDE.local.md, records in the `/hub` skill): the hub's `pages/<artifact slug>` = `{title, url, kind: "audit", branch, total: <every finding, ideas included>, stage: "<n> to fix before landing", registeredAt, updatedAt}`. A follow-up updates that record (`total`, `stage`, `updatedAt`).
   Ticks live in the db collection `checks`: doc id = item id, body `{done: true, note: "<commit or PR>"}`. The header's stage pill shows how many fix-before-landing lines are open, or *Ready to land*; a `_stage` doc (`{label, kind, note}`) overrides it.

9. **Remember it.** Save a project memory `audit-<branch>` with the artifact link, the collection and id scheme, the commit audited, the ids of High items and of the fix-before-landing list (`/land` checks these), and the counts, and add it to MEMORY.md. A follow-up updates the same memory.

10. **Report**, briefly: the link, counts by section, and the fix-before-landing list. Then ask which ones to fix. As each fix is committed, tick its id with `db_write` and a note. Mirror each tick to the hub's `ticks/<artifact slug>--<id>` in the same go. `/land` goes ahead once the user is happy with what's left open.
