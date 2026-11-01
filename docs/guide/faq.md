---
title: Frequently asked questions
description: Short answers about Flexarr's cost and licence, what you need to run it, privacy, the apps, and how many people can use it.
---

## The basics

### What is Flexarr?

Flexarr is a media server you run yourself, on a computer at home. It organises your movies, TV, home videos, music and podcasts, and live TV from an IPTV source, and plays them on the web, Apple TV, iPhone and iPad. Audiobooks arrive in the next release. There's no Flexarr account and no cloud service in the middle: your library and everything about it stays on your server.

It's built to sit alongside Sonarr, Radarr and Lidarr, which fetch and organise your media, and Seerr, which handles requests.

### Is it free?

Yes. The server and the apps are free, with no subscription and no adverts.

Flexarr is licensed under the [PolyForm Noncommercial License 1.0.0](https://github.com/danleach-glitch/flexarr/blob/main/LICENSE). You can use it, change it and share it for any noncommercial purpose: at home, for yourself and your family, or in a charity, school or other noncommercial organisation. You can't sell it or use it in a commercial product or service without a separate licence from the author.

Because it restricts commercial use, Flexarr is *source-available* rather than open source in the Open Source Initiative's sense. The source code is all [on GitHub](https://github.com/danleach-glitch/flexarr).

If you'd like to support it, see [flexarr.io/donate](https://flexarr.io/donate).

### Do I need Sonarr, Radarr or Lidarr?

Flexarr works best with them, and they're where most of its information comes from. Here's what each one adds:

- **Sonarr and Radarr** supply the posters, artwork and details for your shows and movies, with no extra accounts or API keys. Without them, Flexarr still finds and plays your video files, and takes artwork and details from any `.nfo` files and images beside them (as Kodi, Jellyfin and Plex users often have), from TVmaze for shows, and from TMDB for movies with a free key of your own. See [Without Sonarr and Radarr](without-arrs.md). They also power **Coming soon**, deleting through Sonarr and Radarr, and "find a better copy" in [Library health](library-health.md).
- **Lidarr** supplies artist pictures, album covers and the split into albums, EPs and singles. Music works without it, using the tags in your files and any artwork in their folders.
- **Seerr** lets you request movies and shows from **Discover**. Without it, Discover shows your own library by genre.
- **Bazarr** finds subtitles from the player when a title has none in your language.

Cast and crew for TV shows come from TVmaze, which needs no setup. Podcasts are built in and need none of these apps. See [Connect Sonarr, Radarr, Lidarr and Seerr](connect-arrs.md).

### What hardware do I need?

Any computer that stays on at home and can see your media:

- **A Mac**, Apple silicon or Intel. Flexarr runs natively, and uses the Mac's graphics chip to convert video when a device needs it. See [Install on a Mac](install-macos.md).
- **A NAS or Linux machine with Docker.** The image runs on Intel/AMD (x86-64) and ARM (arm64) machines. See [Synology](install-synology.md), [Unraid](install-unraid.md) and [Docker](install-docker.md).

Most of the time the server does very little: the Apple TV, iPhone and iPad play files as they are, and the server sends the bytes. It works harder when it has to convert a video for a device that can't play it, such as a web browser with a 4K HEVC movie. A graphics chip that can do this (Apple's VideoToolbox, Intel Quick Sync, VA-API or NVIDIA NVENC) makes a big difference.

The apps need iOS, iPadOS or tvOS 18.2 or later. More in [What you need](what-you-need.md).

## Using it

### Can several people use it?

Yes. Each person gets a **profile** with their own Continue watching, watched history, watchlist, playlists, podcasts and settings, which follow them from device to device. Profiles can have a PIN, an age limit, and be kept out of particular libraries; kids profiles see only what suits them. A shared Apple TV can ask who's watching each time Flexarr opens.

The **main profile** manages the server, with an admin PIN guarding its settings; the others can change their own playback and podcast settings. See [Profiles](profiles.md).

### How many people can watch at once?

Flexarr doesn't limit how many screens play at once. In practice the limit is your server and your network.

- **Files played as they are** (most playback on Apple TV, iPhone and iPad) take very little work, so many can run at once.
- **Converted streams** take real work. Flexarr converts up to **3 videos at once** by default, which suits a Mac mini. When that's full, the next person to start one is told the server is busy, and one profile can't take every slot. You can change the number with `maxConcurrentTranscodes` under `transcoding` in `config.json` (0 means no limit), then restart Flexarr.
- **Live TV** is limited by how many connections your IPTV provider allows, though everyone watching the same channel shares one.

