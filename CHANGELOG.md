# Changelog

What's changed in Flexarr, newest first.

## Unreleased

### Fixed: a screen kept a stream that had stopped
- When several screens on one profile started watching at the same moment and the server was full, one could be left with a stream that had already been stopped for another screen, instead of being told why.

## 24 October 2026

### Tidier buttons on a film's or show's page
- The buttons fit on one line again: **Play**, **Trailer** and **Mark as watched**, then **⋯** for the rest: **Play from the start**, **Add to collection**, **Fix match** and **Delete**.

### Fix match
- A wrong match can now be fixed by hand: **Fix match** on a film's or show's page (admins only) searches Sonarr or Radarr. Pick the right one and Flexarr keeps that choice through every refresh.
- Whatever you pick is managed by Sonarr or Radarr. If they don't have it yet, it's added from its existing folder, unmonitored and without a search, so nothing is downloaded. If they already use the folder for a different title, Flexarr says so and leaves it to you to change it there.
- The dialog shows the full path of the film's files (or the show's folder), so you can see what's being matched.
- **Match automatically again** in the same dialog undoes the choice.
- **Settings › Library health** has a new **Check these matches** list: films and shows Sonarr and Radarr don't manage, whose details came from searching for the folder's name. **Missing artwork** rows get **Fix match** too.
- **Ignore** on a row in Check these matches says the match is right: it leaves the list and stays as it is. **Match automatically again** in Fix match undoes it.

### Delete titles Sonarr and Radarr don't manage
- **Delete** used to refuse a film or show that Sonarr or Radarr don't manage. Now Flexarr deletes its video files from disk itself, along with any folders that leaves empty, but never a library's own folder or anything outside it.
- The dialog checks first, says whether Sonarr, Radarr or Flexarr will do the deleting, and lists the files when it's Flexarr.

### Fixed: shows matched to the wrong title by a search
- Shows and films that Sonarr and Radarr didn't have were matched by searching for the folder's name and taking the first result, so *Shōgun* (2024) came up as *Shogun* (1980). The search now prefers a result with exactly the same title, accents included.
- A title found by search was then left alone for a week. Now it's checked against Sonarr and Radarr every day, and straight away when they tell Flexarr they've added it, so it switches to their match.

### Fourth pass: clearer screens, and fixes found by a UX review
- **Apple TV:** if it's signed out from the web, it goes back to the connect screen with a new code instead of getting stuck on an error. If it can't find your server it tells you what to check, a replaced connect code is marked **New code**, and posters the server was too busy for appear a few seconds later.
- **Another screen took over:** Flexarr says so plainly and offers **Watch here instead**, which tells you the other screen will stop. When the server is busy, the message says so in plain words and **Try again** no longer stops anyone else.
- **PIN pad:** it has a **Delete** key, Escape takes you back, and on a phone it no longer sits under the phone's keyboard. It shows "Checking…", counts down after too many wrong PINs, and for the main profile offers the API key instead.
- **Profiles:** editing one profile and then another no longer saves the first one's settings (such as hidden libraries) onto the second. The form asks for a new PIN twice, names each colour and gives new profiles a colour of their own.
- **Getting back to the main profile:** Settings on another profile has a **Switch to the main profile** button that brings you back. Any profile can sign a browser out from the profile menu, and the sign-in page explains where to get the key in plain words.
- **Server settings:** making a new key says how many browsers and TVs were signed out, with **Copy** and **Hide**. Signed-in browsers and TVs can be renamed, repeats are grouped, and you can sign out every other one at once.
- **Connecting a TV:** the code is shown large, **Don't allow** makes the TV show a new code, and another profile gets a button to the main profile and back.
- **Library health:** folders on hold use clearer wording ("Remove 4 films from Films?"), Escape closes the dialog, and the score card mentions them. The score no longer drops for missing artwork when Radarr or Sonarr isn't connected.
- **Libraries:** going back puts keyboard focus on the title you opened. The A–Z letters are bigger, grey out letters with no titles and show "Loading…" for a far-off letter, and phones get a **Jump to…** picker.
- **Security:** connecting more TVs or signing in more browsers no longer gives more guesses at the main profile's PIN, at most ten TVs can be connected an hour, and a browser signed in with the main PIN over Tailscale only works for that Tailscale account.
- **Reliability:** an interrupted deploy no longer leaves a server that won't start, one broken artwork download no longer stops a metadata refresh, and posters that can't be found come back straight away.
- On phones the bottom tab bar fades at the edge to show it scrolls, and Home says "Latest unwatched films".

