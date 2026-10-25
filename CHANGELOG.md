# Changelog

What's changed in Flexarr, newest first. Changes not yet in a version are under **Unreleased**.

## 0.21.0 · 25 October 2026

### Music on the web: a queue, playlists, lyrics and discovery
- A Spotify-style queue: Play next and Add to queue on tracks and albums, a queue panel above the now-playing bar with drag or arrow reordering, remove, clear and skip-to; the bar stays with you on music pages, links its cover, title and artist to their pages, and the queue, track and position survive a reload (coming back paused).
- Playlists, per profile: a Playlists page and a page for each playlist with Play, Shuffle, drag-to-reorder, rename and delete, a cover, Add to playlist in track, album and artist menus, and a Your playlists row on Home.
- Lyrics: a Lyrics panel under an album's tracklist for the track playing, from .lrc or .txt sidecars or the file's own tags; timed lyrics highlight the line being sung and clicking a line seeks there.
- Discovery: Recently played, Most played and More from artists you play rows on Home (movable or off in Settings, like any row) and in a music library's Browse view, with New albums; Artist radio, a station of about fifty tracks seeded from an artist and the artists sharing their genres; search finds albums and tracks, with a play button on each track.
- A play button appears on an artist's or album's artwork on hover, playing their tracks.

### Siri: "Play the next episode of Curb Your Enthusiasm in Flexarr"
- On iPhone and iPad, Siri plays a film or the next episode of a show by name ("play the next *Traitors* in Flexarr", "watch *Heat* in Flexarr"), picks up Continue Watching ("continue watching in Flexarr"), pauses, resumes and stops ("pause Flexarr"), and searches ("search for *Fargo* in Flexarr"). The app's name has to be in the phrase. Siri learns your library's titles when the app signs in.
- Settings › Siri on iPhone and iPad chooses where Siri plays: this device, or an Apple TV with Flexarr open; or name a device as you ask ("… on the living room TV"). An Apple TV that isn't listening gets the command when Flexarr is opened on it within two minutes.
- The same actions are in the Shortcuts app, for automations (wake the Apple TV, open Flexarr on it, play).
- The Apple TV app carries the same intents for the Siri Remote, though whether tvOS passes a third-party app such requests is still to be confirmed on a real Apple TV.

