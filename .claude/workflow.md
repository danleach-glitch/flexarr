# Workflow

base: main
checks: see "Checks" below (only for the areas the branch touched)
ci: required
deploy: main is not deployed by landing. `/deploy` puts main or a tag on the Mac Mini; `/release` cuts versions.
preview: see /preview below

## Checks

Run only what the branch touched; anything that takes minutes goes in the background.

- Server (`src/`): `dotnet build --disable-build-servers` and the affected test projects (`dotnet test --disable-build-servers`)
- Web (`frontend/`): `npx tsc -b --noEmit && npx oxlint src && npx vitest run`
- Shared Apple code (`clients/apple/FlexarrKit`): `swift test`
- Android (`clients/android`): `./gradlew :app:testDebugUnitTest`
- Every `xcodebuild` goes through `scripts/claude/sims.sh with-dd` (CLAUDE.md).

## /brief

**Areas** (one Explore subagent each when the brief is wide): server (`src/`), API endpoints and fields, migrations, web (`frontend/`), iPhone and Apple TV (`clients/apple`, FlexarrKit), Android and Android TV (`clients/android`), Mac (`clients/macos`), site, guide, relay, the Home Assistant repo.

**Parity:** say which clients should get it (web, iPhone, Apple TV, Android, Android TV, Mac) as a ✅ 🟡 ❌ table with a legend.

**Rules a plan must respect** (CLAUDE.md): the API only grows, new enum values must be harmless as `unknown`, app fields are optional for older servers, never edit a migration that's on main, the Mac sandbox, half-built server features hidden behind a setting, the per-profile and kids/PIN boundaries.

**Ideas** can include Home Assistant or Siri hooks and guide or onboarding touches.

**Item ids:** `srv-`, `api-`, `web-`, `ios-`, `tv-`, `and-`, `atv-`, `mac-`, `docs-`, `test-`, `fix-`. Every plan has a `docs-` item for the `## Unreleased` changelog entry if people will notice the change, one for `docs/guide` (plus `make guide`) for new screens or settings, and a setting that hides it if the server part could land before it's finished. A large feature's plan also goes in `docs/<topic>.md` (like `docs/kids-profiles.md`), and writing it is the first item.