### Third pass: sign-in, streams and libraries
- Away from home over Tailscale, you can now get to Settings by choosing the main profile with its PIN, from **Switch profile** or from the Settings page. That browser then stays signed in, shows under signed-in browsers, can be signed out there, and is signed out after two weeks unused or when the main PIN changes. Browsers no longer keep a reusable key for the main profile; if an earlier version stored one, you'll be asked for the PIN again (or tap **Not now**).
- Changing or removing the main profile's PIN now asks for the current PIN. If you've forgotten it, you can use the API key instead.
- Wrong-PIN lockouts are now per device, so someone else's guesses on a phone or tablet sharing your Tailscale account don't lock you out. Repeated guessing over days is locked out sooner.
- Several screens on the same profile can now watch at once while the server has room. When it's full and someone starts another, the screen that has to give way says why, and screens no longer knock each other off in turn.
- Moving a film or show to a new folder no longer slows scans: its watch history and collections move across once, in a fraction of a second.
- When Radarr or Sonarr delete one title while a library drive looks disconnected, only that title is hidden; the rest of the folder stays on hold. **Confirm removal** only approves the numbers you saw, and asks you to check again if a scan has found something different.
- A newly imported film or show gets its details from Radarr or Sonarr straight away, without fetching the whole library, and Coming soon and Downloads update right after a download finishes.
- Leaving a library's full list before it finished loading, then pressing Back, now loads it again instead of showing a blank page. A small library shows titles added or removed while you were away, a deleted title doesn't come back, and keyboard focus returns to the title you opened.
- On Apple TV, coming back to a library you'd scrolled far into (more than 500 titles) keeps the whole list and your place. Pressing Back while a TV finishes connecting no longer leaves an unused sign-in behind.
- Scrolling quickly past lots of new posters no longer makes the ones you stop on wait behind the rest, and a poster the server was too busy for loads a few seconds later.
- Deploys now keep the server's ASP.NET Core runtime on the latest security patch instead of the version first installed.

### Sign-in and profiles, second pass
- Wrong PIN guesses lock out only the device guessing. Nobody can lock you out of the main profile anymore.
- Over Tailscale, changing settings now takes the main profile's PIN, or signing that browser in with the API key, once. Watching works as before.
- Showing or replacing the API key asks for the main profile's PIN (or a confirmation if it has none). Replacing it can also sign out every other browser and TV, and new sign-ins are marked **New**.
- You can replace the webhook secret from **Settings › Server**.
- If a browser's chosen profile stops being valid (after a key or PIN change), Flexarr asks who's watching again instead of quietly using the main profile.
- Pairing a TV is safer, and the approval screen shows its code and address prominently.
- A damaged or unreadable list of sign-ins no longer stops browsers and TVs signing in, and long-unused browsers are cleaned up.
- The Apple TV keeps its saved key if the Keychain can't be written, and retries connecting right after you approve it.
- Browsers signed in by versions before per-device sign-ins keep working for 30 days, then need to sign in again.
- Settings explains what "Only away from home" trusts, and what a reverse proxy on the Mac must send. A new config option, `loopbackNeedsForwarding`, makes such a proxy safe.
- Artwork for things not in your library (Coming soon, downloads, requests, collections) can now only be loaded through links Flexarr hands out, so nobody else can use your server to download images.

### Library safety, second pass
- **Library health** now lists folders Flexarr is keeping on hold because they looked unmounted or cleared out. If you really emptied a folder, choose **Confirm removal** and its titles go.
- A folder where most files vanished at once now goes through by itself after a day of scans finding the same thing. An empty folder still waits for you to confirm, or for Radarr/Sonarr to say they deleted it.
- Watch history and collections now follow a film or show when Radarr or Sonarr rename its folder, or when it comes back up to 30 days after it was removed.
- Deleting an episode no longer takes a briefly missing sibling episode, and its watch history, with it.
- A film whose file is mid-upgrade no longer drops out of its franchise or shows up as one to request.
- A malformed webhook can no longer stop the server.

