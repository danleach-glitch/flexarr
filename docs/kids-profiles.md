# Kids profiles: plan

Built: server, web, Apple TV, iPhone/iPad and Android (phases 1 to 5). What waits is under *Later*. A profile can be given an age limit, so it only sees titles rated for that age, and a kids profile gets a simpler app it can't leave without a parent. Muting language and skipping scenes ("Family filter", discussion #44) is a separate feature.

Decided so far:

- **Ratings:** UK (BBFC) steps: **U · PG · 12 · 15 · 18**. Every country's rating is rounded up to the next step (Germany's 16 is 18), with UK equivalents for a few (PG-13 is 12, TV-14 is 15, R is 18); a rating with its reasons counts by its first word. Titles looked up on TMDB take the GB certificate first; Radarr and Sonarr only give US ones, which convert well enough.
- **Requests:** kids profiles never request. Titles above their limit don't appear at all, so there's nothing to ask for.
- **Parent:** the main profile. Its PIN lets a child leave a kids profile and unlocks a device locked to kids.
- **Unrated titles** (all music and home videos, and some films and shows) are hidden from a limited profile unless a parent allows the title or the whole library.
- **Fixed limits:** a parent picks the step and changes it when they choose. No birthdays are stored.
- **First landing:** server, web, Apple TV, iPhone/iPad and Android.
- **Later:** screen time (a daily allowance and a bedtime), a weekly view of what each child watched, and Ask a parent with push approval.

## What people get

### Setting it up (web Settings › Profiles, main profile, unlocked)

- **Age limit** on any profile: *None · 15 · 12 · PG · U*. A teen can have a limit and keep the normal app.
- **Kids profile** switch, on by default when the limit is U or PG: the simpler app (below), no way out without the main profile's PIN, and a picture from a set of characters instead of an initial.
- Creating the first kids profile prompts you to give every other profile a PIN. Without one, a child can choose that profile from the picker.
- **Per title:** *Allow for kids* or *Block for kids* on a film's or show's page (the main profile only). Allow lets an unrated or over-limit title through for every limited profile; Block hides it from them whatever its rating.
- **Per library:** *Fine for kids*, so everything in it shows regardless of rating (a cartoons folder, a family home videos library). Hidden libraries still apply on top of this.

### What a limited profile sees

Everything over its limit is gone, enforced by the server, so older apps get the filter too:

- libraries, Home rows, Continue Watching, Up Next and the next-episode countdown
- search, Discover (TMDB results without a suitable GB/US certificate are left out), Coming Soon and Upcoming
- people pages (their filmographies), collections (smart ones re-evaluated per profile), and playlists (other profiles' shared playlists lose items it can't see)
- Live TV: only channels the main profile has allowed (guide ratings are unreliable); DVR recordings inherit their channel
- podcasts marked explicit, and music unless the library is fine for kids
- downloads on phones, the Apple TV Top Shelf, Siri and the assistant (MCP), notifications
- opening an item by its id, playing it and its subtitles: a 404, as for a hidden library (artwork is served without signing in, by id, as for hidden libraries)
- the household's Now Playing shows "Something private", as for a hidden library
- anything Flexarr hasn't got (Discover's TMDB rows, Coming Soon's unadded titles, requests, podcast directories): a title not in the library has no age to check, so a limited profile, teen or child, is kept out of them altogether

### The kids app (kids profiles only)