### Does it work away from home?

Yes, through [Tailscale](https://tailscale.com), a free service that links your own devices privately. Nothing is opened on your router, and Flexarr isn't put on the public internet. Once remote access is on, the apps switch to it by themselves when you leave home, and back again when you return. You can share it by inviting someone to your tailnet and giving them a profile.

If you'd rather use your own HTTPS reverse proxy, that works too. See [Away from home](away-from-home.md).

### Can I watch offline?

On iPhone and iPad, yes: download movies, episodes, albums, playlists and podcast episodes. What you watch offline counts once you're back online. See [Downloads](downloads.md).

## Privacy

### Does any data leave my home?

Your library, what everyone watches, your profiles and your logs stay on your server. Flexarr has no account, analytics or adverts. A few things do go out, each for a reason:

- **Artwork and details.** The server downloads posters and artwork from the image addresses Sonarr, Radarr and Lidarr give it, looks up shows on TVmaze, and movies on TMDB if you add a TMDB key.
- **Audiobook details** (from the next release) come from Audible and Open Library, and from Google Books and Hardcover if you add their keys.
- **Updates.** Once a day the server asks GitHub whether there's a newer version.
- **Trakt**, if a profile connects to it. Signing in goes through the relay at push.flexarr.io unless you use your own Trakt app.
- **Podcasts.** Browsing and search use Apple's podcast directory, and the server fetches each show's feed and episodes. Your devices play them through your server, so podcast hosts see your server rather than each listener.
- **Live TV logos**, if you use Live TV, come from a community collection on GitHub.
- **Notifications**, if you turn them on, pass through a small relay at push.flexarr.io on their way to Apple. They're encrypted on your server so only your phone can read them; the relay never sees what's in them and keeps nothing. The iPhone's Lock Screen panel for a TV is the exception: its updates carry only whether it's paused, how far in it is, how long it is and its speed, unencrypted, never the title or picture.
- **Error reports**, only if you turn them on. They describe what went wrong in Flexarr's code, with titles, names, addresses and keys removed first. See [Logs and diagnostics](logs-and-diagnostics.md#error-reports).
- **Plex profile pictures**, only if you use that button, make one request to plex.tv. See [Bring your history over from Plex](import-from-plex.md).

The apps talk only to your server. The full details are in the [privacy policy](https://flexarr.io/privacy).

## Apps

### Is there an Apple TV app on the App Store?

Not yet: it's with Apple for review. The iPhone and iPad app is [on the App Store](https://apps.apple.com/app/flexarr/id6826618084). Until the Apple TV app joins it, you can install it on your own Apple TV from a Mac with Xcode. See [Apple TV with Xcode](apple-tv-xcode.md).

### Is there an Android app?

Android and Android TV apps are on the way: see [flexarr.io/apps](https://flexarr.io/apps). Until then, the web app works in your phone's browser.

### Is there a Mac app?

Flexarr Server for Mac is coming to the Mac App Store. Until then, you can build the server on your Mac from source: see [Install on a Mac](install-macos.md).

### Does it do photos?

Not yet.

### Which languages does it speak?

English only, for now.

### Does it work with Siri?

On iPhone and iPad, yes: "Play the next episode of *Curb Your Enthusiasm* in Flexarr", "Shuffle *Family Guy* in Flexarr", and podcast commands too. See [Siri and Shortcuts](siri-and-shortcuts.md).

## Getting help

### Where do I report a bug?

On GitHub: [github.com/danleach-glitch/flexarr/issues](https://github.com/danleach-glitch/flexarr/issues). Say what happened and what you expected. Don't attach the zip from **Download diagnostics** to a public issue: it holds your addresses and file names. See [Troubleshooting](troubleshooting.md#where-can-i-get-help).

### Where do I ask for a feature?

In [Ideas](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas) on GitHub Discussions. The [roadmap](https://github.com/danleach-glitch/flexarr/blob/main/docs/roadmap.md) links each planned feature to its discussion, so you can add your vote and thoughts there.

### What's changed in each version?

**Settings › Updates** in the web app shows your version and what's new in the latest one, and the full [changelog](https://github.com/danleach-glitch/flexarr/blob/main/CHANGELOG.md) is on GitHub. To upgrade, see [Updating](updating.md).