### Playback and speed, second pass
- Two TVs or browsers on the same profile can now watch the same title at once without stopping each other.
- When the server is already converting as many videos as it allows, the web player and Apple TV say the server is busy instead of retrying and then reporting a stopped stream. One profile can no longer take every conversion slot: starting another ends its oldest one.
- The Apple TV's Home, library and detail screens pick up changes made elsewhere (watching on the web, a new download) after a minute, or when you come back to the app, and keep your place.
- Going Back from a title to a library's full list returns you to the same spot, even after marking something watched or watching it, and the ticks are up to date. Searching or re-sorting it no longer jumps the page, and letter jumps land on the right row.
- A show's page opens faster, Settings › Libraries no longer reloads every 2 seconds during a scan, and a request the server never answers fails after 30 seconds instead of loading forever.
- A just-downloaded episode or film no longer shows as "on its way" in Coming soon and Downloads for a couple of minutes, and collection suggestions update straight away when a library is hidden from a profile.
- Artwork scrolled past on the Apple TV keeps loading in the background, and a slow artwork site can no longer hold up a metadata refresh.
- Installing on a Mac now checks the .NET runtime download against the checksum Microsoft publishes.

### Sign-ins of your own, for every browser and TV
- Each browser and Apple TV now has a sign-in of its own. **Settings › Server** lists them, with **Sign out** for any one: a lost TV or a shared laptop, without changing the API key. Browsers you're already signed in on stay signed in.
- Apple TVs no longer keep the server's API key. One that connects, or was set up before, swaps it for its own token. Signing out on the TV ends that token on the server too.
- **Make a new key** in Settings › Server replaces the API key without signing out your browsers and TVs.
- Only the main profile can change settings, connect TVs or delete media, even on a browser or TV someone signed in earlier. To change settings again, switch back to the main profile (with its PIN).
- Wrong PINs lock the profile for a while, longer each time.
- TVs can only ask to connect from your home network or over Tailscale. The approval page shows where the request came from and warns if it isn't your home network.
- Sonarr, Radarr and Lidarr webhooks use their own secret instead of the API key (Settings › Server shows the new addresses). Old webhook addresses keep working.
- Other websites can no longer use your browser to change things on Flexarr.
- On a reverse proxy in Docker or on another computer, visitors now have to sign in instead of counting as your home network. List the proxy in `trustedProxies` in config.json.
- Subtitle names taken from inside media files can no longer tamper with a stream.

### Your library and watch history are safer
- If a library drive or network share hasn't mounted when Flexarr scans, nothing is removed: your library and every profile's watch history are kept until it comes back.
- Titles whose files disappear are hidden rather than deleted, and come back with their watch history and collections if the files return within a day.
- Upgrading a film or episode in Radarr or Sonarr no longer resets its watched status.
- Folders with accented names no longer show up as a second copy of a series after a Sonarr import.
- New episodes get their real titles and descriptions as soon as Sonarr imports them.
- A slow or unresponsive Radarr, Sonarr or artwork server can no longer shut Flexarr down.

### Playback
- Resuming a video after a long pause now carries on where you left off, instead of failing.
- On Apple TV, if a stream stops (a server restart, say), playback picks back up where it was instead of closing the player.
- The server converts at most 3 videos at once by default (Transcoding › MaxConcurrentTranscodes). Beyond that, the player says the server is busy instead of slowing everyone down.
- Switching audio tracks or retrying no longer leaves the old conversion running in the background.
- Up Next no longer jumps to the next special after you watch one mid-season; it carries on with the regular episodes. Daily shows with two episodes on the same day no longer skip the second.
- The player loads faster and runs more smoothly, and switching titles quickly can no longer start the wrong one.

