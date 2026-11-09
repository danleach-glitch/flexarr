# Multiple versions

Someone can have the same film or episode more than once: a 4K and a 1080p copy, or a Director's Cut beside the theatrical one. Flexarr treats those as versions of one title.

## Where versions come from

- **One title with several files.** Files in one film folder, or the same episode number twice in a season.
- **Linked titles.** A film or show in two libraries (typically a second Radarr or Sonarr for 4K), or two loose files that metadata gives the same ID.

## Linking

`MediaItem.LinkGroupId` groups copies of one title, and points at a row in `TitleGroups`. That table has no filter of its own, so `i.LinkGroup.Items` sees only the copies a profile can, which is how list queries keep one copy per title. It's set by `TitleLinker`, never by hand, and null for a title with one copy. No rows or files move, so unlinking only ever means clearing the column.

- Films link to films, and shows to shows, when they share a TMDB, TVDB, TVmaze or IMDb ID, directly or through a third copy.
- An ID shared by more than six titles is taken to be wrong and links nothing.
- Seasons of linked shows link by number. Episodes of linked seasons link by number or air date, and a multi-episode file links only to one covering the same range.
- The linker runs after scans and metadata refreshes, after Fix match, and once for existing libraries.

Copies are always found through `db.MediaItems`, whose filter applies the profile's hidden libraries and age limit. So a copy a profile can't see isn't a version for them. `IgnoreQueryFilters` is for the linker, deleting and mirroring watch state only.

### What's shared and what isn't

- **Watch state** is written to every copy for that profile (`FlexarrDbContext` does this when it saves), so a list query reads a title's state without knowing about links. When copies link, a title is watched if any copy was (unless the latest change was marking it unwatched), and the newest position wins.
- **Lists** show a linked title once: search, Home rows, Continue Watching, people pages and smart collections. A library's own page still lists everything in that library, and manual collections and playlists keep what was added.
- **Show pages** combine the seasons and episodes of the copies a profile can see.
- **Delete** in the dialog removes every copy in every library, including ones hidden from the current profile, and names each one. It asks for that with `DELETE /items/{id}?allCopies=true`; a plain `DELETE` takes only the item named, as it always has. Copies in a library hidden from the current profile take the admin PIN.

## What counts as a version

`VersionRules` holds these rules, shared by playback, downloads and Library Health.

- **Same cut:** the edition tags agree, and the lengths are within 5% or a minute, whichever is more. An unknown length doesn't separate two files. So a different frame rate's few percent stays the same cut, and a tagged Extended is its own.
- **Distinct version:** a different resolution label, HDR type or cut. Two files alike on all three are duplicates, and only those are spare copies in Library Health.

## Which version plays

Without a request for a particular file:

1. The profile's remembered choice for that title. If the file was replaced, a file with the same label and edition stands in. If nothing does, the choice is ignored but kept.
2. A remembered choice gives way only when it would need converting and another version of the same cut wouldn't.
3. Otherwise the automatic pick looks at the main cut, the cut of the best-quality file, so it never changes cut by itself. It runs the playback decider on each version: plays as it is, then repackaged, then converted. Within a tier, the highest resolution then bitrate wins.

A request naming a file (`FileId`, used when restarting a stream) gets that file. If it isn't a version this profile can see (gone since, or in a hidden library), the request carries on as if it hadn't named one, as it always has: the file can't be reached, and a stream isn't lost because an upgrade replaced its file. The response's `fileId` says what's playing.

The server remembers the choice, so apps only call the endpoint that sets it, and Play asks for no particular file. Warm-ups, Play on another screen, Siri and MCP all follow from that.

## API

- `GET /items/{id}` gains optional `versions` (with two or more only): each has `fileId`, `label`, `edition`, `durationSeconds`, `size` and `chosen`, in the order they're offered. Spare copies are left out. `files` keeps its meaning: this item's own files, best first.
- `PUT /items/{id}/version` with `{fileId}` sets the profile's choice, and `{fileId: null}` goes back to automatic.
- `GET /items/{id}/deletion` gains optional `copies` (`itemId`, `library`, `managedBy`, `files`) with more than one copy. `files` can now also list a managed episode's untracked files beside `managedBy`.
- `GET /library-health` gains optional `versions` (biggest first, at most 100), `versionsTotal` and `versionsBytes`; `duplicates` now means only true spares.

Older apps on a newer server get the automatic pick and linked titles but no picker. Newer apps on an older server get no `versions`, so no picker.

## Downloads

A download at a lower quality setting starts from the smallest version that meets it, so less needs converting. Original quality uses the remembered or best version. There's no per-title choice.

## Not done yet

- Separating titles linked by mistake by hand. Fix match corrects a wrong ID, which unlinks them.
- Choosing a version in the player.
- Asking whether to start over when switching to a cut of a different length. Resume is by seconds.
- One choice for a whole show.
