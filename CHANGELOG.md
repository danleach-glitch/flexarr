# Changelog

What's changed in Flexarr, newest first. Changes not yet in a version are under **Unreleased**.

## Unreleased

### Trakt
- Connect a profile to your **Trakt** account in **Settings › Trakt**, on the web, iPhone, iPad, Android and the Apple TV: enter the code Flexarr shows at trakt.tv/activate (or scan the QR code on a TV). Each profile connects its own account. Kids profiles don't have Trakt.
- **Bring your Trakt history across** shows what would change before anything does, then marks watched here what you've watched on Trakt, and sends Trakt what you've watched here. Titles you've watched that aren't on your server yet arrive already marked watched. Nothing is ever marked unwatched.
- From then on, what you watch shows on Trakt as you watch it, and counts there when it counts here. **Mark as watched** and downloads watched away from home go to Trakt too, and plays in other apps connected to Trakt come back within a few minutes.
- Stop part way and pick up in another app connected to Trakt, or the other way round.
- Your watchlist stays in step with Trakt's, both ways. Titles from Trakt are never requested by themselves.
- Choose Trakt lists, your own or ones you've liked, to show in **Collections**, just for you, with films you don't have yet ready to request (web).
- Discover has a new row, **For you, from Trakt**, with Trakt's picks for you (web and Apple TV).
- Settings says plainly if Trakt has signed Flexarr out, if a free account's watchlist is full, or if changes are waiting to send. **Disconnect** makes Trakt forget Flexarr; what you've watched stays.
- The main profile can use its own Trakt app instead of Flexarr's, in **Settings › Connections › Trakt**.

### Profiles
- The main profile can have a PIN of its own, apart from the admin PIN. Choosing your profile then asks for it on every device. Add, change or remove it in **Settings › Profiles**, like anyone's. The admin PIN still guards the server's settings and lets a child off a kids profile, and it opens the main profile too.
- Someone on your tailnet with their own account chooses the main profile with the admin PIN only. So does a browser at home that isn't signed in, when there are kids profiles.
- **Forgot PIN?** on a profile's PIN pad takes the PIN off with the admin PIN, on the web, iPhone, iPad, Apple TV and Android.
- PIN pads that want the admin PIN show a padlock, not the main profile's picture.
- Upgrading turns the PIN you had into the admin PIN, and nobody is signed out. Your profile keeps it as its own too only if **Ask for the PIN when someone chooses the main profile** was on (that switch is gone): change it in **Settings › Profiles** if you change the admin PIN.
- Going back to an earlier version after this one needs the backup from before the upgrade. Earlier versions don't know the admin PIN.

### Security
- A new **Settings › Security** page holds the admin PIN, the API key with **Ask for it**, and the signed-in browsers and TVs. Change the admin PIN there with the current one, or with the API key if it's forgotten. **Settings › Access** keeps watching away from home.

### Changed
- For scripts: `PUT /profiles/owner` sets the main profile's own PIN, and sending `currentPin` or `apiKey` there (the old way to change the admin PIN) is refused. Change the admin PIN with `POST /admin/pin` and `currentPin`. Saving `askPinForMainProfile` in `/settings` does nothing; reading it says whether the main profile has a PIN of its own.

## 0.56.0 · 31 October 2026

### Watchlist
- Each profile now has a **Watchlist** of films and shows to watch later, whether you have them yet or not. Add titles from their pages, from posters' menus, from Discover and search (on iPhone and Android, Search now finds films and shows you don't have yet, to add or request), with Siri ("Save Severance in Flexarr" for one in your library, or "Add to my watchlist in Flexarr" and then any title), or through an assistant. It's in the web sidebar, at the top of **Libraries** on iPhone, iPad and Android, and in the Apple TV's sidebar.
- Each title says where it's got to: ready to play, on its way, out on a date, or **Request** (on Apple TV too), and which streaming services have the ones you don't. Ones you can play now come first, wait on Home under **Ready from your watchlist** and in a new **Watchlist** widget on iPhone and Android, and your phone tells you **From your watchlist** when one arrives.
- Show only one kind, only films or only shows, and sort by when you added them, when they came out (newest first), or by title, in every app.
- Titles leave by themselves once you've watched the film or started the show.
- The main profile can have a profile request what it adds to its watchlist, in **Settings › Profiles**. It's off to start with.

### Kids profiles
- Give any profile an age limit in **Settings › Profiles**: U, PG, 12, 15 or 18. Titles rated above it are hidden from that profile everywhere, on every device: Home, the libraries, search, Continue Watching, the next episode, collections, Siri and notifications. Ratings from any country count, rounded up to the next UK age, so a show rated TV-14 in the US is a 15 and one rated 16 in Germany an 18.
- Titles without a rating are hidden too, and so are music and home videos, which have none. Choose **Fine for kids** on a library in **Settings › Libraries** to let all of it through, or **Allow for kids** and **Block for kids** in a film's or show's **⋯** menu to decide for one title. Live TV shows only the channels you pick under **Channels for kids** in **Settings › Live TV**.
- Profiles with an age limit don't see Discover or Coming soon, can't make requests and can't add podcasts, since Flexarr can't tell how old a title it hasn't got is for.
- **Child lock** is what keeps a child on kids profiles. Without it, anyone can switch from any profile to any other, kids profiles included, with no PIN but a profile's own.
- Films Flexarr looks up on TMDB itself (not through Radarr) now show the age rating of your server's country, falling back to the UK's.
- Make it a **Kids profile** for a child: no Settings, no scores or score-ranked rows, no marking things watched and no trailers, with a **Watch again** row on Home for their favourites. On the web, the artwork is bigger too. A child with nothing to watch yet sees one line, not the setup screen.
- **Child lock** keeps a TV, phone or browser on kids profiles until the admin PIN turns it off: in the web's profile menu, in Settings on Apple TV, iPhone and Android, and in the profile picker, where each kids profile shows a padlock (even while a kids profile is watching): hold it on the phones, or press and hold the profile on Apple TV. **Settings › Devices** on the web marks devices with **Child lock on**, and **Turn off child lock** there turns it off from afar, which the phone notices the next time its profile picker opens. Turning it on from the main profile takes no PIN; turning it off takes the **admin PIN**, the PIN that also protects the server's settings. A phone's notifications follow it to the kids profile while child lock is on, and come back to the main profile when it's turned off.
- Kids profiles no longer have Podcasts, the Watchlist, Collections, Live TV or subtitle search, in every app. Settings › Profiles no longer shows request settings for a profile with an age limit, since it can't request.

### Getting started
- A new server now welcomes you with a short setup guide after you choose its PIN, one card at a time. It asks your name, then walks you through connecting Sonarr, Radarr and Lidarr, adding your libraries, adding everyone who watches, getting Flexarr on all your devices, and watching away from home. You can skip any step after your name, or choose **Set up later** to leave the guide.
- The guide fills in the address of any Sonarr, Radarr or Lidarr it finds running nearby, so you only paste the API key. It then offers each app's media folders as ready-made libraries (one each for films, TV shows and music), so there's no folder browsing, and warns you if you pick a whole drive. At the end it says what's still being read, and points out a library that came up empty or a folder Flexarr can't see.
- Connecting Sonarr, Radarr or Lidarr, in the guide or in **Settings › Connections**, now adds Flexarr's webhook to the app by itself, so new downloads show up straight away. If the app can't reach Flexarr, the webhook addresses are still under **Webhooks** to add by hand.
- If you leave the guide part way, Home shows **Finish setting up** with what's left. Each one opens the right page in Settings, and **Hide** puts the list away.
- An empty Home now says **Nothing to watch yet**, with **Set up Flexarr** to open the guide.

### Sound
- **Dialogue boost** makes voices easier to follow under music and effects, at Low, Medium or High. **Night mode** quietens loud scenes and lifts quiet ones, so explosions don't wake the house and whispers stay audible. Turn it on, or have it come on by itself between set times (10 pm to 7 am to start with). **Speakers** folds the sound to stereo or 5.1. Choose them in the player's audio menu or in **Settings › Playback**, on the web, iPhone, Apple TV and Android. Each device keeps its own, since it's about the room. On the web and Android they apply to live TV too. When the server converts a film for an Apple TV or iPhone, it shapes the sound itself, so they work there too.
- Say "Turn on night mode in Flexarr" to Siri on iPhone or iPad. It changes the device chosen under **Settings › Siri › Siri plays on**, or the iPhone or iPad itself. The Shortcuts app has **Night mode** and **Dialogue boost** actions for automations, and assistants connected over MCP can change a device's sound too. Another device takes the change while Flexarr is open on it, from this version of its app on.
- On the web, the player has a new **Audio** button (the waveform) for the audio track, audio sync and these sound settings, as the apps do; **CC** is now just subtitles.
- On iPhone and Apple TV they need **Play files directly**. While any of them is on, surround plays as PCM rather than passed straight to the receiver, so Atmos plays without its height channels. With AirPlay, the sound plays as it is, and the audio menu says so. Dolby soundtracks are turned down a little while any of them is on, to match how loud they are when passed through, so switching one on doesn't make everything louder.

