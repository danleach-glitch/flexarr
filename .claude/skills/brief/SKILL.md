---
name: brief
description: Turn a brief (a new feature, a fix, an improvement) into a mapped-out plan. Explore the code, ask the user's questions with the question tool, recommend ways to make it better, and publish a build checklist artifact. When the user says go, build the whole list, committing often and ticking items off as they land. Use when the user types /brief or hands over a brief to plan and build.
---

# /brief: from a brief to a built, ticked-off checklist

Two halves with a gate between them. **Map** (steps 1–6) explores, asks, recommends and publishes the checklist, and changes no code. **Build** (steps 7–10) only starts when the user says go ("go", "build it", "go ahead"). Until then, answer questions and change the plan, but don't write code.

## Map

1. **Read the brief.** The text after `/brief` is the brief. If it's empty, ask for one. Decide what kind of change it is (feature, fix, polish, integration) and give it a short kebab-case slug, e.g. `feat/sleep-timer` or `fix/resume-offset`. Look for earlier context: a memory note, a `docs/<topic>.md` plan, `docs/roadmap.md`, open issues (`gh issue list --search`), and any `## Unreleased` changelog entry on the same topic.

2. **Map it out against the code.** Find everything the brief touches before deciding anything. For a small fix, read the code yourself. For anything wider, start Explore subagents in one message (for example one for the server and API, one for web, one for the apps), each with the brief and the question "what exists today, what would change, what's risky". Work out:
   - **Where it lives:** server (`src/`), API endpoints and fields, migrations, web (`frontend/`), iPhone and Apple TV (`clients/apple`, FlexarrKit), Android and Android TV (`clients/android`), Mac (`clients/macos`), site, guide, relay, the Home Assistant repo.
   - **Parity:** which clients should get it (web, iPhone, Apple TV, Android, Android TV, Mac), shown as a ✅ 🟡 ❌ table with a legend.
   - **Rules it runs into** (CLAUDE.md): the API only grows, new enum values must be harmless as `unknown`, app fields are optional for older servers, never edit a migration that's on main, the Mac sandbox, half-built server features hidden behind a setting, the per-profile and kids/PIN boundaries.
   - **Risks and unknowns:** anything that can't be found out from the code.

3. **Ask.** Collect every question whose answer changes what gets built, and ask them with `AskUserQuestion`: up to four per call, each question understandable on its own, the recommended option first and marked "(Recommended)". Don't ask what the code, CLAUDE.md, memory or a sensible default already answers. Pick those yourself and say what you picked on the page. Answers can raise new questions, so go round again if needed. Usually one or two rounds is enough.

4. **Supercharge.** Suggest 3–8 ideas that would make the change clearly better than what was asked: a client that should get it too, an edge case that turns into a feature, a smarter default, automation, a Home Assistant or Siri hook, guide or onboarding touches. Rank them by value for effort (S/M/L), each with a one-line why. Ask with a multi-select `AskUserQuestion` (four options per question, so split across questions if there are more). The ones picked go on the build checklist. The rest go in a "Later" section on the page.

5. **Write the checklist.** Break the work into items that are each small enough for one commit, in build order: server and migrations first, then API, then each client, then guide, changelog and tests. Group them into phases. Each item gets a stable id (`srv-1`, `api-1`, `web-1`, `ios-1`, `tv-1`, `and-1`, `atv-1`, `mac-1`, `docs-1`, `test-1`, `fix-1` for fixes), a title, the files it touches and how it'll be checked (a test, a build, a preview). Ids are numbered in page order and never renumbered or reused; new items get the next free id in their section. Always include:
   - a `docs-` item for the `## Unreleased` changelog entry if people will notice the change, and one for `docs/guide` (plus `make guide`) for new screens or settings;
   - `test-` items covering the new code paths;
   - a setting that hides it, if the server part could land before it's finished.
   For a large feature (several phases or clients), the plan also goes in `docs/<topic>.md`, the way `docs/kids-profiles.md` does, and writing it is the first build item.

6. **Publish the checklist page.** Every brief page comes from one fixed template, `scripts/claude/checklist/brief.html`: its look and tick script never change, and a page differs only by its data. Never hand-write the page or edit the template for one brief. Read the artifacts guide (`pages` topic) before the first publish of the session.
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
   Save a project memory `brief-<slug>` with the artifact link and slug, the collection and id scheme, the user's answers and the branch, and add it to MEMORY.md. Then report: the link, the phase and item counts, and "say go when you're happy". Changes to the plan before go edit the data, re-render and republish the same page, and update the memory. If the scratchpad's `data.json` is gone (a later session), save the live page with `read_artifact` and get the data back with `render.py extract <page.html> <data.json>`.

## Build

7. **Branch.** At go, if the current branch is `main`, create the feature branch from an up-to-date `origin/main` (`git fetch origin && git switch -c <slug> origin/main`), or, if the user prefers a worktree, ask them to run `yolo <slug>` and carry on there. Add the branch to the data (`branch`), re-render and republish, and add it to the memory note. Commit early: yolo removes a branch with no commits when the session ends.

8. **Work the list, in order.** For each item: make the change, run the cheap check for what it touched (`dotnet build`/`test --disable-build-servers` for the affected projects; `npx tsc -b --noEmit`, `npx oxlint src`, `npx vitest run` in `frontend/`; `swift test` in FlexarrKit; `./gradlew :app:testDebugUnitTest` in `clients/android`; long ones in the background), then **commit** and **tick**:
   - Commit as soon as an item works, with only that item's files staged (one item per commit, or a few tiny ones together), and a message that says what it does. Don't let uncommitted work pile up across items. The changelog bullet goes in the same commit as the change people will notice.
   - Straight after the commit, tick the item: write `{done: true, note: "<short hash>"}` to `checks` for its id.
   - Work that turns up mid-build becomes a new item with the next free id (added to the data, re-rendered and republished) before it's built, so the page stays the whole story.
   - Don't push or open a pull request; `/land` does that.

9. **Stop only for real blockers.** Build the whole list without pausing for check-ins. Stop and ask with `AskUserQuestion` only when a decision belongs to the user (it changes what people see, the API's shape, data or migrations, or something outward-facing), or when something can't be done as planned. Say what you found, recommend an option, and carry on with the items that don't depend on it while you wait. If an item gets dropped, note why in `checks` (`{done: false, note: "dropped: <why>"}`) instead of deleting it.

10. **Finish.** When every item is ticked (or dropped with a note): run the full checks for the areas touched, update the memory note (done, commits, anything left), and report briefly: what got built, the page link, and anything dropped or open. Then offer `/preview` for the clients the branch touched and `/audit` before `/land`. Never release.
