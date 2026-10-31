---
name: hub
description: Keep the checklist hub (one page listing every brief, audit, review, plan and test list with its progress) in step with the checklist pages, and delete the pages marked for deletion there. Use when the user says "sync the hub", "/hub", "/hub delete", or a checklist page was ticked by hand.
---

# /hub: the checklist hub

The hub is one artifact page that lists every checklist page with its progress. Its link and slug are in CLAUDE.local.md under "Checklist hub". A page can't read another page's ticks, so the hub has its own records, and Claude keeps them current:

- `pages/<slug>`, written by Claude: `{title, url, kind, branch, total, stage, registeredAt, updatedAt, doneIds, syncedAt, lastTickAt}`. `kind` is `brief`, `audit`, `review`, `plan` or `test list`; `total` is the page's item count; `stage` is free text such as `Building` or `Landed · #228`; `doneIds` lists the page's done or dropped item ids as of `syncedAt`; `lastTickAt` is the newest tick's time.
- `ticks/<slug>--<item id>`, written by Claude as it ticks a page: `{done, note, at}` (`at` an ISO time). Live ticks since the last sync; the hub adds them to `doneIds` (or takes an untick away).
- `prefs/<slug>`, written only by the hub page itself: `{pinned, archived, deleteMarked, deleted}`. Read it, never write it.

Every `db_write` on a record that already exists needs its `ifVersion` (from `db_get` or `db_list`). Send at most 50 ops per call, one call at a time, and wait 2 seconds after a big one.

/brief, /audit and /land write to the hub as they go (register a page, mirror each tick, mark Landed). This skill catches up everything else.

## `/hub` or `/hub sync`

1. **Find the pages.** `list_artifacts`, then `db_list` the hub's `pages`. A checklist page is one with a `checks` collection: titles ending Brief, Audit, Review or Checklist, test lists, and the app plans ("Flexarr for …"). Skip the hub itself.
2. **Register what's missing.** For a page not in `pages`, work out `kind` from its title and `total` from its items: for a page from the shared template, `read_artifact`, save it, and `python3 scripts/claude/checklist/render.py extract <page.html> <data.json>`, then count the item ids; for an older page, count its rows from the page's script (each page numbers its ids its own way, so read how). Take `branch` from the page or its memory note. Write `pages/<slug>` with `registeredAt` and `updatedAt` now.
3. **Fold in the ticks.** For each registered page, `db_list` its `checks`. Update its `pages` record (with `ifVersion`): `doneIds` = every id whose doc is `done: true` or has a note starting `dropped`, `syncedAt` now, `lastTickAt` = the newest doc's `updatedAt`, and `stage` from a `_stage` doc if there is one (it's not an item). When the page has gained items since it was registered, update `total`. Then delete that page's `ticks/<slug>--*` records (`db_list` the hub's `ticks` for their versions): `doneIds` now holds them, and a stale untick left behind would cancel a later re-tick.
4. **Pages that are gone** (no longer in `list_artifacts`): delete their `pages` record.
5. **Report** in a line or two: pages added, ticks copied, totals changed, and the hub's link.

## `/hub delete`

1. `db_list` the hub's `prefs` and take the slugs with `deleteMarked: true` and no `deleted`.
2. Name them (title and progress) and ask the user to confirm with a multi-select `AskUserQuestion`, every page unticked by default meaning keep. Never delete a page that isn't marked, and never the hub.
3. For each one confirmed, `delete_artifact` (which asks the user again), then delete its `pages/<slug>` record and its `ticks/<slug>--*` records. Leave `prefs` to the page. If the page has a memory note (`brief-*`, `audit-*`), say it's gone in the note.
4. Report what was deleted and what was kept.