### Search
- Search shows your recent searches before you type anything, on the web, iPhone, Apple TV and Android. It shows your last five searches (pressing Search keeps one, as does opening a result), plus the things you opened from them, so you can go back with one tap. They follow your profile across devices, so something you searched for on your phone is waiting on the TV. Remove a single search, or clear them all.
- Search forgives typos, accents and punctuation: "breking bad" finds Breaking Bad, "amelie" finds Amélie and "spiderman" finds Spider-Man. This works for titles, albums, people and genres, including titles you ask for by voice with Siri or Google Assistant.
- A genre with a slash in its name ("Action/Adventure", from a folder's own .nfo files) now opens from Discover on the web, iPhone, Apple TV and Android. Before, its page showed an error or no titles.
- On the web, the search box in the search window no longer draws its own outline into the window's edges.

### Profiles and settings
- Changing the server's settings now takes the admin PIN, even at home. Anyone can still watch as the main profile without it. Open **Settings** on the main profile, enter the PIN, and settings stay unlocked on that browser or device for 15 minutes after your last change, or until someone else is chosen there or you choose **Lock settings**.
- Setting up a new server asks for a PIN to protect its settings along with the setup code. The Mac app opens the welcome page with the code already filled in, and Docker's log gives a link that does the same, so you only choose the PIN. Without the link, the page says in one line where the code is for your kind of install, and takes the whole link pasted in as well as the code. A server set up before this asks for a PIN the first time you open Settings.
- The admin PIN can be changed but no longer removed. Forgotten it? Choose **Forgotten it?** in Settings and enter the API key with a new PIN. The Mac app has **Reset Admin PIN…** in its menu, which needs no key.
- Choosing the main profile no longer asks for its PIN on browsers and devices you signed in, unless you turn on **Ask for the PIN when someone chooses the main profile** in **Settings › Profiles**. Servers whose main profile already had a PIN keep asking, as before.
- When a device asks to connect, the main profile still hears about it while settings are locked, and **Allow** asks for the PIN first.
- Tidying the library doesn't take the PIN: on the main profile, **Delete**, **Fix match**, **Allow for kids** and **Block for kids**, analysing a title, **Scan for changes** and the household's collections work while settings are locked.
- When someone gets the admin PIN wrong too many times, Flexarr notes it in its log and tells the main profile's phones which device it was. Tapping the notification opens Settings in the app.
- After too many wrong PINs, the web's **Settings are locked** page and PIN prompt count the wait down, and **Unlock** comes back when it's over.
- The profile picture at the top of Home on iPhone and Android is bigger, so it's easier to find and tap. On iPhone it scrolls away with the page rather than staying pinned over it.
- Switching to another profile on Android stops the music or podcast that was playing, as on iPhone, and clears Up Next, so what you play next counts for the right person.
- On the web, **Test** in **Settings › Connections** shows a red cross when the test fails, as well as a green tick when it works.

### Playback
- Auto-play now moves on to the next episode even if you've left the Apple TV alone for hours. Before, it could go to sleep during the countdown and stop on the credits.
- For shows with no credits found, Next Episode now appears a little before the end (up to two minutes for an hour-long show), not just in the last 30 seconds. On iPhone and Apple TV, Skip Intro and Next Episode show by themselves, without bringing up the rest of the controls.
- The first time a film is played, it starts as quickly as any other time. Its scrubbing previews are now made a minute later, and in the background, rather than while the player waits for its first seconds.
- On Android, the player's buttons light up as you reach them with a keyboard's arrow keys or Tab, so you can see where you are.
- Films whose sound starts a little after the picture, with a silent opening, now play on Apple TV and iPhone with **Play files directly** off. Before, the player refused them straight away. Their Dolby Digital Plus is converted for these films only, so they play without Atmos.

### Film and show pages
- On the web, Play, Trailer, Watched, Watchlist and **⋯** sit on one row, rather than wrapping onto a second.
- On the web, a long description is cut to four lines, with **More** to read the rest.
- On Apple TV, a title's buttons sit on one tidy row: Play and Start over spelled out, then round buttons for Watched, Watchlist, Go to show and **⋯**, each named underneath as you move onto it. Forgetting your place in something half-watched moved into **⋯**.

### iPhone
- **Sign out**, **Delete all downloads**, removing a download and unfollowing a podcast ask in a normal dialog in the middle of the screen, rather than a bubble pointing at the wrong row.

### Connecting devices
- When a new Apple TV, iPhone or Android phone asks to connect, any iPhone or Android phone on the main profile asks "Connect Living room?", with what kind of device it is and its code. Check the code matches the one on the device's screen and tap **Allow**, with no need to go to a computer. It shows while Flexarr is open, and as a notification when it isn't, on phones with notifications turned on. The notification has **Allow** and **Don't allow** buttons of its own (the phone asks to be unlocked first); on Android they show on the lock screen too, and on iPhone press and hold the notification. The web app asks the same when it's open on the main profile, in a banner across the top of the page. While settings are locked, **Allow** asks for the admin PIN, then connects it, the same way on iPhone, Android and the web.
- Settings on the iPhone and Android apps (right on the Settings page), and **Settings › Access › Connect a device** on the web, list the devices waiting to connect, each with **Allow** and **Don't allow**.
- A device left on its connect screen keeps the same code rather than getting a new one every ten minutes, so the notification on your phone stays right. After **Don't allow**, the device's connect screen (Apple TV, iPhone and Android) says **Not allowed**, with **Try again** to ask your phones once more, with the same code, rather than quietly showing another code. Asking again never ends in "Too many devices are waiting to connect". Apps from before this version still show a new code by themselves, which isn't asked about but is listed in Settings, marked as turned away, with **Allow** in case you changed your mind.
- On iPhone, notifications come back on by themselves after the phone is signed out and connected again, once you choose who's watching; before, they could stay off until you turned them on again in Settings. Devices waiting to connect, in Settings, says when notifications are off, since without them a device only asks while Flexarr is open.
- The connect screens on Apple TV, iPhone and Android now start with approving on your phone, and still explain how to approve from a computer (**Settings › Access › Connect a device**).

### Music
- On Apple TV, the now-playing bar along the bottom goes away a few seconds after the music is paused or finishes, and comes back when you press play. Now Playing has a new **Stop** button, which ends the music and takes the bar and the Now Playing tab away until you play something else.
- On Apple TV, album, artist and playlist pages fill the screen, without the grey strip down the left.
- On the web, **Made for you** mixes are cards the size of albums again, not one blurry mix filling the width of the page.

### Podcasts
- On the iPhone and Android apps, a downloaded episode is deleted as soon as you've heard it to the end, rather than a day later, and leaves Downloads and Home's new episodes there and then, without a refresh. Downloads you made yourself go too, not only ones the app fetched by itself. With no connection, it goes once the phone is back online and has told the server.
- Marking a downloaded episode as played on the phone (or **Mark all as played** on its show) deletes the download straight away. An episode finished or marked played somewhere else, such as on the web, is deleted from the phone the next time the app catches up with the server. An episode you download again after hearing it stays until you've heard it again.
- On the iPhone and Android apps, an episode's download button shows a ring that fills as it downloads, from the moment you tap it, instead of an hourglass while the server fetches it.

### iPhone remote
- When an Apple TV in the house is playing something from Flexarr, the iPhone app shows a **Playing on TV** bar above the tabs, with play/pause. Tap it for a remote: a scrubber you can drag, back and forward 10 seconds, previous and next episode, and **Skip Intro** or **Next Episode** when the TV offers them.
- From the remote you can also change the TV's soundtrack, subtitles and speed, and its dialogue boost, night mode and speakers. Below are the season's other episodes: tap one to play it on the TV.
- **Continue on iPhone** stops the film or episode on the TV and carries on from the same moment on the phone. The TV goes back to the title's page and says **Now playing on iPhone**.
- While the remote is following a TV, the phone shows what's playing on the Lock Screen and in the Dynamic Island (with Flexarr's mark), with back 10 seconds, play/pause and forward 10. Tap it to open the remote. It keeps up while the phone is locked, as long as notifications are on for your server. The title and picture never leave your server: only play/pause and the time pass through Flexarr's notification service.
- The bar and the remote follow the TVs playing for the profile chosen on the phone: Claire's film on the bedroom TV doesn't show on Dan's phone. With more than one of yours playing, switch between them at the top of the remote. For menus and volume, use the Remote in Control Centre as before.
- **Stop** offers **Undo** for a few seconds. The **×** on the bar hides it until that TV plays a different show or film, without stopping anything.

### Siri
- New things to ask on iPhone and iPad: "Skip intro in Flexarr", "Next episode in Flexarr", "Skip back in Flexarr", "Skip forward in Flexarr", and "Flexarr, turn on subtitles" or "Flexarr, turn off subtitles" (said that way round, as Siri answers "Turn on subtitles" itself). They work on the phone or on the Apple TV that Siri plays on. Skips are 10 seconds, as on the remote, and subtitles come on in your language where there are some.

### Android
- The buttons at the bottom of the welcome pages are their proper size on Samsung phones, with a soft glow as on iPhone, rather than sitting on a large pale block.
- Connecting the phone to a server no longer gets stuck on "Too many devices are waiting to connect" when the phone sleeps or the Wi-Fi blinks while it shows its code. It keeps waiting on the same code, so approving it in the browser lets the phone straight in.
- The carousel at the top of Home no longer sometimes stops halfway between two titles when it moves on by itself.
- "Who's watching?" and the podcast sheet open at full height, so every profile shows without dragging the sheet up.
- If the profiles can't be read as the app starts (the network not up yet, say), the app asks again, rather than leaving Home without its profile button until it's restarted.

## 0.55.0 · 29 October 2026

### Music
- Play and Shuffle on an artist, album, playlist or mix go straight to Now Playing, on the web, iPhone and Apple TV. Picking a single song still leaves you on the list. On Apple TV, starting music no longer drops you back on Home, and Menu on Now Playing takes you back to where you pressed Play.
- On Apple TV, the screensaver stays off while the music visualiser is on screen and the music is playing.

### Discover
- Discover's Popular shows row is now Popular new shows: the most popular shows that started in the past year, rather than long-running ones like Grey's Anatomy and Friends.