**Checklist page** (instead of only the plan file; keep the plan file too, so `/land` can find it). **Publish the checklist page.** Every brief page comes from one fixed template, `scripts/claude/checklist/brief.html`: its look and tick script never change, and a page differs only by its data. Never hand-write the page or edit the template for one brief. Read the artifacts guide (`pages` topic) before the first publish of the session.
   - **Write the data** as `brief/data.json` in the scratchpad. `scripts/claude/checklist/example-brief.json` shows every field:
     - `title` (`<Feature> Brief`), `brief` (the user's brief in their own words, as typed), `summary` (a paragraph on what gets built and for whom), `date`, `kind` (feature, fix, polish, integration, tooling), `branch` once there is one, `plan` if there's a `docs/<topic>.md`;
     - `decided`: the user's answers, one bullet each, starting with a bold label (`"**Kids:** …"`). These are the key decisions at the top of the page;
     - `defaults`: what you picked without asking;
     - `parity`: `rows` of `{mark, client, what}`, marks ✅ 🟡 ❌;
     - `phases`: `{name: "Phase 1 · Server", intro, items}`, or `groups: [{name, items}]` instead of `items` when a phase covers several clients. Each item is `{id, title, detail, files, check}`, plus `you: true` for anything only the user can do (an account, a real device, an App Store step);
     - `later` (ideas not picked, with S/M/L) and `risks`.
     Text can use `**bold**`, `*em*` and `` `code` ``; nothing else is HTML.
   - **Render** with `python3 scripts/claude/checklist/render.py brief <data.json> <scratchpad>/brief/index.html`. It refuses bad data (a missing brief or decisions, an unknown id prefix, an id used twice, a gap in the numbering); fix the data, never the check.
   - **Publish** the file with `publish_files` (title = the data's `title`, icon `checklist`), then `declare_capabilities` `{db: {}, user: {}}`. Later changes re-render and republish to the same slug.
   Ticks live in the db collection `checks`: doc id = item id, body `{done: true, note: "<short commit hash>"}`. The page reads them live, so ticking never needs a republish. A `_stage` doc (`{label, kind, note}`) overrides the header's stage pill, which otherwise says *Waiting for go*, *Building* or *Built* by itself.
   **Register it on the checklist hub** (slug in CLAUDE.local.md, records in the `/hub` skill): write the hub's `pages/<artifact slug>` = `{title, url, kind: "brief", branch, total: <item count>, stage: "Waiting for go", registeredAt, updatedAt}`.
   Save a project memory `brief-<slug>` with the artifact link and slug, the collection and id scheme, the user's answers and the branch, and add it to MEMORY.md. Then report: the link, the phase and item counts, and "say go when you're happy". Changes to the plan before go edit the data, re-render and republish the same page, and update the memory. If the scratchpad's `data.json` is gone (a later session), save the live page with `read_artifact` and get the data back with `render.py extract <page.html> <data.json>`.

**Ticking while building:** straight after each commit, write `{done: true, note: "<short hash>"}` to `checks` for the item's id, and mirror it to the hub: `ticks/<artifact slug>--<id>` = `{done: true, note, at: <now, ISO>}` (pass `ifVersion` if that record exists). Both writes or neither. New items mid-build are added to the data, re-rendered and republished, and the hub's `total` goes up to match. At go, add the branch to the data, republish, and update the hub's `pages` record (`branch`, `stage: "Building"`). A dropped item gets `{done: false, note: "dropped: <why>"}`, mirrored to the hub.

## /preview

- **Web** (`frontend/`): `cd frontend && FLEXARR_API_URL=http://localhost:<api port> npx vite --port $PORT --strictPort`.
- **Server:** if the branch changed `src/`, start this worktree's own server: `ASPNETCORE_URLS=http://localhost:$((PORT+5000)) dotnet watch --project src/Flexarr.Api` (log and PID alongside the web ones), and point the web at it. Otherwise use the server already on :8383 (`make dev-api` from the main checkout), starting it there if nothing is listening.
- **Apple TV, iPhone, Android:** only this branch's own simulators and emulator from `scripts/claude/sims.sh ios|tv|android`, built through `sims.sh with-dd`. Never drive the user's own Mac or real devices. `/preview stop` also runs `scripts/claude/sims.sh stop`.
- Parts: `/preview web`, `/preview server`, `/preview ios`, `/preview tv`, `/preview android`; no part = whatever the branch's diff touched.

## /audit

**Areas:** server (`src/`), web (`frontend/`), Apple (`clients/apple`, `FlexarrKit`), Mac (`clients/macos`), Android (`clients/android`), site (`site/`), relay (`relay/`). Reviewers also read `docs/design.md` for UI.

**Reviewer extras:** Bugs: wrong watch state or progress, crashes on data from older or newer servers. Performance: recompositions, FFmpeg work that could be skipped. Security: admin-only versus profile, PIN and hidden-title boundaries, what other apps or the LAN can reach. Compatibility: no renamed or removed API fields, endpoints or enum values and no changed meanings; new enum values harmless as `unknown` (`ForwardCompatibleEnum`); app fields newer than the oldest supported server are optional with an "Absent from servers before …" note; no edited migration already on main; the Mac sandbox (no other programs besides its own ffmpeg/ffprobe, chosen rather than typed paths, no `libx264`/`zscale`/`libass`). UX: copy brevity (fewest words, detail in the guide), the Settings style, tvOS focus and remote navigation, VoiceOver and TalkBack labels, Dynamic Type.

**Screenshots:** web through Playwright, simulators with `xcrun simctl io <device> screenshot`, the emulator with `adb -s <serial> exec-out screencap -p`. Use the no-history launch flags, never trigger requests or searches. Shrink each image with `sips -Z 1400`.

**Land readiness extras:** `site/apps.json` if an app's status changed; `make guide` run after `docs/guide` changes.

**Follow-up audits:** **Already audited?** Look for a project memory `audit-<branch>`. If there is one, this is a follow-up: save the existing page with `read_artifact` and get its data back with `python3 scripts/claude/checklist/render.py extract <page.html> <scratchpad>/audit/data.json`, read its `checks` collection (`db_list`), and note the commit it last audited. Then run steps 1–7 on `git diff <audited commit>...HEAD` only, and in step 8 update that page instead of publishing a new one:
   - **Open items** whose code the delta touched: check the fix against the source and tick them (`{done: true, note: "<commit>"}`).
   - **Ticked items** whose code changed again: check they're still fixed; if not, untick (`{done: false, note: "reopened: <why>"}`).
   - **New findings** go after the existing ones with the next free id in their section; never renumber or reuse an id.
   - Every tick, untick and reopen above is mirrored to the hub's `ticks/<artifact slug>--<id>` (`{done, note, at}`).
   - Update `commit` and `date`, add a `history` entry saying what the follow-up covered, re-render and republish to the same url (the ticks live in the db, so they survive). A page from before the template has no data block: rebuild its data from the page, keeping every id.
   Report what got ticked, reopened and added.

**Checklist page** (as well as the audit file, which `/land` reads). **Publish the checklist.** Every audit page comes from one fixed template, `scripts/claude/checklist/audit.html`, in the same style as the /brief pages: its look and tick script never change, and a page differs only by its data. Never hand-write the page or edit the template for one audit. Read the artifacts guide (`pages` topic) before the first publish of the session.
   - **Write the data** as `audit/data.json` in the scratchpad. `scripts/claude/checklist/example-audit.json` shows every field:
     - `title` (`<Feature> Audit`), `branch`, `commit` (the one audited), `date`, `pr` if there is one, `summary` (the five-line summary from step 2, as a paragraph), `history` (`{date, commit, text}` per audit run, newest last);
     - `mustFix`: the fix-before-landing list, `{ids, title, why}` per line; a line ticks itself when all its ids are done;
     - `sections`: keys `bug`, `perf`, `sec`, `compat`, `ux`, `land`, each `{items, doneWell}`. A finding is `{id, sev, title, where, what, fix, verified, shots}`: `sev` is High, Medium, Low or Info, `where` is `file:line`, `verified` is `confirmed` or `uncertain`, `shots` is `[{src, caption}]`, and `issue` (optional) is the link to the finding's GitHub issue, added if the findings are filed later;
     - `screenshots`: `{intro, items: [{src, caption}]}`.
     An audit doesn't suggest ideas or check parity across clients (that's /brief's job), so new pages have no `idea` section and no `parity`. Older pages that have them keep them on a follow-up.
     Ids are numbered in page order and never renumbered. Text can use `**bold**`, `*em*` and `` `code` ``; nothing else is HTML.
   - **Screenshots are assets**, never inline images. Publish the page first with no screenshots, `declare_capabilities` `{db: {}, user: {}, assets: {}}`, upload the shrunk images with `upload_asset`, then put their urls in `src` and republish.
   - **Render** with `python3 scripts/claude/checklist/render.py audit <data.json> <scratchpad>/audit/index.html`. It refuses bad data (an id outside its section, an unknown severity, an id used twice, a gap in the numbering, a fix-before-landing id that isn't a finding); fix the data, never the check.
   - **Publish** the file with `publish_files` (title = the data's `title`, icon `checklist`).
   - **Register it on the checklist hub** (slug in CLAUDE.local.md, records in the `/hub` skill): the hub's `pages/<artifact slug>` = `{title, url, kind: "audit", branch, parent, total: <every finding, ideas included on older pages>, stage: "<n> to fix before landing", registeredAt, updatedAt}`. `parent` is the slug of the branch's brief (from its `brief-<slug>` memory, or the hub's `pages` record of kind `brief` on the same branch); the hub shows the audit's progress bar under the brief's instead of as a row of its own. Leave it out when the branch has no brief. A follow-up updates that record (`total`, `stage`, `updatedAt`).
   Ticks live in the db collection `checks`: doc id = item id, body `{done: true, note: "<commit or PR>"}`. The header's stage pill shows how many fix-before-landing lines are open, or *Ready to land*; a `_stage` doc (`{label, kind, note}`) overrides it.

**Remember it.** **Remember it.** Save a project memory `audit-<branch>` with the artifact link, the collection and id scheme, the commit audited, the ids of High items and of the fix-before-landing list (`/land` checks these), and the counts, and add it to MEMORY.md. A follow-up updates the same memory.

**Fixing.** **Fix, commit, tick, one at a time.** Work through the picked findings one fix at a time:
   - **Commit after each fix.** Several findings with one cause can share a commit; unrelated fixes never do. Commit early: hw removes a branch with no commits when the session ends.
   - **Straight after that commit**, tick each id it fixes with `db_write`: `{done: true, note: "<short hash>"}` in `checks`. In the same breath, mirror it to the hub's `ticks/<artifact slug>--<id>` = `{done: true, note, at: <now, ISO>}` (pass `ifVersion` if that record already exists). Both writes or neither.
   - Never batch ticks at the end, and never tick a finding that isn't fixed and tested. A finding the user decides to leave gets `{done: false, note: "dropped: <why>"}`, mirrored to the hub too.
   - **Keep the hub's stage current.** When a tick closes a fix-before-landing line (all its ids done or dropped), update the hub's `pages/<artifact slug>` (with `ifVersion`): `stage` = `"<n> to fix before landing"` with the new count, or `"Ready to land"` when none are left, and `updatedAt` now.
   `/land` goes ahead once the user is happy with what's left open.

## /land

### Before

- **Open audit items.** If there's a project memory `audit-<branch>`, read the checklist's `checks` collection (`db_list`, slug from the memory) and compare it with the High and fix-before-landing ids the memory lists. If any are still open, name them and ask whether to fix them first or land anyway; don't land past them unasked. If the branch has commits newer than the audited one, mention that `/audit` would review them.
- **Changelog.** If the branch changes anything people will notice, CHANGELOG.md must have an entry under `## Unreleased` (rules in CLAUDE.md). Add it if missing. Internal-only work (tests, CI, refactors, docs for developers) gets none.

### After

- **Mark the pages landed.** For each project memory about this branch with a checklist page (`brief-<slug>`, `audit-<branch>`), write `checks/_stage` = `{label: "Landed", kind: "done", note: "#<PR number>"}` with `db_write` (read it first with `db_get`; if it exists, pass its version as `ifVersion`). The page's stage pill then says *Landed · #PR* without a republish. Note the PR in each memory. Then set `stage: "Landed · #<PR number>"` and `updatedAt` on each page's record in the checklist hub (`pages/<artifact slug>`, hub slug in CLAUDE.local.md), so it moves to Landed there too.
- Report also: how many changes are now waiting on main (`git rev-list --count $(git describe --tags --abbrev=0 --match 'v*' origin/main)..origin/main`). Offer `/deploy` to try it on the Mini. If several features have built up since the last release, mention that a release is possible; don't start one.
- Worktree: in a `.claude/worktrees/agent-*` worktree, leave it for the parent session to remove.