### Faster everywhere
- Home and library pages load faster, and a slow or offline Sonarr/Radarr no longer holds Home up for long.
- The Collections page opens much faster with many film series.
- The library's All view keeps your place: scans and marking things watched no longer throw you back to the first 200 titles, and going back to it returns you to where you were scrolled. When a scan adds titles to a big library, a **Refresh** button appears.
- Jumping to a letter in a large library is much faster.
- Pages load faster when you go back and forth, download less data, and Flexarr stops checking the server from tabs you aren't looking at.
- Featured backdrops and posters download at the size your screen needs, and the artwork cache keeps itself under 4 GB.
- Scans after an import are faster, and files that can't be read are no longer re-checked on every scan.
- On Apple TV, returning from a title's page keeps your place in the library instead of jumping back to the top.

### Profiles on Apple TV
- When your household has more than one profile, the Apple TV asks **Who's watching?** the first time it connects, and remembers the answer.
- The current profile's picture sits in the top right corner, level with the tab bar (press right from the last tab to reach it). Open it to switch; with two profiles the other person is already highlighted, so it's one press.
- A profile with a PIN asks for it on an on-screen keypad.

### Collections
- A new **Collections** page. Make your own collections and add films and shows from a card's ⋯ menu or a title's page (**Add to collection**). Drag posters to put them in order.
- **Smart collections** fill themselves from rules (type, genre, years or "this year", rating, watched or not, recently added, words in the title or description) and keep up to date, e.g. *Top 10 shows this year*.
- **Film series** come from Radarr automatically: every series with two or more of its films in your library (James Bond, Back to the Future…), in release order. Films you don't have yet are shown greyed out, with **Request**. Hide a series you don't want and it stays hidden.
- **Suggestions from your library**: directors and actors you have plenty of, genres by decade, the season (Halloween, Christmas) and the year's best. One click adds one.
- Collections are for the whole household (made by the main profile) or just for you. Profiles kept out of a library don't see its titles in any collection.
- **Show on Home** puts a collection on Home as a row, which you can move in **Settings › Home**.

### Fixed: "The stream stopped" after a server restart
- A converted stream lives on the server, so restarting or upgrading the server mid-film ended it, and the next seek (Skip intro, say) showed "The stream stopped". The web player now asks for a fresh stream from the same spot and carries on, retrying for up to 15 seconds while the server comes back. It only says the stream stopped if it stops again within a minute.

### Security fixes
- The web app no longer keeps your API key in the browser. Signing in now gets you a session cookie that scripts can't read. A key saved by an earlier version is swapped for one on your next visit, and changing the API key signs every browser out. A wrong key now says so.
- Plex import only reads Plex's own database file (`com.plexapp.plugins.library.db`) when you give it a path, not any file on the server.

### Connect an Apple TV without typing
- The server announces itself on your home network, and the Apple TV lists it: pick it with the remote.
- The TV then shows a QR code and a short code. Scan it with your phone (or enter the code in **Settings › Connect a TV** on any device signed in as the main profile) and tap **Allow**; the TV signs itself in. Codes last ten minutes.
- Typing an address and API key is still there, under **Enter an address instead**.
- A phone that's never signed in to Flexarr, opening a TV's QR code, is told why it's asked for the API key (once), and how to approve from a signed-in computer instead.
- The Apple TV app has the new logo: the icon is the three bars, and the Top Shelf shows them beside the name.

### Apple TV install
- `make apple-install` builds the Apple TV app and installs it on a paired Apple TV in one step (see the README). With a free Apple ID, run it again every 7 days.

### Better TV file name reading (thanks, LetMeSleep8h)
- Anime-style absolute episode numbers are understood ("One Piece - 1015", "Cowboy Bebop - 26").
- Scene-style names spelled with dots get readable episode titles, and titles that really contain dots ("S.O.B.s", "WUPHF.com") are no longer dropped.

### Fixed: Discover search only found films
- Search, Discover, Coming soon and requests hid every new show from a profile kept out of *any* TV library (yours hides one). They now hide a kind of title only when the profile can't see any library of that kind.

### Search everything on Discover
- A search box on Discover finds any film or show, not just what you have. Titles in your library are marked and open as usual; anything else opens its details with **Request**.
- Results are split into Shows and Films, and searching waits until you pause typing.
- The profile menu closes when you click anywhere else (or press Escape).

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