### Android
- Podcast episodes swipe as on iPhone: swipe toward the end to mark one played (or unplayed) or play it next, and toward the start to add it to Up Next or download it. Swiping all the way across marks it played at once, and in Up Next, swiping the other way takes it off. Unfollowing a show now says its new episodes stop coming to this phone and downloaded ones stay.
- Cast to a Chromecast or Google TV: the Cast button sits beside picture in picture in the player, and in Now Playing for music and podcasts. While casting, the phone shows what's playing with play, pause, skip and a scrubber, and your place is still saved. Stop casting and it carries on on the phone from the same spot. Films and episodes the Chromecast can't play as they are get converted, which needs the server on this version too. Subtitles and Live TV aren't cast yet.
- The Android app only reaches Google's notification service once you turn notifications on, rather than each time it starts. The privacy policy now covers the Android app.
- Settings has Playback and Podcasts pages, as on iPhone: turn auto-play and skipping intros, recaps and credits on or off, set subtitle defaults, choose how far the player jumps back and forward (10, 15 or 30 seconds), and set podcast auto-download, how many episodes to keep and the speed. Settings also links to the privacy policy.
- If the server signs your phone out, its downloads stay, and anything you watched offline is sent once you connect to that server again.
- Back steps back through connecting and entering a PIN rather than closing the app. Player errors say what went wrong in plain words and offer Try again, and a failed download or Mark as watched from a long-press now says so.
- TalkBack can bring back the player's controls (which no longer hide while it's on), reads the timeline as "12:30 of 1:45:00", and announces headings, buttons and errors throughout. Small buttons are easier to tap, large text no longer cuts off the top of Home and title pages, and the Home carousel holds still while you touch it, under TalkBack, or with animations off.
- The Live TV guide opens quickly with hundreds of channels, and the app uses less battery in the background.
- Moving on to the next episode during the credits marks the last one watched, and a "play on this phone" sent for another profile plays on that profile.
- Away from home, the app only uses a secure (https or Tailscale) remote address, and an address you type for a server on the internet is tried over https first.
- When a film or episode's stream drops (the server restarts, or the Wi-Fi blinks), the player picks it up again from where it was, trying for up to 15 seconds, as on iPhone. If someone started watching on another screen and took the stream, it says so and offers Watch here instead.
- Previous episode starts the episode again if you're more than a few seconds in, and goes back through what you've watched in this sitting, shuffled episodes included.
- Live TV plays on in picture in picture when you leave the app, and has the sleep timer, with "Still watching?" before it pauses. Shake the phone to answer "Still watching?" with 15 more minutes, while watching anything. The back and forward buttons in Live TV and in picture in picture jump as far as you chose in Settings › Playback.
- Film and TV libraries have Genres at the top, as on iPhone: a tile for each genre, with a picture from one of its titles, that opens the library's titles in that genre.
- The Libraries tab follows your library order for Podcasts and Live TV too, rather than always putting them last. In Settings › Library order, drag a library by its handle to move it, and Podcasts and Live TV are in the list.
- Home has a New podcasts row after Continue watching: the newest episodes of the shows you follow, each opening its notes with Play.
- A fresh install opens on three welcome pages saying what Flexarr is, how to set up the free server and how the app finds it.
- When Android allows notifications but your phone isn't getting them from this server, Settings offers Turn on notifications.
- Settings has Rate Flexarr, which opens the app's Google Play page, and now and then, after you've watched something to the end, Google Play may ask how you're finding Flexarr.
- Download albums and playlists on Android, as on iPhone: the download button beside Play and Shuffle fetches every track, Downloads has a Music section, downloaded tracks and albums carry a green arrow, and they play from the phone with no connection. What you listen to offline counts once you're back.
- On Android 14 and later, downloads you start (including from a notification's Download button) keep going for hours with the app closed, rather than stalling when Android limits background work. They show in Android's list of running apps, where you can stop them.
- In Android Auto, say "Hey Google, play The Rest Is History on Flexarr" (or an artist, album, song or playlist) and it plays: a podcast you follow picks up at the episode to hear next, "the latest …" plays its newest, and "play my Up Next" plays Up Next. The car's search button finds your podcasts and music too.
- On the phone, the Google Assistant plays from Flexarr as Siri does on iPhone: "play Heat on Flexarr" plays the film, a show's next episode, a podcast or music, and "continue watching on Flexarr" picks up where you left off. "Search Flexarr for Fargo" opens Search with it typed in, and "open Downloads in Flexarr" goes straight there.
- Two podcast widgets for the home screen, as on iPhone: Continue listening (the episode you're on, how long is left, and a play button) and Up Next (the episodes waiting). They keep showing what they last knew when the server can't be reached.
- Teletext subtitles in recorded TV have a Teletext page choice in the subtitles menu, as on iPhone: Automatic, or page 888, 777, 150 or 801, for a recording whose subtitles aren't on the page the broadcast marks. It needs the server on this version too.
- Styled subtitles (the ASS and SSA kind, common with anime) keep their look: signs and captions sit where the file puts them, in its colours, bold and italics, while ordinary lines follow your subtitle position and size.
- Download on a film's or episode's page starts straight away at your size from Settings › Downloads, as on iPhone, rather than first asking which size. Tapping one that's here offers Remove download.
- Downloads the phone starts by itself (the next episode, new podcast episodes) aren't asked of the server until the phone is on Wi-Fi, when Settings keeps them to Wi-Fi, so the server doesn't get ready what can't come yet. They also hold off while Data Saver is on, as on iPhone in Low Data Mode.
- flexarr:// links from other apps and the browser open the Android app, as they do the iPhone app: a title's link opens its page, a Live TV channel's opens the guide, and one naming a profile opens "Who's watching?" (asking for its PIN, if it has one). A link from another app never starts anything playing by itself.
- A library's list keeps up with the server, as on iPhone: when it refreshes after you've watched something or been away a while, titles removed since disappear and new ones appear, rather than only the badges updating.

### iPhone and Apple TV
- Away from home, the apps only use a secure (https or Tailscale) remote address, and only once they've checked the same server answers there.

## 0.54.0 · 29 October 2026

### Podcasts
- Now Playing for a podcast has a fresh look on iPhone, Android and the web. The controls stay put, and a Notes button swaps the cover for the episode's chapters and notes (and, on the phones, Up Next).
- Episodes with chapters show the one playing above the scrubber, with arrows to skip to the next or back to the last. The scrubber marks where each chapter starts, and the list of chapters takes you to any of them. Chapters come from the show's chapters file or, as most shows do it, from the times listed in its notes.
- Times written in an episode's notes ("14:26") jump straight there, and web addresses in them open.
- Now Playing says when the episode will end, at the speed it's playing. Speed, the sleep timer (now a moon) and a new menu sit together in one row. The menu has Go to podcast, Mark as played and Share podcast, plus Download on the phones.
- The glow behind a podcast takes its colour from the show's artwork.
- On iPhone and Android, the scrubber is slimmer and thickens while you drag it, Play or Resume in an episode's notes opens Now Playing, and anything downloaded (episodes, music, films) shows a green download arrow rather than a tick, so a tick only means played. A played episode's tick in its notes is green.
- Home on iPhone and Android no longer says "Nothing to watch yet" above your podcasts when they're all there is.
- On the web, dragging the scrubber no longer stalls a podcast: it jumps once, where you let go.

### Settings
- Settings › Connections lists every app Flexarr works with (Sonarr, Radarr, Lidarr, Seerr, Bazarr, TMDB and Dispatcharr), so you can see at a glance what's connected. Any that isn't has a Connect button. Running a second Sonarr, Radarr or Lidarr is the + beside the one you have.
- Settings is easier to read at a glance. Connect buttons are green, like the dot beside a connected app, and switches that are on are green too. Deleting something turns red when you point at it, and the final Delete is a solid red button. Boxes you type in are darker than the panel around them and have a light border. A row's own buttons (edit, rescan, artwork, delete) stay out of sight until you point at them.
- In Settings › Connections, an app you've connected has Disconnect instead of a bin, because its row stays and you can connect it again. A second Sonarr, Radarr or Lidarr still has Remove, as its row goes.
- Settings has shorter help text throughout. In Connections, setup steps (such as making Dispatcharr's Streamer user) are under How to set it up, and Media folders' saved mappings are under Path mappings, with Edit mappings.

### Libraries
- Film and TV libraries have a Genres view: a tile for each genre, with a picture from one of its titles, that opens the library's titles in that genre. On the web it's beside Browse and All; on the Apple TV it's in the switch at the top of the library (and can be what a library opens on, in Settings › Libraries); on iPhone and iPad, choose Genres at the top of the library.

### Library order
- Put your libraries in the order you like, and every device follows it: the web's sidebar, the Apple TV's sidebar, and the Libraries tab on iPhone and Android. Each profile has its own order.
- On the web it's in Settings › Sidebar (drag a library, or use the arrows); on the Apple TV, Settings › Sidebar as before; on iPhone and Android, Settings › Library order.
- An order you'd already set on the Apple TV carries over to your profile the first time you open the app.
- On the web, Podcasts now takes its place among your libraries in that order, instead of always coming last.

### Settings
- In Settings › Libraries on the web, a library's buttons stay in one row when its folder paths are long.
- Settings › Access shows each phone, tablet and TV with its own icon, and says what it is (an iPhone, an Android TV) when you point at it, once its app is up to date.

### Playback
- A film with a chapter called "Ending" or "Credits" well before its end no longer offers Skip Credits there, or counts as watched from that point. Real end credits, even with a short scene after them, still do.
- On Apple TV and iPhone, scrubbing through a long film no longer keeps every preview picture in memory, which could make older Apple TVs close the player.
- Previous in the player works like it does for music: a few seconds into an episode it starts it again, and a second press goes to the episode before. In a film, or the first episode, it starts it again. On Apple TV, iPhone and the web, which now has a Previous button too.
- Audio sync moves the sound in 10 ms steps, so lips and voices can be lined up exactly. Hold − or + (a long press on the Apple TV remote) to move it 50 ms at a time. On the web, Apple TV, iPhone, iPad and Android.

### Home
- When a library is empty, Home says so in one short line: add files to the library folder, or connect Sonarr or Radarr.
- Latest unwatched shows puts a show up front for a new episode you haven't seen, not because Sonarr upgraded one you've already watched.

### Siri and Shortcuts
- Play a film or show, and Shuffle a show, can play as a chosen profile (the new As setting), so a "kids' bedtime episode" shortcut plays their next episode under their name, whoever used the TV last. The device switches to that profile first, and asks for its PIN if it has one. It works on this device and on another one, such as the Apple TV.
- Links can name a profile too: `flexarr://play/<id>?profile=Sam` switches to Sam, then plays. Handy for NFC tags and Home Assistant. `flexarr://play/` with a show's id plays its next episode.

### Apple TV
- The Top Shelf shows what's been added recently, below Continue Watching, so it's never blank once you've finished everything or on a new profile. It follows your profile's libraries, as Home does.

## 0.53.0 · 29 October 2026

### Music
- In the web app, click the cover in the music bar to open Now Playing across the whole window: a big cover, the controls and what's up next, with soft light in the cover's colours moving in time with the music behind it. Escape closes it. The light can be turned off in Settings › Playback › Music in this browser.
- On Apple TV, the same light moves behind Now Playing in the cover's colours, in time with the music. Settings › Playback › Music visualiser turns it off.
- The Apple TV plays FLAC and MP3 music files as they are, so the server no longer converts them.

### Apple TV and iPhone
- The screensaver no longer comes on in the middle of a film, whatever format it's in. It still comes on when you pause.
- A film that won't open within a minute plays from the server instead of leaving the spinner up for ever.
- Subtitles follow your caption style from the system's Accessibility settings (font, size, colours and background), as Apple's own player does, on top of your profile's subtitle size.
- A subtitle line no longer shows twice after you rewind.
- On Apple TV, the sidebar no longer stops responding when music starts or stops while you're browsing.
- Coming back to the Apple TV app after leaving it mid-film picks up where you were, playing if it was playing, instead of a black screen.
- Skipping or scrubbing to the very end of a film counts as finishing it: it's marked watched and the next episode comes up.
- The spinner stays until the picture is actually on screen, so a film no longer starts on a black screen.
- The next episode starts sooner, as its opening is fetched during the credits.
- Lossless surround is on by itself when the Apple TV is connected to a receiver or soundbar that takes it, which keeps 4K films with TrueHD sound from stuttering. Settings › Playback still turns it off.
- HLG videos that the Apple TV can't draw itself play from the server instead of showing black.
- The player's controls no longer sometimes stay on screen while a film plays; they fade away a few seconds after you stop using the remote, as they should.
- On iPhone, notifications come back on by themselves when you sign in to a server again, if you had them on before.
- If your iPhone allows Flexarr notifications but isn't getting them from this server, Settings says so, with a button to turn them on.

### HDR10+
- Films and episodes with HDR10+ say so: **HDR10+** (or **Dolby Vision, HDR10+**) in their details on the web, iPhone and Apple TV, and in the player's stream info. Files already in your libraries are checked on the next scan.
- When the server repackages a stream for Apple TV, the HDR10+ scene-by-scene metadata goes with the picture, for televisions that use it.

### Subtitles
- Search for subtitles from the player, on Apple TV, iPhone and the web, or from a title's page in the web app, when a film or episode has none in your language. Flexarr asks Bazarr, which searches OpenSubtitles and the other subtitle sites it's set up for, and lists what it finds, best match first, marking those made for your exact file. Choose one, or let Flexarr pick, and it's saved beside the video and switched on where you were. Connect Bazarr in Settings › Connections; it works for what Sonarr and Radarr manage.
- Subtitles beside a video that drift out of time can be re-synced to the soundtrack from the same menu.
- Hearing-impaired subtitle files named the way Bazarr names them (`Film.en.hi.srt`) are labelled SDH.
- Styled subtitles (ASS and SSA, as most anime has) play in the web player as they were made: their own fonts (those attached to the file), colours, positions, signs and karaoke, rather than plain white text. On Apple TV, iPhone and iPad they now go where they were placed, such as signs at the top of the picture.
- Choose where subtitles sit: **Bottom**, **Raised**, **Higher** or **Top**, in Settings › Playback or the player's subtitles menu, on every device.
- **Subtitles in the black bars**: in films wider than the screen, subtitles go in the black bar below the picture instead of over it. Turn it on in Settings › Playback or the player's subtitles menu.
- Closed captions carried inside the video, as American TV recordings have them, can be turned on like any other subtitles, on every device. Files already in your libraries are checked on the next scan.
- Teletext subtitles, as European TV recordings carry them, show on Apple TV, iPhone and iPad, where the subtitles menu also lets you choose the teletext page. In a web browser they show when the server's FFmpeg can read teletext, as the Docker image's can.
- In the web player, the “Preparing subtitles” note now goes away as soon as the subtitles appear.

### Home
- Every show or film in the header at the top of Home (and of a library's page) that you've started says **Resume** and picks up where you left off, on the web, iPhone and Apple TV. Before, only the first one did, and on iPhone and Apple TV a show always said Play.

### Requests
- Each profile can choose which library the shows and films it requests are added to, in Settings › Requests. When two people have a TV library each, the shows each of them asks for land in their own. It's there when you have two TV or film libraries Seerr can add to, applies to requests from every device, and the main profile can set it for anyone in Settings › Profiles.

### Settings
- In Settings › Connections, Add a connection offers Seerr, Bazarr, TMDB and Dispatcharr as well as Sonarr, Radarr and Lidarr, and only what you've set up is listed. In an open connection, Remove is shown in red.
- Profiles other than the main one see only their own settings: the server's section is gone from their Settings, rather than listed with a note about the main profile.

### Playback
- When you've chosen a language, the soundtrack in it is picked over a commentary or audio description track, and the file's default track wins among the rest.
- **Audio sync:** when a file's sound and picture drift apart, move the sound earlier or later in 50 ms steps from the player's audio menu, on the web, Apple TV, iPhone and iPad. It's fixed for everyone in the household.
- **Sleep timer for films and episodes:** stop in 15, 30, 45 or 60 minutes, or at the end of this episode. In the last minute the picture dims and asks "Still watching?", and any button adds 15 minutes (on iPhone, a tap or a shake). Otherwise the sound fades out and playback pauses.
- **Speed in the web player,** from half speed to double. The speed you choose for a show's episode is kept for its other episodes, on every device.

## 0.52.0 · 28 October 2026

### Playback
- Live TV channels that need converting share the server's limit on conversions with films and episodes, so a busy evening can't overload it. A channel played as it is doesn't count.
- A download being converted for your phone stops when someone starts watching, and carries on once they've finished, instead of competing with them.
- HDR films converted for an SDR screen no longer look grey and washed out on Docker and Linux servers, on NVIDIA and Intel Quick Sync graphics, or with subtitles burned in.
- When the graphics card won't convert a video (busy, or not passed in to Docker), Flexarr converts it in software instead of failing every time you press Play. The log says when it does. (The Mac app has no software converter, so there it still fails.)
- Burned-in picture subtitles (Blu-ray PGS, DVD) fit the picture: they stay in frame on films cropped to widescreen, and are full size in 4K instead of small in a corner.
- Dolby Vision films are only sent as they are to screens that show Dolby Vision. Elsewhere they're repackaged or converted, instead of showing purple and green. Safari on an HDR Mac now says it shows Dolby Vision, so it keeps playing them directly.
- Films and episodes in 10-bit H.264, and other formats players can't decode (4:2:2 and 4:4:4 video, 12-bit HEVC), are converted instead of failing to play.
- HDR10 and HLG are only sent as they are when the Apple TV says it's showing them right now.

### Server
- The Docker image has a health check, so Docker, Compose and NAS apps show when the server has stopped answering.
- **Coming soon**, requests and notifications find titles faster in big libraries.
- Very large Sonarr and Radarr libraries over a slow connection no longer time out while Flexarr reads them.
- A live TV channel whose stream stopped while nobody was watching starts afresh for the next viewer, instead of opening frozen.
- Big library scans finish sooner and use less memory once the files have been read and the details are being fetched.
- Download progress shows for everything Sonarr and Radarr are fetching, not just the first 200.
- Episodes in **Coming soon** that Sonarr gives only a date for now show on the right day wherever you are.
- Finding intros and credits carries on when one file can't be read, such as one on a network share that stopped answering, instead of stopping for good.
- A title or its show being removed by a scan while you open it no longer shows an error.
- Watching a long film no longer fills the disk: the server keeps the last 10 minutes of what's been converted for rewinding and deletes the rest as playback moves on. Films whose video is copied rather than converted also take half the space they did.
- A podcast folder Flexarr isn't allowed to read no longer stops the server from starting.

### iPhone and Apple TV
- The app's sign-in and notification key stay on the device they were made on. Restoring a backup to a new iPhone or iPad asks you to connect it again, so the old and new devices don't share one sign-in.
- Where you got to in a film or episode is saved even when you leave the app straight after stopping.
- Signing out of the iPhone app sends anything watched offline that hadn't reached the server yet before the downloads are removed.
- On iPhone, tapping an episode in Home's **New podcasts** opens it (its notes, Play and download) instead of playing it straight away.
- On iPhone, an episode's page shows **Pause** while that episode is playing, and **Resume** carries on from where it is.
- On iPhone, the playing bar above the tabs has a close button on its left. It stops what's playing, keeps a podcast episode's place, and takes the bar away.
- Playing a podcast episode that's already playing or paused carries on from where it is, instead of going back to where it was when the screen opened.
- Downloads on your iPhone are kept through app updates: if the app can't read one saved download, it keeps the rest instead of starting with an empty list.
- Discover and downloads keep working when a newer server reports a request state or download quality the app doesn't know yet.

### Web
- Dialogs (Fix match, Delete, Add to playlist or collection, the collection editor, trailers) keep the keyboard inside them while they're open, and put it back where it was when they close.
- **Settings › Logs** stays smooth while lines arrive quickly in the live view.
- A song or podcast episode that won't play is tried once more from where it stopped. If it still won't play, Flexarr says so in the player bar and moves on to the next one, instead of stopping silently.
- A video that keeps failing to decode now says so, instead of trying again for ever.
- Going from one title to another (a Similar title, say) no longer briefly shows the previous title's details and Play button.
- Space and Enter on a focused player button (Subtitles, Quality) now use that button, instead of pausing.
- Flexarr starts in browsers that block storage, such as some private windows.
- When error reports are switched on, the web app's reports now reach Flexarr's developer. The browser was blocking them before. They now go through your server, so Sentry no longer sees the address of whoever's watching.

### Privacy and security
- A Mac server only installs an update by itself when the update is signed by Flexarr's release key, so a release slipped onto GitHub by someone else can't install itself overnight. Updating with the install script still works as before.
- A profile kept out of a library no longer sees what's playing from it on the household's other screens. The device shows as playing **Something private**, with no title or artwork, in the device list, Home Assistant events and the AI assistant.
- A phone, tablet or TV that hasn't been used for a year is signed out, so one that was lost, sold or given away stops working. Signing it in again takes a new code.
- Phones signed in with the server's API key can't change or test each other's notifications when more than one of them is set up.
- One podcast import (OPML) runs at a time on a server.
- If a Sonarr, Radarr or Seerr address redirects to another site, Flexarr no longer follows it, so their API keys aren't passed along. Redirects on the same server (to https, say) still work.
- Podcast feeds can no longer reach your home network through IPv6 addresses that carry a home network address inside them (NAT64, 6to4).
- A device can send diagnostics six times an hour, and diagnostics have their own 100 MB of space, so they can't crowd out the server's own log.
- **A new server asks for its setup code.** Setting up Flexarr in a browser now needs the eight-character code the server writes in its log as it starts (`docker logs flexarr` with Docker; the Mac app shows it in its menu). Behind some reverse proxies and Docker Desktop, visitors from the internet can look as though they're on your home network, and the code makes sure only whoever has the server can claim it. After ten wrong tries the code changes. The iPhone and Apple TV apps say to type it in when the server asks for one.
- An AI assistant connected to Flexarr (MCP) no longer sees titles from libraries hidden from its profile.
- When Flexarr plays, downloads or scans a video, it only reads the file itself. A file that's really a playlist in disguise can no longer make it fetch addresses on your network.

## 0.51.1 · 28 October 2026

### Web
- Right after a library's first scan, Home shows what it found instead of saying the library is empty, and the rows under that message no longer overlap its buttons.
- Adding your first library no longer shows an empty box under the form.

## 0.51.0 · 28 October 2026

### Libraries
- **Films and shows without Sonarr or Radarr get artwork and details** from the files beside them. Flexarr reads the `.nfo` files that Kodi, Jellyfin, Emby and tinyMediaManager write (title, year, plot, genres, rating, age rating), and artwork such as `poster.jpg`, `folder.jpg`, `fanart.jpg`, `clearlogo.png`, `season01-poster.jpg` and an episode's `-thumb.jpg`. Folders kept for Plex or Jellyfin show up in Flexarr looking the same.
- **What's in your folders wins**: a poster or `.nfo` you've put beside a film or show is used over Sonarr's and Radarr's. The IDs in a `.nfo` also help Flexarr find the right title in them. Flexarr only reads these files and never changes them.
- **Shows Sonarr doesn't know are looked up on TVmaze**, with no account or key: posters, backgrounds, overviews, genres, ratings and cast, and episode titles, descriptions and stills.
- **Films Radarr doesn't know can come from TMDB**: paste a free TMDB key under **Settings › Connections › TMDB** and films that weren't matched are looked up straight away, with posters, logos, overviews, genres, age ratings, trailers and cast.
- **Fix match works without Sonarr and Radarr**: it searches TVmaze for shows and TMDB for films (with a key), and keeps your choice in Flexarr. Titles matched by a TVmaze or TMDB search are listed under **Check these matches** in Library health.
- **Home videos**: a new kind of library for videos of your own, arranged by folder and never looked up online. Each folder shows like a show, the folders in it as its seasons, and each video as an episode named after its file, in name order. Videos get a still from a little way in, unless there's a `-thumb.jpg` or a `poster.jpg` beside them. Home videos don't get Skip intro, so videos in a folder aren't mistaken for episodes with a shared opening. They show on the web, iPhone, iPad and Apple TV.
- The guide has a new page, **Without Sonarr and Radarr**.

### Connections
- **Media folders** replaces Path mappings in **Settings › Connections**. Each of Sonarr, Radarr and Lidarr's folders has a line saying where it is on this server. When the apps run in Docker and see your media under other paths, Flexarr usually works out where it is by comparing the titles both sides have, and saves it on the next library scan, so there's nothing to type.
- When Flexarr isn't sure, the folder's line suggests one (**Use this**) or lets you pick it with **Choose folder**. The app's own line says when one of its folders hasn't been found.
- Saving a mapping or a connection gives titles that weren't linked to Sonarr, Radarr or Lidarr another try. You can still write mappings yourself with **Edit by hand**.

### Home
- **One Latest unwatched row for each kind**: with more than one film or TV library (say **TV** and **Anime**, or **Films** and **Films in 4K**), Home now shows one **Latest unwatched films** row and one **Latest unwatched shows** row covering all of them, instead of a row for each library. A title in two libraries shows once. This is on by default, on the web, iPhone, iPad and Apple TV. To give each library its own row again, turn off **Combine libraries of the same kind** in **Settings › Home and Browse**.
- **An empty library says so**: when you've added a library but Flexarr hasn't found anything in its folders, Home now says **Your library is empty**, shows the folders it looked in, and suggests adding films or shows there or connecting Sonarr or Radarr. Before, Home was just blank. During a library's first scan it says Flexarr is still looking through the files instead. This is on the web, iPhone, iPad and Apple TV.

### Continue Watching
- **Mark as unwatched** works on anything you're part way through, on the web, iPhone, iPad and Apple TV: it forgets your place without marking it watched, so it leaves Continue Watching and starts from the beginning next time. **Mark as watched** is still there beside it.
- **Remove from Continue Watching** on a show's next episode in that row takes the show out of it until you play another episode. An episode you were part way through and marked unwatched does the same, so the show doesn't come straight back.
- **Podcasts**: **Mark as unplayed** on an episode you're part way through forgets your place and takes it off the listening list, on the web and iPhone. It stays in Up Next. A downloaded episode on the iPhone starts from the beginning too.
- **Mark [show] as watched** on an episode's long-press menu on iPhone, iPad and Apple TV marks every episode of the show, as the web's card menu already could.
- On Apple TV, when a title leaves a Home row (marked watched or unwatched, or removed), focus moves to the one that takes its place instead of jumping back to the start of the row.
- Assistants connected over MCP can do both: `mark_watched` with unwatched forgets the place, and `remove_from_continue_watching` is new.

### Updates
- **Settings › Updates** shows the version your server runs and whether there's a newer one, with what's changed in it. Flexarr checks for one once a day, and Settings shows a dot when an update is waiting.
- **On a Mac**, choose **Update now** to download and install a new version without the install script. It brings the .NET runtime up to its latest patch too, as the install script does. If the new version doesn't start, the one you had is put back, with your database as it was. **Install updates automatically** (off unless you turn it on) does it between 4 and 5 in the morning, when nothing is playing, being watched live or recording.
- **In Docker**, the page shows the commands to update the container, since a container can't replace itself.

### Mac
- On a Mac with the Tailscale app but not its command-line tool, the web app no longer says it can't reach the server. Settings › Access says to install Tailscale's command line (Tailscale › Settings › Install CLI) for remote access.

### Docker
- **Found on your network**: Flexarr now announces itself without any extra software, so the iPhone, iPad and Apple TV apps list a Docker install by themselves. Run the container on the host's network (`network_mode: host`) for this; Docker's own network doesn't pass the announcement on.
- **`PUID` and `PGID`** pick the user Flexarr runs as, as with Sonarr, Radarr and Lidarr. A settings folder Docker created as root is handed over when Flexarr starts, so it no longer fails to write there. `--user` still works as before.
- **Intel and AMD graphics** can convert video: the image now includes the VA-API drivers, and Intel's Quick Sync ones on 64-bit Intel/AMD machines. Pass `/dev/dri` into the container. Settings › Streaming only offers VA-API and Quick Sync when the container has a graphics device.
- Settings › Access says Tailscale isn't available in Docker, with what to do instead, rather than asking you to install it on the server.

### Apple TV
- **Enter an address instead** connects with a code, like a server found on your network, so there's no API key to type with the remote. The key is still there behind **Connecting from outside your home? Use an API key**.
- The app opens on Home with the sidebar closed and **Play** on the featured title ready to press, rather than in the sidebar.

### iPhone and iPad
- Settings › Server and the sign-in error point to **Settings › Access** on the web, where remote access and the API key are now.

### Website
- **[flexarr.io/apps](https://flexarr.io/apps)** lists every Flexarr app and platform with its version and status, from what you can download today to the Android apps on the way, with a link to get each one.
- **flexarr.io on a phone** is under a third of the length: get the iPhone app and set up the server, right under the headline, with a button that sends the setup steps to your computer, then a swipe through what Flexarr does. **[flexarr.io/install](https://flexarr.io/install)** goes straight to the setup steps.

### Web
- A new server's settings read **Main profile**, not "Main profile's profile".

## 0.50.0 · 28 October 2026

Not released on its own: its changes are in the next version, under Unreleased above.

## 0.49.3 · 27 October 2026

### iPhone, iPad and Apple TV
- When Flexarr asks to use your local network, it now says why: to find your Flexarr server at home so you can connect without typing an address, with an example.

## 0.49.2 · 27 October 2026

### Website and guide
- **A guide at [flexarr.io/guide](https://flexarr.io/guide)**: 31 step-by-step articles, from installing on a Mac, Synology, Unraid, Linux or Windows to connecting your devices and \*arrs, live TV, podcasts, Siri, Home Assistant, backups and troubleshooting, with search. The source is in `docs/guide`, so corrections are a pull request away.
- flexarr.io has been redone around Apple TV and iPhone, with every feature, a "Just got the iPhone app?" starting point for anyone new to running a server, setup steps for each kind of computer, and answers to common questions.
- The quick start keeps Flexarr's settings in a Docker volume, so it works without setting folder permissions first.
- The README is shorter and points to the guide for the details.

## 0.49.1 · 27 October 2026

### Error reports
- Reports sent while Flexarr is being developed or tested are now kept apart from reports from real use, so problems you hit show up clearly.

## 0.49.0 · 27 October 2026

### Setting up
- A new server needs no API key: open it in a browser on your home network and choose **Set up Flexarr**, and that browser becomes its owner. Phones, tablets and TVs then connect with a code you enter there. If the iPhone app finds a server nobody has set up yet, it says to do that first, with the address to open. Servers that already exist aren't affected.

### iPhone
- A fresh install opens with three short cards: what Flexarr is, that it needs your own Flexarr server running at home (completely free, and set up in less than a minute at flexarr.io), and how the app finds it on your Wi-Fi. Skip them if you already have a server.
- While the connect screen hasn't found a server, it says what Flexarr needs and links to the setup guide, and its "can't see your server?" hint comes up sooner.
- **Enter an address instead** connects with a code too; the API key is only offered for connecting from outside your home.
- Connecting by code is a short numbered list: open the server's address on your computer, go to Settings › Access › Connect a device, and type in the code shown large underneath. Approve in Safari is a quiet link for when Safari on the phone is already signed in.
- On a server with nothing added yet, Home says where to add your movies, shows and music instead of staying blank.

### Apple TV
- Connecting by code shows the same short numbered steps as the iPhone: open the server's address on your computer, go to Settings › Access › Connect a device, and type in the code. On a server nobody has set up yet, it says to do that first. The QR code is still there, smaller, for a phone that's already signed in.

### Web
- **Settings › Access › Connect a TV** is now **Connect a device**, since iPhones and iPads connect there too.
- Approving a device by its code talks about the device, not "your TV", since iPhones and iPads connect the same way. Opened on a browser that isn't signed in, the page says to enter the code on the computer where you set up Flexarr, instead of asking for the API key.

## 0.48.0 · 27 October 2026

### iPhone
- After you've finished a few films or episodes on more than one day, the iPhone app may ask whether you're enjoying Flexarr, using Apple's own star-rating box. It asks at most once a version, never during playback, and iOS limits it to three times a year.
- **Settings › Rate Flexarr** opens the App Store so you can rate or review Flexarr whenever you like.

## 0.47.1 · 27 October 2026

### Logs
- What the apps and browsers report is kept to one line per entry, so a report can't pass itself off as a line from the server. Errors they report keep their full stack trace (up to 8 KB, not cut at 200 characters as before), still shown line by line in Settings › Logs.

## 0.47.0 · 27 October 2026

### Logs
- Error reports in Settings › Logs show a green or red dot for on and off, and what's sent folds away under the switch; your browser remembers whether it's open.

## 0.46.0 · 27 October 2026

### Home Assistant
- The Home Assistant integration has its own home: [flexarr-home-assistant](https://github.com/danleach-glitch/flexarr-home-assistant). Install it through HACS (add that address under Custom repositories), so Home Assistant offers its updates. Copied in by hand before, it keeps working; remove the old copy and install it through HACS to get updates.

### Logs
- New **Settings › Server › Logs**: the server's log, newest first, filtered by level and area, searchable, and **Live** to watch it as it happens. **Download diagnostics** makes a zip to attach to a bug report.
- Turn up the detail for one area (Playback, Library scans, Live TV…) while you chase a problem; it applies at once, no restart.
- Every playback has a short id that shows on the server's lines and in what the apps report, so you can follow one from start to finish.
- The Apple TV and iPhone apps send their warnings and errors to your server, and the web app its errors. **Send diagnostics** in an app's settings sends its full recent log.
- Keys, passwords and tokens are taken out of everything logged. Logs never leave your server.
- **Error reports**, off unless you turn them on in Settings › Logs: errors and crashes from the server and its apps are sent to Flexarr's developer (through Sentry) to get bugs fixed sooner. Titles, file names, what you watch, names, addresses and keys are removed before anything is sent; the setting lists exactly what goes and what doesn't.
- Logs now have timestamps, start a new file each day, and are kept for 14 days up to 100 MB (both adjustable). Routine chatter is gone: the old log was mostly lines about every call to Sonarr and Radarr.

## 0.45.0 · 27 October 2026

### Home Assistant
- Flexarr has a Home Assistant integration (in `clients/home-assistant`; copy it into Home Assistant's `custom_components` folder). Set it up with the server's address and the API key from Settings › Access.
- Each Apple TV, iPhone and iPad becomes a media player: see what it's playing, with artwork and position, and play, pause or stop it. `media_player.play_media` with a title's id plays it there; a show plays the episode that device's profile is up to.
- Automations can follow what's playing: each device has triggers for started, paused, resumed, credits, ended and stopped, so the lights can dim when a film starts and come up at the credits. The `flexarr_playback` event covers browsers too, with the title, kind (film, episode, music or podcast) and profile.
- For other automation tools, the server streams the same events at `GET /api/v1/events`.
- Resuming on the Apple TV, iPhone or iPad is reported straight away, so a paused film shows as playing again at once rather than up to ten seconds later.

## 0.44.0 · 27 October 2026

### Backup and restore
- Settings › Backup and restore keeps backups of everything Flexarr can't rebuild from your media: profiles, what everyone's watched, playlists, collections, settings and signed-in devices. Artwork and previews are left out; they come back by themselves.
- A backup is made every night at half past three (the last 7 are kept), and always before an upgrade changes the database. Back up now makes one whenever you like.
- Backups can go in a folder of your choosing, such as a share on another drive. If that drive isn't connected, the page says so rather than filling this one.
- Download a backup to keep it elsewhere, protected with a passphrase if you like. Restore one from the list or from a file: Flexarr keeps a copy of the server as it is first, then restarts with the backup.
- A backup is checked before anything is restored. A damaged one, one from a newer Flexarr, or one that isn't Flexarr's own is refused, and one from another server is pointed out. This machine's paths (ffmpeg, the cache) are kept as they are.

### Settings file
- Export the server's settings and libraries to a file, and import them on this server or another. Choose which parts to take. Library folders can be changed to where they are on the new machine.
- Server keys, access settings and this machine's paths start unticked, and taking them from a file asks for the main profile's PIN.

## 0.43.1 · 27 October 2026

### Discover (Apple TV)
- A title you already have is marked with a gold play badge on its poster's top-left corner, in place of "In your library" under it.
- After pressing Request, Menu goes back to Discover. Before, it left the app.

## 0.43.0 · 27 October 2026

### Discover (Apple TV)
- Discover is in the sidebar, above Search, as on the web. It shows what's trending, popular films and shows, and what's coming to cinemas, each marked if it's in your library or already requested.
- Open a title you don't have to see its ratings (TMDB, IMDb, Rotten Tomatoes), cast, director or creator, and more like it, and press Request to ask Seerr for it. One you already have opens as itself.
- Search, at the top of Discover next to Browse, opens straight to the keyboard and finds any film or show, not just what's in your library, so you can request it. Menu comes back to Discover.
- Your library by genre, five across: pick one to see everything in it, best rated first, and narrow it to films or shows.
- Without Seerr connected, Discover shows your library by genre, and says where to connect Seerr to request new titles.

### Settings (Apple TV)
- Watching as shows your profile picture next to your name.

## 0.42.0 · 27 October 2026

### Rename libraries
- Settings › Libraries has a Rename button on each library. The new name shows in the sidebar and on every device straight away.
- Renaming a library no longer rescans it. Adding a folder scans just that folder.
- A library's buttons are now icons (rename, scan, refresh artwork and details, remove), named when you hover over them. Scan keeps spinning while the scan runs.

### Times are right in every time zone
- "Scanned 4 h ago" a moment after a scan, and other times like it, were out by your time zone's difference from UTC. They're now right wherever you are.

### Buttons show they're working
- Across the web app, a button that has to wait on the server (save, refresh channels, test a connection, delete and so on) now shows a spinner until it's done, instead of looking as though nothing happened.
- Edit, remove, rename, delete and test are icons now, in Settings, on playlists and collections, and on recordings. Hover over one to see what it does.

### Connections and Live TV settings
- Each app's Open link is now an icon beside its name, and goes to the server by name (http://your-server:8989) rather than localhost, so it works from any computer at home.
- Live TV has its own page in Settings for rewinding and recording. Connections keeps just Dispatcharr's address and login.
- Changing a Live TV setting no longer fetches every channel and the whole guide again. That only happens when Dispatcharr's address or login changes.

## 0.41.0 · 27 October 2026

### Sidebar (Apple TV)
- The tabs along the top are now a sidebar down the left, like the Apple TV app's, so any number of libraries fits. Before, with more than five or six, the tab bar ran off the screen and took Search and Settings with it.
- Who's watching is at the top, with your profile picture: select it to switch. Then Home, your libraries and Live TV, and a line before Search and Settings at the bottom.
- Press left, or Menu, to bring the sidebar back from a screen. Moving through it doesn't change the screen until you select.
- Selecting a library puts you on Play, or on its first title in All, rather than on the Browse · All switch at the top. Up from there still reaches the switch.
- The Flexarr logo and version are at the foot of the sidebar on tvOS 27. The version is still in Settings too.
- Settings › Tab bar is now Settings › Sidebar.
- A title opened from the Top Shelf, or a link, while Flexarr is already open gets focus on its Play button. Before, focus could stay in the sidebar, where Menu left the app instead of going back.

### Live TV (Apple TV)
- Recordings is now right under Favourites, above All channels and the source's groups.

### Settings (Apple TV)
- Settings › Home screen lists Posters, the default for the Top Shelf, above Wide artwork.

## 0.40.0 · 27 October 2026

### Notifications (iPhone and iPad)
- Get a notification when something new is ready: a film or show you haven't seen, new episodes, a podcast episode from a show you follow, or a recording that's finished. Turn them on, and choose which, in Settings › Notifications.
- Several episodes of a show arriving together are one notification, and a big batch is summed up in one, so a season pack or a new library doesn't flood your phone. Upgrades and replacements of things already in the library don't count as new.
- "Your request is ready": whoever asked for a film or show from Flexarr is told when it arrives.
- Upcoming (off to start with): each morning at the hour you choose, what's airing or out today from the shows you watch and the films you asked for.
- Buttons on the notification: Play, Download, and Play on Apple TV. Podcasts have Play and Download.
- Each phone gets notifications for one profile, the one it was on when they were turned on, so a phone the household shares gets one set, not one for each profile.
- Notifications travel through a relay at flexarr.io to reach Apple, sealed so that only your phone can read them: the relay never sees what's in them. A server can turn them off with `Flexarr:Notifications:Enabled=false`.
- These replace the podcast-only notifications: turn on Settings › Notifications to keep getting new episodes.
## 0.39.0 · 27 October 2026

### Licence
- Flexarr is now licensed under the PolyForm Noncommercial License 1.0.0 instead of the GPL-3.0. It stays free to use, change and share for any noncommercial purpose, but it can't be sold or used in a commercial product without a separate licence.
- New ACKNOWLEDGEMENTS.md credits the projects, libraries, fonts and data sources Flexarr is built on.
- The repository has a Sponsor button.

## 0.38.1 · 27 October 2026

### Navigation (web, iPhone and iPad)
- Live TV has a broadcast antenna for its icon, as on the Apple TV, so it's no longer mistaken for a TV Shows library.
- Discover's icon is now sparkles instead of a compass.
- Playlists have their own icon on the web, a list with a note, as on iPhone and iPad, instead of the Music library's note. Empty playlist covers use it too.

### Live TV
- Recordings no longer appear as a library on the web, iPhone, iPad or Apple TV once Live TV is set up: not in the library list, Settings › Libraries or Settings › Profiles, and with no "Latest unwatched" row on Home. They're in Live TV › Recordings. Without Live TV, they stay a library so they can still be found.
- A new TV library can't be called Recordings, which is the name Live TV's recordings library uses.

## 0.38.0 · 26 October 2026

### Libraries (Apple TV)
- Movies and TV Shows open on Browse, as on the web: a hero of the library's own titles, then Continue watching, the latest, the best not yet watched and a row for each of its biggest genres. "All films" (or "All shows") at the top switches to every title.
- A row's See all opens every title already sorted or filtered to match, including a genre's row. Menu comes back to the rows.
- Every title, sorted A to Z, has a letter rail down the right: press right from the posters, pick a letter, and the grid jumps to it.
- Choose which view a library opens on in Settings › Libraries.
- Put your libraries and Live TV in the order you like in the tab bar (Settings › Tab bar): select one, move it up or down, and select again. Home stays first, and Search and Settings last.
- The Music and Live TV tabs no longer repeat their name at the top; the tab bar already shows where you are.
- Search in the tab bar is a magnifying glass, like the Settings gear, so the bar stays short.
- Recordings has no tab of its own once Live TV is set up: they're in Live TV › Recordings.
- On Home, a long-press's Go to opens the title on its library's tab, so Home stays Home: what you play from it, and Menu afterwards, no longer leaves Home showing the title.

## 0.37.1 · 26 October 2026

### Top Shelf (Apple TV)
- A title opened from the Top Shelf opens on its library's tab (an episode on TV Shows, a film on Movies), with that tab lit in the bar, so Home stays Home: choosing Home in the bar takes you there. Menu from the title goes back to the library. Leaving the app and coming back no longer finds the title still open. Before, the title took Home's place and Home couldn't be reached again without closing the app.

## 0.37.0 · 26 October 2026

### Settings (Apple TV)
- Settings is back in the tab bar, as a gear at its end. The profile picture in the corner opens Who's watching? straight away again, rather than a menu.
- Settings on the Apple TV is a short list of pages, like the Apple TV's own Settings: Playback, Home screen, Profiles and Server. Each row says how it's set now, and what the row you're on does shows on the left.
- Your profile's playback settings can be changed on the Apple TV: auto-play the next episode, skip intros, recaps and credits, and subtitle size. They're the same ones as on the web and iPhone, so a change follows you everywhere.
- Choose how far a click on the left or right of the remote skips: 10, 15 or 30 seconds.
- An Apple TV everyone shares can ask who's watching each time Flexarr opens, instead of opening as whoever watched last (Settings › Profiles).

## 0.36.0 · 26 October 2026

### Top Shelf (Apple TV)
- The Top Shelf can show Continue Watching as wide artwork with each title's logo on it, instead of posters. Choose it in Settings › Home screen on the Apple TV. Titles without a logo show their artwork alone.
- Opening a title from the Top Shelf no longer traps Home on that title. Menu now goes back to Home, even from the tab bar, where before it left the app and Home kept showing the title until the app was closed.

## 0.35.1 · 26 October 2026

### Siri
- Siri is back to films, shows and podcasts. Its Live TV phrases from 0.35.0 ("Put on BBC One", "What's on BBC One") clashed with the film and show phrases and didn't find channels, so they're gone. Putting a channel on another device, and assistants' Live TV and recording tools, stay.

## 0.35.0 · 26 October 2026

### Live TV with Siri (iPhone, iPad and Apple TV)
- "Put on BBC One in Flexarr" puts the channel on: here, or on the TV chosen in Settings › Siri plays on (iPhone and iPad). "Watch", "Switch to" and "Turn on" work too. Siri knows your channels by name, favourites first, and hears "BBC 1", "Channel four" or "Film four" as well.
- "What's on BBC One in Flexarr" says what's on now and what's next, without opening anything.
- Recording what's next on a channel is an action in the Shortcuts app, to give a phrase of your own (Siri's own phrases for Flexarr are all in use).
- Other devices and assistants can put a channel on an Apple TV, iPhone or iPad with Flexarr open, as they can play a film or show.

### Assistants (MCP)
- An assistant connected to Flexarr can see what's on every channel, put a channel on a device, set a programme or a series to record, and see what's recording and what's been recorded.

## 0.34.2 · 26 October 2026

### Live TV (iPhone and iPad)
- Tapping a programme in the guide now opens that programme. Before, a tap could land on the last programme in the row, often one off the edge of the screen, so Record set the wrong one and nothing seemed to change.

## 0.34.1 · 26 October 2026

### Recording (DVR)
- What's set to record now shows everywhere you'd look: a red dot on the programme in On now and the iPhone's channel list (now and next), and a REC badge while it's recording. In the guide, a programme set to record is outlined in red as well as dotted, on the web, Apple TV, iPhone and iPad.

## 0.34.0 · 26 October 2026

### Recording (DVR)
- Record from the Live TV guide: choose a programme, then Record, or Record series for every showing of it (on that channel or any, new episodes only if you like). Programmes set to record carry a red dot; ones a series covers, two dots.
- Flexarr records the channel as it comes, picture and every sound track (5.1 included), nothing converted, into a DVR folder on the drive your TV library is on (Settings › Connections › Dispatcharr to choose another). Recordings start a minute early and run five minutes on, as broadcasts drift; both can be changed.
- Recordings appear in a Recordings library, named like TV episodes, and play like anything else, with resume and Continue watching.
- Live TV › Recordings lists what's coming up, the series being recorded (keep all or only the latest few), and what's been recorded, with why anything didn't record.
- Recordings and live viewing share the channels your IPTV login allows (watching and recording the same channel counts once). A recording takes priority: if someone's watching another channel when it's due, that channel stops with a message; and while every stream is recording, trying another channel says what's recording and offers to watch that channel instead, or stop the recording. Two recordings that would need more streams than there are are flagged as clashing when you set them.
- No new recording starts when the recordings drive has less than 100 GB free (changeable), or isn't connected; the Recordings list says why. A recording under way when Flexarr restarts carries on afterwards.

## 0.33.1 · 26 October 2026

### Playback (iPhone and iPad)
- Picture in Picture works for films and TV shows again. The button was there but greyed out, because it was set up before the player had opened the file; it now turns on as soon as playback starts.

## 0.33.0 · 26 October 2026

### Live TV
- Surround sound on Apple TV, iPhone and iPad. Channels that broadcast Dolby Digital or Dolby Digital Plus (Channel 4, 5 and others, for films and big dramas) now come through as they are, so a soundbar or receiver gets the 5.1. Before, it was turned into stereo. The web player and watching away from home stay stereo.

## 0.32.0 · 26 October 2026

### Settings
- Saving settings now waits until the server has taken them up, so what you see straight after saving is what's saved. On a slower machine it could briefly show the old value.

### Live TV (Apple TV, iPhone and iPad)
- Apple TV: Live TV is now a guide: categories down the left, and for the one chosen, channels down and three hours across. Move between programmes with the remote; past either edge it moves on an hour and a half. What's on now plays; anything later shows what it is, with a way to watch that channel now. Press and hold a programme to add its channel to Favourites or take it off; favourites carry a star.
- Apple TV: the guide without leaving the channel. The player has a Guide button that opens the grid over the picture, which carries on behind it, starting on your channel at what's on now. Choose something else on now to change to it; Menu goes back to the channel.
- The guide no longer shows two programmes at once on a channel, and BBC One and ITV2 are at the right times again. The IPTV guide had both channels twice, one copy an hour early from before the clocks went back; Flexarr now drops that copy, and programmes that run into the next by a minute or two end where it starts.
- iPhone and iPad: a Guide button on Live TV turns the list into the guide, channels down the left and six hours across, for the category chosen. Tap what's on now to watch; tap anything later to see what it is; hold a programme to add its channel to Favourites. The List button goes back, and it remembers which you used.
- Home's Live TV row starts with the channel you watched last, so it's one click to get back to it, then your favourites. Each profile has its own.
- Apple TV: while the guide or the channel list is open over the picture, moving up and down in it no longer changes channel behind it.
- The player has a Channels button: every channel over the picture in number order, each with what's on. At the top is Last watched, the channel you were on before, and the list opens on it, so flipping between two channels is one press. On Apple TV it's a panel down the right; on iPhone and iPad a sheet.

## 0.31.0 · 26 October 2026

### Live TV
- Away from home (over Tailscale or the internet), channels come in a reduced version: at most 720p at about 2.5 Mbps with stereo sound, so they keep playing on hotel Wi-Fi or a phone's data. At home they still play as they come. Someone at home and someone away can watch the same channel at once on one provider connection.

## 0.30.1 · 26 October 2026

### Changed
- Live TV: Favourites is always listed first (Apple TV, iPhone, iPad and the web), before All channels, saying how to add some while there are none. It's still where Live TV opens once you have favourites.

### Fixed
- Saving the Live TV source in Settings › Connections brings the channels in straight away; before, the first refresh could run before the new settings were in and find nothing.

## 0.30.0 · 26 October 2026

### Live TV (web)
- Watch TV channels from Dispatcharr: connect it in Settings › Connections (beside Sonarr, Radarr and Seerr) with a Dispatcharr user of Flexarr's own, test the connection, and the channels and guide come in (and are refreshed every six hours). How far back live TV can be rewound is set there too.
- Live TV in the sidebar: On now is a compact list of every channel with what's on, how far through it is and what's next, filtered by group (so hundreds stay quick to scan); the Guide shows the next six hours (or up to a day) as a grid, with details for anything later.
- Favourites: star the channels you watch most. Each profile has its own, and Live TV opens on them; the Guide can show just them, and the player lists them first.
- The player rewinds live TV (by default up to an hour back, while anyone watches the channel), shows how far behind live you are with a Jump to live button, and changes channel with the arrow keys or the channel list.
- Channels that are already H.264 play as they come; interlaced, MPEG-2 and HEVC channels are converted (deinterlaced to smooth 50 frames a second), and Dolby Digital and MP2 sound becomes AAC.
- Channels take current logos from the community collection (tv-logo/tv-logos on GitHub) by default, at their own proportions, matched by name and country; channels it doesn't have keep Dispatcharr's.
- Channels added or removed in Dispatcharr show up within five minutes (Dispatcharr can't tell Flexarr itself, so Flexarr looks); the guide still refreshes every six hours.
- One channel is shared by everyone watching it, so a provider that allows one connection still serves the whole house. Changing channel lets the old one go first, and a channel that can't start says why ("No connection free", "isn't sending this channel at the moment").
- Home has a Live TV row of your favourite channels and what's on them (on the web, Apple TV, iPhone and iPad); it can be moved or switched off in Customise display.

### Live TV (Apple TV, iPhone and iPad)
- Apple TV: a Live TV tab with your categories down the left (Favourites, All channels and the source's groups) and a compact list of channels on the right, each with what's on, how far through, and what's next, so even hundreds stay quick to move through. Select to watch; press and hold to add or remove a favourite.
- iPhone and iPad: Live TV under Libraries, a list of channels with Favourites first; swipe right (or press and hold) to add or remove a favourite.
- The player's timeline is the rewind window: it shows the time of day, LIVE or how far behind you are, and Back to live once you've rewound. Previous and Next change channel, and on the Apple TV so does clicking the top or bottom of the remote's touch surface while the controls are hidden.

## 0.29.0 · 26 October 2026

### Settings on the web
- Settings is in two groups: your profile's own (Playback and Podcasts) and the server's. Every profile can now open Settings and change its own playback and podcast settings; the server's pages stay with the main profile.
- A new Overview page shows who's watching, what the server is working on, and how libraries, health, connections and remote access stand, each a click from its page.
- Pages are regrouped: Library health, Bring over from Plex and the podcast folder and country live under Libraries; video conversion is its own Streaming page; remote access, the API key and signed-in browsers and TVs are together under Access (with Connect a TV); webhooks are under Connections; the version, storage and changelog are under About, showing the latest release with older ones a click away.
- Sonarr, Radarr, Lidarr and Seerr each take one line saying how they are, opened with Edit to change them.
- Settings save as you change them, Home and Browse rows included; forms like a connection's address keep a Save button.
- Home and Browse rows can be dragged into order (the arrows still work from the keyboard and on touch screens).
- Editing a profile is simpler: picture, name and colour together at the top, the PIN fields only once you add or change a PIN, and a switch for each library it sees. Profiles are listed in one panel.
- The main profile, which manages the server, has a crown: beside its name in Settings › Profiles, and on its picture when choosing who's watching.
- A plainer, tighter look: titles in the text face, switches for every on/off setting, one panel per list, and Library health leaves out groups with nothing in them.

## 0.28.1 · 26 October 2026

### Fixed
- With two devices on one profile (an iPhone and an iPad, say), whichever downloaded a podcast episode, film or album second was told it had "run out on the server". Each device now gets its own copy, and new episodes download by themselves on both.

## 0.28.0 · 26 October 2026

### Changed
- iPhone and iPad: Settings is a short list of pages (Playback, Downloads, Podcasts, Siri and Server), each showing how it's set now, such as the space downloads use or where Siri plays. Inside, each page says which settings are your profile's on every device and which are this device's own.

## 0.27.0 · 26 October 2026

### Podcasts
- Up Next: queue episodes with Play next or Add to Up Next, and they play in that order on any device: the web and the phone share one queue. It's on the Podcasts page, where episodes can be moved or taken out; one that starts playing or is played leaves it.
- iPhone and iPad: Up Next too (Play next and Add to Up Next on episodes, a reorderable Up Next on the Podcasts screen, and in Now Playing).
- Siri and Shortcuts: "Play <show> in Flexarr" plays the episode you'd hear next; also "Play the latest <show>", "What's new in my podcasts", "Add <show> to Up Next" and "Play my Up Next".
- Notifications when the shows you follow have new episodes (Settings › Podcasts), with a Play button.
- Widgets: Continue listening (Home Screen and Lock Screen) and Up Next.
- CarPlay: Up Next, New episodes and your podcasts in the car, with speed and skips. It turns on once Apple approves Flexarr for CarPlay.
- Settings › Downloads: "Automatic downloads on Wi-Fi only" can be turned off, to let them use mobile data.
- Assistants (over MCP) can list the podcasts you follow and their new episodes, queue episodes in Up Next, and play a podcast on a device (the episode you'd hear next).

### Changed
- Home's row of new podcast episodes is called New podcasts.

## 0.26.0 · 26 October 2026

### Downloads on iPhone and iPad
- Downloaded things are marked with a green tick everywhere: podcast episodes, tracks, albums and playlists, films and episodes, and the Downloads tab.
- Each stage looks its own: an hourglass while the server gets it ready ("Getting ready"), Wi-Fi while waiting for Wi-Fi, a filling ring only while it's really coming down, and an exclamation if it failed (tap for why, and Try again).

### Fixed
- Podcast episodes the phone fetches by itself wait for Wi-Fi, never mobile data or Low Data Mode, and say so; one you download by hand still comes over mobile data. A transfer that loses Wi-Fi stops and carries on later.
- A podcast episode deleted from a phone is never downloaded to it again by itself (downloading it by hand still works), and the server's copy goes.

## 0.25.0 · 26 October 2026

### Podcasts
- A show can be listed oldest first, for ones heard in order. A followed show remembers its order, on the web and the phone. Play then starts the oldest you haven't heard, and phones keep the next ones to hear rather than the newest.
- Marking an episode played or unplayed moves it at once: off Unplayed, or out of New episodes.
- The row of shows you follow is just their artwork, the most recently updated first.
- iPhone and iPad: a ⋯ menu on a show's page with Newest first, Oldest first and Mark all as played. New episodes rows are shorter: no notes, and Play beside the title.

### Fixed
- iPhone and iPad: downloaded podcast episodes were listed under Films; they're under Podcasts.
- Deploying to a Mac no longer replaces the program that runs Flexarr when .NET updates, which made macOS withdraw its access to an external media drive (scans and downloads stalled until it was given again).

## 0.24.0 · 26 October 2026

### Podcasts
- Podcasts are built in: no library to add. Settings › Podcasts holds their settings, which country's charts to show, and the folder episodes wait in on their way to phones (deleted once the phone has them). Podcasts sit with the libraries in the sidebar, and a profile can leave them out.
- Browse finds shows in Apple's podcast directory: top charts by category, and search. Paste a feed's address to add a show Apple doesn't list. Settings › Podcasts chooses whose charts (the server's country unless you pick another).
- Follow a show and it joins the row of shows you follow under Podcasts, above Continue listening and the newest episodes you haven't heard. A show's page lists unplayed episodes first, and Mark all as played clears a back catalogue in one go. Each profile follows its own shows. Flexarr checks feeds by itself, more often for shows that publish often.
- Episodes play in the music bar, from where you left off on any device, with 15 seconds back and 30 on, a speed from 0.8× to 3×, and a sleep timer. When one ends, the show's next episode plays, or else the newest you haven't heard.
- Home has a New podcast episodes row, after Continue watching: the newest episodes you haven't heard from the shows you follow.
- Podcast settings are each profile's own and cover every show: whether phones download new episodes, how many to keep, and the speed.
- Bring the shows you follow from another podcast app: export them as OPML (Pocket Casts, Overcast and most others can) and use Import on Podcasts. Export gives them back.
- Episodes play through the server, so they work over Tailscale as they do at home, and podcast hosts don't see each listener.
- iPhone and iPad: Podcasts under Libraries, laid out as on the web, with Settings › Podcasts for the same podcast settings. When an episode ends the next plays; with no connection, the show's next downloaded episode. Podcasts under Libraries, with the same Browse, following and player (back 15 and on 30 on the lock screen too, speeds, a sleep timer). Download any episode to listen with no connection. The newest episodes of the shows you follow download by themselves on Wi-Fi only (turn this off in the podcast settings); played ones are cleared away, and what you hear offline counts once you're back online.

## 0.23.0 · 25 October 2026

### Made for you
- Up to four daily mixes for each profile, built from what it plays: artists grouped by the genres they share ("Rock Mix", "Dire Straits, Mark Knopfler & more"), about 40 tracks each, the same all day and fresh tomorrow. A Made for you row on Home and in a music library's Browse (movable or off in Settings › Home), and a page for each mix with Play and Shuffle. Mixes appear once a profile has played twenty or so tracks. On the web for now; the Apple apps follow.
- A New albums row on Home, newest first, when there's a music library.

### Albums on iPhone and iPad, offline
- A Download button on every album and playlist. Tracks the phone plays as they are (AAC, ALAC, MP3, FLAC, WAV, AIFF) are kept as they are; anything else is converted to AAC. Downloads has a Music section, downloaded tracks carry a tick, and they play from the phone with no connection; what you listen to offline counts once you're back.

### Lyrics in the apps, and track artists
- Lyrics on Apple TV, iPhone and iPad, from the same files as the web: a Lyrics button on the Now Playing screen, timed lyrics follow the song with the current line lit, and choosing a line jumps there.
- A track by someone other than the album's artist (a duet, a compilation) shows its own artist under its title, on the web and in the apps, and the player names them. Albums tagged as compilations without an album artist are filed under Various Artists rather than split across their tracks' artists. Tracks already in a library pick this up when their files are next added.

### Added
- Siri: "Shuffle Family Guy in Flexarr" plays a show's episodes in a random order, here or on the Apple TV ("… on the living room TV"); also in the Shortcuts app, and for assistants through the play tool's `shuffle` flag.

### Changed
- Apple TV: Settings has left the tab bar. The profile picture in the corner now opens a small menu with Switch profile and Settings, so the bar stays short as libraries and Now Playing come and go.

### Fixed
- Shows named with a year by TheTVDB and Sonarr ("Archer (2009)") now carry the year in their year field instead of their title, so Siri, search and the shelves say "Archer". Existing titles are put right by a scan with metadata refresh.
- Apple TV: Now Playing's Up next lists the whole queue in a list that scrolls, instead of six rows with the last cut off at the bottom of the screen.

## 0.22.0 · 25 October 2026

### Music on Apple TV, iPhone and iPad
- Music libraries open in the apps: artists (with their pictures), albums, tracks and your playlists, with Play and Shuffle on every artist, album and playlist, and the Home rows for music (Recently played, Most played, More from artists you play, Your playlists).
- A queue player: next and previous, seek, shuffle and repeat; one Now Playing screen on Apple TV (its own tab while music plays, play/pause from anywhere on the remote) and a mini-player above the tab bar on iPhone and iPad that opens the Now Playing sheet with cover, scrubber, controls and the queue.
- The lock screen, Control Center and a watch control the music; AirPlay from the Now Playing sheet; playing a film pauses the music and the remote goes back to it afterwards.

### Albums by kind
- With Lidarr connected, an artist's page shelves albums into Albums, EPs and singles, Live and compilations, and Other, and an album page says "EP" or "Live" before its year.

### Fixed
- Music no longer appears in Continue Watching or as the featured title on Home (a half-played track belongs to music's own Recently played row). On Apple TV, a Play button on an artist or album in a row plays them rather than opening their page.
- Turning subtitles off (or picking a track) in the Apple TV, iPhone and web players is now remembered for the title and its show: the next sitting and the next episode start the way you left them, instead of the server's first choice coming back. Forced subtitles the server puts on by itself no longer count as a preference that turns the full subtitles on next time.
- Subtitles played through the engine on Apple TV and iPhone no longer pile up on screen: each line goes when its time is up, instead of staying under the next ones.

## 0.21.1 · 25 October 2026

### Fixed: the iPhone and iPad apps away from home
- Connected at home, the app only ever knew the server's home address, so away from home it tried that and sat on its spinner, Tailscale or not. The server now tells the apps its Tailscale address (once remote access is on in Settings › Remote access), and the app keeps it beside the home one: when the home address can't be reached, requests go over Tailscale, and while away the home address is tried again every few minutes so the app comes back to it. Nothing to set up on the device beyond having Tailscale connected; Settings › Server shows both addresses and which one is in use. Apps already connected pick the address up on their next launch.

## 0.21.0 · 25 October 2026

### Music on the web: a queue, playlists, lyrics and discovery
- A Spotify-style queue: Play next and Add to queue on tracks and albums, a queue panel above the now-playing bar with drag or arrow reordering, remove, clear and skip-to; the bar stays with you on music pages, links its cover, title and artist to their pages, and the queue, track and position survive a reload (coming back paused).
- Playlists, per profile: a Playlists page and a page for each playlist with Play, Shuffle, drag-to-reorder, rename and delete, a cover, Add to playlist in track, album and artist menus, and a Your playlists row on Home.
- Lyrics: a Lyrics panel under an album's tracklist for the track playing, from .lrc or .txt sidecars or the file's own tags; timed lyrics highlight the line being sung and clicking a line seeks there.
- Discovery: Recently played, Most played and More from artists you play rows on Home (movable or off in Settings, like any row) and in a music library's Browse view, with New albums; Artist radio, a station of about fifty tracks seeded from an artist and the artists sharing their genres; search finds albums and tracks, with a play button on each track.
- A play button appears on an artist's or album's artwork on hover, playing their tracks.
- Near-gapless playback: the next track is loaded into a second player before the current one ends, so the switch takes a few milliseconds; an optional crossfade (2 to 12 seconds) in Settings › Playback.
- Normalise volume (on by default in Settings › Playback): ReplayGain and Opus R128 tags in the files level the loudness between tracks and albums.

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
