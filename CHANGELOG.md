# Changelog

What's changed in Flexarr, newest first.

## 24 October 2026

### Quicker profile switching
- With two profiles, the profile menu says **Switch to …** (or whoever) and switches straight away, unless that profile has a PIN.

### Spare copies and phone fixes
- Fixed: Remove spare copies skipped titles whose two copies were identical; the copy Sonarr or Radarr use is now kept and the other removed.
- Each copy of a duplicate has **Delete this copy**, for titles where the best copy and the one Sonarr or Radarr use differ (both are kept automatically). Identical copies are labelled as such.
- The removal summary says when titles kept every copy, and why.
- Fixed: on phones, the tab bar jumped up under the header (behind your profile picture) after scrolling; it stays at the bottom, and scrolls sideways if the tabs don't all fit.

### Smaller fixes
- Cards write episodes out in full: "Season 21 | Episode 08", with the episode's name beneath.
- Library health shows when Sonarr or Radarr are already downloading a new copy ("Downloading 45%"), instead of offering another search.

### Library health, redesigned
- A **health score**: the share of films and episodes with no problems, with a breakdown of what's affecting the rest.
- Every problem is a tidy row: poster, title (show, episode number and name for episodes), the file name and what's wrong, with its action on the right.
- **Remove spare copies**, per title or all at once: deletes extra files from disk, always keeping the best copy and the one Sonarr or Radarr use. It asks first and shows progress.

### Fixed: blank screen when playing after an update
- A tab opened before Flexarr was updated could show a blank page when you pressed Play. Updates now keep the previous version's files for a week, and if a page still can't load, Flexarr reloads itself once.

### Changelog
- **Settings › Changelog** lists what's changed, newest first.

### Library health check and smaller fixes
- **Run health check** in Settings › Library health looks at every file: anything no longer on disk, empty files, and files that wouldn't read are listed, each with **Search again**.
- In Latest unwatched rows, a show with one unwatched episode plays it straight from the poster.
- The home banner says "Resume Season 04 | Episode 01", like show pages.

### New logo
- Three stacked bars in white, blue and rose, replacing the marquee F (and the original Plex-like play triangle). It's the favicon, the home-screen icon and the mark beside the wordmark.

### Choose what Home and Browse show
- **Settings › Home and Browse**: switch rows on or off and reorder them, for Home and for film and TV libraries' Browse view. Coming soon can be moved or turned off.

### Subtitles sit higher
- Subtitles are lifted off the bottom edge, and rise above the player controls while they're showing.

### Watched is a toggle
- The tick on a film or episode is a switch: click it to mark watched or unwatched. Unwatched titles show a faint tick on hover.

### Delete titles through Sonarr and Radarr
- **Delete** on a film, show or episode page (main profile only) deletes its files through Radarr or Sonarr, and stops them downloading it again, after asking first.

### Links on cards, like Plex
- The text under a card links to each part: an episode's show, its season and the episode itself.
- A **⋯** menu (or right-click) on every card: play, resume, play from the start, mark watched.
- "New in…" rows are now **Latest unwatched movies** and **Latest unwatched shows**, and leave out what you've seen.
- Show and episode pages say "Season 01 | Episode 10".
- Settings › Connections (was "Sonarr and Radarr") shows whether Seerr is connected.
- Fixed: trailers failed with YouTube error 153.
- Fixed: marking a show watched changed when its already-watched episodes were watched.

### Discover: details for titles you don't have
- Clicking something in Discover opens its page: ratings (TMDB, IMDb, Rotten Tomatoes), trailer, cast, director or creator, studio, and similar titles, with **Request**.
- Fixed: "Highly rated, not yet watched" for TV included shows you'd watched.
- Coming soon starts from yesterday.
- The watched tick is easier to see on any poster.
- Fixed: scrolling with the mouse over a Top 10 row moved the row, not the page.

### Coming soon as a calendar
- A row per day, a show's same-day episodes grouped into one entry.
- Titles you don't have open in Sonarr or Radarr; downloads that need attention say which app and link to its queue.

### Library views
- Libraries open on **Browse** (rows for just that library, led by what you're watching) with **All** one click away.

### Profiles
- Profile pictures, including your Plex Home pictures (Settings › Bring over from Plex).
- Choose which libraries each profile can see. Hidden libraries disappear from that profile everywhere: home, search, Discover, Coming soon and requests.
- The home banner leads with what you were last watching.

### Phones
- Rows scroll on their own, with momentum.

### Apple TV
- A tvOS app: home, libraries, search, show and film pages, and playback with skip intro and next episode.

### Remote access with Tailscale
- Use Flexarr away from home through Tailscale, signed in as your Tailscale account, with no ports opened on your router.
- Only the main profile can change settings.

### Profiles for the household
- Everyone gets their own Continue Watching, watched history and Up Next, with optional PINs.

### Works with Sonarr and Radarr's built-in connection
- Sonarr and Radarr can tell Flexarr about new downloads using their Emby/Jellyfin connection.

### Discover and requests
- Trending and popular titles from Seerr, with one-click requests.

### Player
- Scrubbing previews, subtitles ready in seconds, skip intro and credits.

### Music
- Music libraries with artists, albums and a player that keeps playing as you browse.

### Coming soon, downloads and library health
- What Sonarr and Radarr expect soon, what's downloading, and a library health page (duplicates, unreadable files, missing artwork, SD films to upgrade).

### Plex import
- Bring your watch history and resume points over from Plex Media Server.

### First version
- Scans your film and TV folders, reads artwork and details from Sonarr and Radarr, streams to the browser (transcoding when needed, using the Mac's hardware), and detects intros.
- Runs natively on macOS, and is reachable from other devices on your home network.
