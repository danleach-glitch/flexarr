# Watchlist and Trakt: plan

A plan, not built yet. A watchlist of films and shows to watch later, per profile, whether the server has them or not ([#39](https://github.com/danleach-glitch/flexarr/discussions/39)); then Trakt keeps it, and watch history, in step with everything else people use ([#128](https://github.com/danleach-glitch/flexarr/discussions/128)).

What sets it apart: Flexarr knows what the household's own server has, so the watchlist can follow a title the whole way. You want it, it's requested, it's downloading, it's ready (and you're told), you watch it, and it leaves the list.

Decided so far:

- **The watchlist is Flexarr's own.** Trakt mirrors it. Seerr's watchlist isn't used: Flexarr talks to Seerr with one key, so it can't tell profiles apart.
- **Auto-request is a switch per profile, off by default.** When it's off, a missing title shows a one-tap Request.
- **No link between profiles and Seerr users for now.** Requests still go in as the admin.
- **Trakt history goes both ways, and watched wins.** A sync never marks anything unwatched, and the newest resume point wins.
- **Flexarr registers one Trakt app.** Its credentials go into releases. Since July 2026, Trakt only lets VIP members create API apps, so asking each admin to make their own isn't realistic. People who build from source can enter their own.
- **Two branches.** `feat/watchlist` lands first; Trakt follows on `integration/trakt`.

## What people get

Each line lists the phase that delivers it.

### Watchlist

- **An Add to watchlist button wherever a title appears:** title pages (in the library or not), Discover, search, person pages, recommendations and Coming soon. Long-press on posters too. *(1)*
- **Siri, Shortcuts, MCP and Home Assistant** can add or remove titles: "add Dune to my watchlist". *(1)*
- **Four statuses:** *(1)*
  - **Ready:** in the library.
  - **On its way:** requested through Flexarr, or Seerr says it's requested or being fetched. (Download progress later: the \*arr queue doesn't carry TMDB ids yet.)
  - **Coming:** has a release date that hasn't arrived.
  - **Not available:** can be requested.
- **A Watchlist page** in the sidebar and as a TV tab. It filters by status and by films or shows, and sorts by date added, release date or title. *(1)*
- **A Home row, "Ready from your watchlist",** that holds only titles you can play now. It's in the layout straight after Continue watching, and only appears once something on the list is ready. *(1)*
- **Titles leave on their own:** a film when it's watched, a show when you start it (Continue Watching takes over from there). You can still remove anything by hand. *(1)*
- **An arrival notification** ("From your watchlist") when a title on your watchlist is ready, built on the existing request notifications and following the phone's Requests switch, so there's no new setting. A title you also requested gets "Your request is ready" only. *(1)*

### Requests from the watchlist

- **Auto-request:** a profile switch in the admin's profile editor, off by default. When it's on, adding a missing title requests it through Seerr, sent to that profile's chosen library. *(1)*
- **Guards, whether or not the switch is on:** *(1)*
  - Only titles a person adds by hand are requested. Titles that arrive from a Trakt sync or import never are; they show as Not available with a Request button.
  - A profile that can't see films (or shows) can't request them through the watchlist either (`LibraryVisibility`).
  - Taking a title off the watchlist doesn't cancel its request.
  - Seerr refusing (already requested, over quota) leaves the title on the list with its real status, not an error.

### Trakt

- **Connect per profile** in the person's own Settings, using Trakt's device code: the screen shows a code and a QR code for trakt.tv/activate, so nothing is typed on a TV. *(2)*
- **Live scrobbling:** Flexarr tells Trakt when you start, pause and stop, so Trakt shows "watching now". Trakt marks a title watched at 80%, and Flexarr's own watched point should match it. *(2)*
- **History import on connect, with a preview first:** for example, "Trakt has 1,240 watched episodes; 312 are in your library; 41 are newer than Flexarr's". Then you confirm. *(2)*
- **Two-way sync:** *(2)*
  - Flexarr's changes go to Trakt straight away, through a queue that retries.
  - Trakt's changes come back by polling `/sync/last_activities` every few minutes, which costs one request when nothing has changed.
  - Watched always wins, and nothing is ever marked unwatched. The newest resume point wins.
- **Watchlist sync both ways.** Removing a title in one place removes it in the other. *(2)*
- **Resume across apps** through Trakt's playback progress (`/sync/playback`): start a film in another Trakt-connected app and pick it up in Flexarr. *(2)*
- **Privacy:** sync is per profile, never on unless that person connects, and Disconnect removes the token. *(2)*
- **Later:** *(3)*
  - Trakt lists as smart collections.
  - Trakt recommendations as a Discover row.
  - Ratings, which need a rating control first.
  - Simkl, which has the same shape.
  - MyAnimeList and AniList, which need an id mapping.

## Data model

- **`WatchlistEntry`:** profile, kind (Movie or Series), TMDB id (also TVDB and IMDb ids when known), title, year, poster, `AddedAt`, `Source` (`Flexarr` or `Trakt`), and the library item once one matches. Unique on profile + kind + TMDB id. It's keyed by external ids rather than library items, so titles the server doesn't have can still go on it. Library items match it when they arrive through the existing scan and webhook paths, the same way `WatchTombstone` and `MediaRequest` do.
- **`Profile.AutoRequestWatchlist`:** a bool, false by default.
- **`TraktConnection`:** profile, access and refresh tokens, expiry, Trakt username, `LastActivities` (to compare against), and status (connected, needs sign-in again, or watchlist full).
- **`TraktOutbox`:** queued scrobbles and sync changes, with attempts and the next try, so nothing is lost while Trakt or the network is down.
- **Tokens live in the database, so backups carry them.** Disconnect and profile deletion remove them.

## API

New endpoints only; nothing changes shape.

- `GET/POST /api/v1/watchlist`, `DELETE /api/v1/watchlist/{id}` or `DELETE /api/v1/watchlist?libraryItemId=…|mediaType=…&tmdbId=…`, `GET /api/v1/watchlist/contains?…` for the button's state, and the Home row (`watchlist`) through the existing layout.
- `PUT /api/v1/profiles/{id}/watchlist-requests` (admin): the auto-request switch; `ProfileDto.autoRequestWatchlist`.
- MCP tools `watchlist`, `add_to_watchlist` (a library id, or a title looked up through Seerr) and `remove_from_watchlist`.
- `POST /api/v1/trakt/connect`, which returns the device code and QR link; `GET /api/v1/trakt/status`; `POST /api/v1/trakt/import`, as a preview and then a confirm; `DELETE /api/v1/trakt`.
- Item and external-title detail DTOs gain an optional `onWatchlist`. Apps treat it as optional ("Absent from servers before 0.56").

## Trakt specifics

- **Tokens:** access tokens last 24 hours (since March 2025), so Flexarr refreshes them from `expires_in` instead of on a fixed schedule. A refresh that fails marks the connection *needs sign-in again*, and the person's Settings says so.
- **Device code:** it expires after 10 minutes. Flexarr polls at Trakt's `interval`, and the server does the polling, so the app can be closed.
- **Rate limits:** about 1 write per second and 1,000 reads per 5 minutes. History goes in batches.
- **Free accounts:** the watchlist is capped (100 in 2025; reportedly 250 since 2026). When Trakt says the list is full, that title stays in Flexarr, and Settings shows it plainly.
- **Matching:** films by TMDB id, then IMDb. Episodes by show (TVDB, TMDB, then IMDb) plus season and episode number. Specials and absolute numbering can disagree; anything unmatched is counted in the preview, never guessed at.
- **Credentials:** the client id and secret come from CI secrets at release time and never appear in the repo. A setting overrides them with your own app. Flexarr's app is registered under Dan's Trakt account, which needs VIP.

## Apps

All four in phase 1: web, iPhone and iPad, Apple TV, Android. On the Apple TV and Android, Trakt connects by code and QR. The Mac app needs nothing extra, since all of this is outgoing HTTP from the server.

## Phases

1. **Watchlist** (`feat/watchlist`, S–M): the model, endpoints, the button everywhere, the page, the Home row, statuses, arrival notifications, auto-request with its guards, Siri and MCP, on all four apps.
2. **Trakt** (`integration/trakt`, M): connect, scrobbling, history import with a preview, two-way sync, watchlist sync, resume across apps.
3. **Later:** lists, recommendations, ratings, Simkl, anime services.

## Testing

- Unit tests:
  - the merge rules: watched wins, nothing is un-watched, newest resume point wins;
  - matching;
  - the auto-request guards;
  - an outbox that retries;
  - refresh after 24 hours.
- A fake Trakt server in tests. A real test Trakt account for end-to-end runs, never the household's.
- On the Mini, never place a real Seerr request while testing (auto-request stays off on real profiles).

## Risks and open questions

- **Trakt's API terms keep changing** (the VIP requirement, token lifetime, list caps). Keep the Trakt client small, and make the connection's status visible.
- **The client secret ships in releases** and can be extracted. It can't reach any account unless that person approves it; rotate it if it leaks.
- **When should a show leave the watchlist:** on starting it, or on finishing it? Plan: on starting it.
- **Is the Home row on by default?** Yes: it's empty (so hidden) until something on the list is ready.