### Play on another device, and an assistant's way in (MCP)
- An Apple TV, iPhone or iPad with Flexarr open can be told what to play from elsewhere: the server lists the devices signed in (`GET /api/v1/devices`, with whether each is listening and what it's playing) and takes `POST /api/v1/devices/{id}/play` with a title's id (a show's plays the episode that device's profile is up to), and `pause`, `resume` and `stop`. A command to a device that isn't listening waits two minutes for Flexarr to be opened on it.
- Flexarr is an MCP server at `/api/v1/mcp` (sign in with the API key as `X-Api-Key`, or `?apikey=`): an assistant such as Claude can search the library, see Continue Watching and what's playing on every screen, play a title on a device, pause, resume and stop it, and mark things watched.

## 0.20.1 · 25 October 2026

### HDR10, HLG and Dolby Vision 7 and 8 play as they are on Apple TV
- A copied HDR stream was refused by the Apple TV at the playlist ("unsupported URL"), so HDR10, HLG and Dolby Vision 7 and 8 were converted to SDR unless Play files directly was on. Two things were wrong on the server's side: the HEVC codec string in the playlist was written from a template rather than read off the file's own configuration record, and that record was copied into the stream with its SEI messages (x265's settings blob among them), which the Apple TV won't take for HDR. The string is now derived from the record (with the declared level capped at 5.1, which the Apple TV also insists on) and the record is cut down to its parameter sets, so a display showing HDR10 or HLG gets the original picture: HDR10 and HLG as they are, Dolby Vision 8.1 with its metadata for a Dolby Vision display or as HDR10 otherwise, and Dolby Vision 7 as its HDR10 base layer.

### Music from Lidarr
- With Lidarr connected (Settings › Connections), artists and albums take their names, years, overviews and artwork from it: the artist's own picture rather than an album cover, and every album's cover even when the files carry none. Tags remain the fallback, and the tags' spelling is kept as the original title.
- On the web, an artist's page has Play and Shuffle for their whole catalogue (albums oldest first, then discs and tracks); the album page's Shuffle is a proper shuffle.

### Settings
- **Play files directly** is on by default on Apple TV, iPhone and iPad (anyone who turned it off stays off). Its wording and the Apple TV Settings page are tidied: a Server card and a Playback card, with each explanation under its control.
- The skip amount is no longer a setting on Apple TV (clicking the remote's edges always skips 30 seconds); iPhone and iPad keep the choice.

### Fixed
- On Apple TV, the ring around the profile picture in the tab bar sat too high on the picture.
- On Apple TV, opening "Who's watching?" from the tab bar with two profiles left the remote nowhere to move, since the profile already watching was disabled; it's a stop again, and choosing it just closes the picker.
- Skipping back or forward moved the picture but the timeline's head waited for the seek to land, and a second click measured from where the picture still was; the head now moves at once and clicks add up.

## 0.20.0 · 25 October 2026

### Our own player on Apple TV, iPhone and iPad
- Flexarr draws its own player now instead of Apple's: previous and next episode buttons (shuffle included), skip back and forward by a chosen amount (30 seconds unless Settings says 10 or 15), play/pause, a timeline with the intro and credits shaded and preview thumbnails while scrubbing, and menus for audio, subtitles (with timing and size inside) and playback speed. The title, episode and what's playing (resolution, sound, original or converted) sit in the corners; the controls sit on clear glass and fade after a few seconds of playing.
- On Apple TV, the timeline takes the remote's swipes, clicking the remote's edges skips, holding an edge scans at 2× (a minute of video a second; each click doubles it to 4, 8 and 16×, the other edge turns it round, Select lands), Menu hides the controls and then closes, and the Skip Intro / Next Episode pill takes focus when it appears. On iPhone and iPad, a tap shows or hides the controls, two taps at the edges skip 10 seconds, AirPlay and Picture in Picture are in the top corner, and the lock screen, Control Center and a paired watch can play, pause, skip and move between episodes.
- The audio track can be changed on a server stream too (the stream restarts where it was with the new track).
- **Play files directly** now plays every file on the device: what the hardware can't decode (AV1, VC-1, MPEG-2, XviD and the like) is decoded in software, with the file's subtitles drawn by Flexarr. It no longer falls back to the server for those.
- The server tells the apps which episode comes before the one playing, for the Previous Episode button.

### Fixed
- The AirPlay button on iPhone and iPad showed the audio AirPlay symbol rather than the video one.
- On the Apple TV Home screen, pressing down from the tab bar went to a poster in Continue Watching rather than Play on the featured title.

## 0.19.0 · 26 October 2026

### Play files directly on Apple TV, iPhone and iPad (experimental)
- A new switch in the app's Settings, **Play files directly**, plays each file as it is on the device instead of a stream from the server: the device demuxes it and decodes in hardware, so every container plays with its picture untouched, HDR10, HLG and Dolby Vision included, and the server does nothing but send bytes. Built on [AetherEngine](https://github.com/superuser404notfound/AetherEngine).
- Apple's player stays over the top: the same scrubbing, menus, Skip Intro, countdown and progress. The file's audio tracks and its picture subtitles (PGS, DVB) get menus of their own in the transport bar on Apple TV, with picture subtitles drawn over the video.
- **Lossless surround**, a second switch, sends TrueHD, DTS-HD and FLAC soundtracks as multichannel PCM over HDMI for a receiver or eARC soundbar that takes it. Off, they're sent as Dolby Digital Plus, which anything plays.
- Files the device can't decode in hardware (AVI, AV1 on older models, interlaced broadcasts) are converted by the server as before, and so is anything the engine can't open. Remote viewing with a quality limit and downloads are unaffected.
- Off by default. The apps are about 11 MB bigger.

### Fixed: AVI and other old formats wouldn't convert on a Mac
- With hardware acceleration on a Mac, converting a file whose video the GPU can't decode (AVI's MPEG-4 and XviD, VC-1, and the like) failed before the first frame, so the player sat on its spinner. Those files are now decoded in software and still encoded in hardware.

## 0.18.1 · 25 October 2026

### Fixed: the next-episode countdown during an episode's opening titles
- An episode whose chapters name its opening titles "Credits" (Peacemaker, Luther) was taken to be ending there, so with Skip credits on, the countdown to the next episode started during the intro. Opening credits early in an episode are now an intro to skip; only credits near the end move on to the next episode. Affected episodes are re-analysed.

## 0.18.0 · 25 October 2026

### Apple TV, iPhone and iPad play the original video
- MKV files with H.264 or HEVC video are no longer re-encoded for Apple devices: the server repackages the original picture into the stream, cut at the file's own keyframes, with almost no work for the machine running Flexarr. Starting and seeking stay instant.
- Dolby Vision profile 5 plays as Dolby Vision on a Dolby Vision display. HDR10, HLG and Dolby Vision profiles 7 and 8 are still converted for now: Apple TV refuses a copied HDR10 stream for a reason still being worked out.
- MKV keyframe indexes are read in milliseconds; other files are indexed in the background and play repackaged from the next time.

### Fixed: streams that wouldn't start while another was paused
- On a Mac, a stream that had got well ahead of its viewer was paused, and while it was, nothing else could start: another screen's stream, the next episode getting ready, a download's conversion, or your own stream after a jump. The player sat on its spinner or said "Couldn't play this". ffmpeg now paces itself (three minutes ahead, then real time) and is never paused.

## 0.17.1 · 25 October 2026

### Changed: Top Shelf on Apple TV
- The Top Shelf shows Continue Watching as a row of posters, the show's for an episode, each with a bar for how far through it you are and the episode underneath. Select one to open it, or press Play to carry on.

## 0.17.0 · 25 October 2026

### Dolby Atmos and lossless sound on Apple TV, iPhone and iPad
- Films with Dolby Atmos in Dolby Digital Plus now reach Apple TV with their Atmos intact and signalled, ready for an Atmos receiver, soundbar or HomePods.
- TrueHD, DTS-HD Master Audio and PCM soundtracks, which Apple TV can't play as they are, now arrive as lossless FLAC, up to 7.1, instead of being squeezed into 5.1 AAC. Plain DTS is decoded with nothing more lost on the way.
- Over a connection with a quality limit (watching away from home, say), the sound is compressed as before.
- This needs the Flexarr 0.17 app; older apps and the web player stream as they did.

### Top Shelf on Apple TV
- With Flexarr in the top row of the Apple TV Home screen, the Top Shelf shows what you're watching: big artwork for each title in Continue Watching, ready to play or open.

## 0.16.0 · 25 October 2026

### Subtitle timing and size
- Subtitles that come too early or too late can be moved to match the speech: in the web player's subtitles menu (a quarter of a second at a time), or on Apple TV from the subtitles button in the player's bar. Each film or episode remembers its own.
- Choose how big subtitles are, **Small**, **Standard**, **Large** or **Extra large**: in Settings › Playback on the web, Settings on iPhone and iPad, or the player on the web and Apple TV. Each profile has its own, on every device.

### Changed on iPhone and iPad
- Opening Search puts you straight in the search field with the keyboard up.

## 0.15.3 · 25 October 2026

### Fixed on iPhone and iPad
- Search shows its search field straight away, without pulling the page down first.

## 0.15.2 · 25 October 2026

### Changed on iPhone and iPad
- **Download** starts straight away, with no question about size. Choose the size once in Settings › Downloads: **Automatic (720p)**, **Original** or **Small (480p)**. A title whose original won't play on the phone comes at 1080p instead.
- While a download's on its way, its button shows how far it's got; tap it to stop or remove the download.
- On a title's page, **Play** or **Resume** has a line to itself, so it no longer squeezes onto two lines. Start over, Download, Watched and Show sit underneath, each with its name.

## 0.15.1 · 25 October 2026

### Fixed: resuming on Apple TV took ages
- Picking up where you left off on Apple TV could sit on the loading spinner for half a minute or more, because the stream was got ready from the beginning first, then again from where you'd got to. It now starts from where you left off straight away (a couple of seconds). iPhone and iPad resume faster too.

## 0.15.0 · 25 October 2026

### Downloads on iPhone and iPad
- Download films and episodes to watch with no connection: tap **Download** on a title's page and choose a size, **Original**, **High (1080p)**, **Medium (720p)** or **Small (480p)**, each with about how much space it takes.
- Your server gets each download ready first, quietly, and never while someone's watching. Downloads carry on with the app closed and pick up where they left off.
- A new **Downloads** tab plays them anywhere, with Skip Intro and the countdown to the next episode. What you watch offline is sent to your server when you're back online.
- Once an episode's downloaded, the next one downloads too, on Wi-Fi. Episodes are deleted a day after you finish them. Turn this off in Settings › Downloads.
- Signing out deletes this device's downloads.

## 0.14.1 · 25 October 2026

### Fixed: Settings stuck on "Loading…"
- When the server couldn't be reached (while it restarts, say), Settings › Playback, Connections and Server sat on "Loading…" for good. They now say what went wrong, with **Try again**.

## 0.14.0 · 25 October 2026

### The next episode starts straight away
- While an episode's credits play, Flexarr gets the next one ready, so when the countdown ends (or you press Next) it starts at once instead of after a few seconds of loading. On the web, iPhone, iPad and Apple TV.
- It never takes a screen away from anyone: if the server is already busy playing elsewhere, the next episode just starts the usual way.

## 0.13.1 · 24 October 2026

### Changed
- The countdown says **Next episode in…** when shuffling too, rather than calling it a random one.

### Fixed: a blank card in Continue Watching
- When an episode's still couldn't be fetched, its card stayed blank. It now shows the show's backdrop instead.

## 0.13.0 · 24 October 2026

### Getting ready for the App Store
- The iPhone and iPad app is on its way to the App Store, with a privacy policy at flexarr.io/privacy: Flexarr collects no data.
- A server that only listens on this computer (for example, one published with Tailscale Funnel) no longer shows up in other devices' lists of servers on the home network, where it couldn't be reached anyway.

## 0.13.0 · 24 October 2026

### Shuffle a show
- **Shuffle** on a show's page plays a random episode, then another, and another, until you stop. On the web and on iPhone and iPad.

### Auto-play, and skipping the boring bits
- When an episode finishes, a five-second countdown starts the next one. **Cancel** stops it.
- New choices for each profile: **Auto-play the next episode** (on to start with), **Skip intros**, **Skip recaps** and **Skip credits**. Skip credits moves on to the next episode as the credits start.
- Change them in **Settings › Playback**, in the player's quality menu on the web, or in **Settings** on iPhone and iPad. They follow the profile to every device, the Apple TV included.

## 0.12.1 · 24 October 2026

### Fixed: subtitles coming back on after skipping ahead
- On iPhone, iPad and Apple TV, subtitles you've turned off stay off when you drag along the timeline. Before, the player turned them back on each time it landed somewhere new, especially with the sound muted.
- Subtitles start the way your profile likes them (or off), rather than however the device guesses from its language. Turning them off carries on to the next episode.

### iPhone and iPad
- The Flexarr icon now stands in for the house on the Home tab, and the logo in the top left corner has gone.

## 0.12.0 · 24 October 2026

### Flexarr for iPhone and iPad
- A new app for iPhone and iPad, alongside the Apple TV app: Home with Continue Watching and the rows you've set up, your libraries, search, title pages with seasons and episodes, and profiles with their PINs.
- Films and episodes resume where you left off, skip intros and recaps with a tap, and move on to the next episode. They keep playing in Picture in Picture, over AirPlay and with the screen locked.
- It finds your server on the network by itself. Approve the code it shows in Safari on the same phone, or in **Settings › Connect a TV** on any device signed in to Flexarr, or type the server's address and API key.
- It installs from Xcode onto your own device for now: see the README.

## 0.11.0 · 24 October 2026

### A tidier Server page in Settings
- **API key:** the key sits in a field as dots, with an eye to show it and **Copy** once it's showing. There's no extra step to confirm first, unless the main profile has a PIN; then the eye asks for it.
- **Signed-in browsers and TVs:** each one gets an icon and a single line saying who, where and when it was last used. The date it signed in and its address are on hover, and an address from the internet is always shown. **Sign out every other browser and TV** sits at the bottom, in red.
- **Webhooks:** one line per app, with its logo, its address cut to fit (Copy still takes all of it) and **Copy**. The Docker tip and **Make a new secret** sit underneath.

## 0.10.0 · 24 October 2026

### Logos and links in Settings
- **Settings › Connections** shows each app's logo (Sonarr, Radarr, Lidarr and Seerr) and an **Open** link that opens the app in a new tab. An app set up as `localhost` opens on the same address you reached Flexarr on.
- **Settings › Libraries** shows each library's icon (films, TV or music) beside its name.
- **Home and Browse** in Settings is now called **Customise display**.

### Tidier buttons
- Buttons always keep their label centred.
- On **Libraries** and **Profiles** in Settings, a card's buttons sit side by side, each as wide as its label, rather than stacked at different widths.

## 0.9.2 · 24 October 2026

### Fixed: a profile shown twice while editing it
- In **Settings › Profiles**, **Edit** now opens the form in place of that profile, rather than above the list while the profile is still listed underneath.

## 0.9.1 · 24 October 2026

### Settings without the main sidebar
- **Settings** no longer shows the main sidebar next to its own list of sections. The Flexarr logo at the top left takes you back home.

### Fixed: cramped spacing
- In **Discover** search results, the posters no longer touch the **Shows** and **Films** headings above them.
- In **Settings › Connections**, the Sonarr and Radarr cards have space between them again.

## 0.9.0 · 24 October 2026

### A sidebar for your libraries
- On the web, your libraries are now listed down a sidebar, under **Home**, on the left instead of across the top, so the bar no longer gets crowded when you have lots of them. **Collections**, **Discover** and **Coming soon** sit under them, and the list scrolls on its own if it gets long.
- **Home** is at the top of the sidebar; the Flexarr logo above it takes you home too.
- **Search**, **Settings** and your profile picture stay at the top right of the page.
- **Collapse sidebar** at the bottom of it shrinks it to a strip of icons (hover one to see its name). The web app remembers your choice, and starts as the strip on narrower windows.
- On phones, the ☰ button at the top left slides the sidebar in, replacing the tab bar along the bottom.

## 0.8.0 · 24 October 2026

### Version numbers
- Flexarr now has version numbers. This is **0.8.0**; earlier changes below are grouped into the versions they arrived in, 0.1.0 to 0.7.0.
- **Settings › Changelog** says which version you're running, and each version is tagged on GitHub.

### Your profile picture on Apple TV
- The current profile's picture sits in the top right corner, level with the tab bar (press right from the last tab to reach it), replacing its name at the end of the tab bar. Open it to switch.

### Fixed: a screen kept a stream that had stopped
- When several screens on one profile started watching at the same moment and the server was full, one could be left with a stream that had already been stopped for another screen, instead of being told why.

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

## 0.7.0 · 24 October 2026

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

## 0.6.0 · 24 October 2026

### Profiles on Apple TV
- When your household has more than one profile, the Apple TV asks **Who's watching?** the first time it connects, and remembers the answer.
- The current profile's name sits at the right end of the tab bar. Open it to switch; with two profiles the other person is already highlighted, so it's one press.
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

## 0.5.0 · 24 October 2026

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

## 0.4.0 · 24 October 2026

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

## 0.3.0 · 24 October 2026

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

## 0.2.0 · 24 October 2026

### Profiles for the household
- Everyone gets their own Continue Watching, watched history and Up Next, with optional PINs.

## 0.1.0 · 24 October 2026

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