- **Home:** large artwork, *Watch again* (finished titles, newest first), *Keep watching*, then a row per library. The main profile can pin up to three rows it chooses (a collection or a genre).
- **Not there:** Settings (only the profile's own subtitle size), requests, Discover, Live TV's guide grid (channels only), deleting, marking watched, ratings and critic scores, trailers of anything outside the limit.
- **Leaving:** switching to a profile with a looser limit, or none, asks for the main profile's PIN (any limited profile, not only kids). Signing out a locked device is refused.

### Lock this device to kids

- On the Apple TV, iPhone/iPad, Android and a browser: **Lock to kids profiles** in the profile menu, with the main profile's PIN. The picker then only shows kids profiles, and nothing else can be chosen until the PIN releases the lock.
- Survives restarts and app updates. Kept on the server per sign-in, so the main profile can see and release locked devices in Settings › Devices.
- Needs a sign-in of its own: a browser or a paired device. An app still using the API key itself, or a browser let in by "only away from home", has nothing to lock.

## How it works

### Ratings to an age

- `RatingAges` (Flexarr.Core) maps every certificate the library holds and the common ones beyond it (BBFC, MPAA, US TV, FSK, ACMA, and ages written as numbers: `-16`, `16+`, `MA15+`) to an age: 0 (U), 8 (PG), 12, 15 or 18. Unknown values count as unrated. A podcast without the explicit flag is 0.
- TMDB lookups take the GB certificate first, then US, then any.
- `MediaItem` has `RatingAge` (null when unrated) and `KidsOverride` (none, allow, block); `Library` and `LiveChannel` have `FineForKids`; `Profile` has `MaxRatingAge` and `IsKids`. Migration `KidsProfiles`.
- The context keeps ages right: saving an item works out its age from its rating; seasons, episodes, albums and tracks without a rating of their own take their parent's age and override, after each save that touches them. At startup `ReconcileRatingAgesAsync` works every age out afresh (older databases, or a newer table of ratings).

### Enforcing it

- `LibraryVisibility.ApplyAsync` sets the context's `HiddenLibraryIds`, `MaxRatingAge` and `KidsLibraryIds` per request. The `MediaItem` filter: not hidden, not missing, and under a limit either *allow*, or not *block* and (in a library fine for kids, or `RatingAge <= MaxRatingAge`). Libraries: a limited profile doesn't see music or home-video libraries unless they're fine for kids. Live TV channels: only those fine for kids.
- Round the filter: `HiddenTypesAsync` hides every kind for a limited profile (Discover, Coming Soon, collections' missing films); the event stream and `PlayingAsync` tell playing titles outside the limit as "Something private"; notifications check each profile's limit (new titles, explicit podcast episodes, and no recordings); collection suggestions are cached per limit. `NotForAgeLimited()` turns limited profiles away from requests (whose `/status` says not configured), adding podcasts, and the DVR.
- Leaving: `POST /profiles/{id}/select` from a kids profile to one that isn't takes `parentPin` (the main profile's; choosing the main profile, its `pin`), through `PinGuard`. On a locked sign-in it's refused outright (`kidsLock: true`). Errors carry `parentPin: true` so apps know which PIN to ask for.
- Endpoints: `PUT /profiles/{id}/age-limit` (`maxRatingAge`, `isKids`; main profile only, never on itself; a kids profile needs a limit and a main PIN), `POST /profiles/kids-lock` (`pin`, `profileId`), `POST /profiles/kids-lock/release` (`pin`), `POST /sign-ins/{id}/release-kids-lock`, `PUT /items/{id}/kids` (`override`), `PUT /livetv/channels/{id}/kids` (`fineForKids`), and `fineForKids` on `PUT /libraries/{id}`.

### Compatibility

- The API only grows. `ProfileDto` gains `maxRatingAge`, `isKids`; `/me` gains `isKids`, `maxRatingAge`, `kidsLock`; item details gain `ratingAge`, and `kidsOverride` for the main profile; libraries, channels and sign-ins gain `fineForKids` / `kidsLock`. All optional in FlexarrKit and the Android client ("Absent from servers before 0.56").
- Old apps get the content filter for free. They don't know about kids mode, so they'd show Settings and the picker: the server still refuses what matters (requests, settings, leaving without the PIN), and the guide says to update the apps.

## Phases

1. **Server** ✅: rating mapper, GB-first TMDB certificates, the columns and reconcile, the filters, the audit of what goes round them, requests refused, the PIN to leave, device lock. Tests: `RatingAgesTests`, `AgeLimitTests`, `KidsProfileTests`.
2. **Web** ✅ (screen time, pinned rows and the per-profile subtitle setting for kids wait): Settings › Profiles (age limit, kids switch, PIN prompt for other profiles), Allow/Block on title pages, Fine for kids on libraries, Live TV channel allow list, the kids home, Lock to kids, Settings › Devices showing locked devices.
3. **Apple TV and iPhone/iPad** ✅ (kids home styling waits; the server's Watch again row shows as any row): kids home, the PIN to leave, Lock to kids, hidden Settings and requests, Top Shelf.
4. **Android** ✅ (Lock and Unlock sit in the profile picker): the same.
5. **Guide** ✅ (`docs/guide/profiles.md`, a new section on kids profiles), `docs/architecture.md`, changelog.

## Later

- **Screen time:** a daily allowance and a bedtime window; the current episode finishes and then the app says it's time to stop. Builds on the sleep prompt.
- **Parent's view:** what each child watched this week, with Block on each.
- **Ask a parent:** if requests from kids are wanted after all, a push to the main profile's iPhone with Approve or Deny.
- **Family filter** (#44): muting language and skipping scenes.
