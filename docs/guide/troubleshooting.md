---
title: Troubleshooting
description: Fixes for the most common Flexarr problems, from an app that can't find your server to playback that won't start, HDR, sound and connecting away from home.
---

Most problems have a short fix. Find yours below. If it isn't here, or the fix doesn't work, the log usually says what's wrong: see [Logs and diagnostics](logs-and-diagnostics.md), and [where to get help](#where-can-i-get-help) at the end.

## Connecting

### The app can't find my server

The iPhone, iPad and Apple TV apps look for Flexarr on your home Wi-Fi. If **Looking for Flexarr…** never finds it:

1. **Check the server is running.** On another device on the same network, open `http://<server>:8383` in a browser, using the computer's name or IP address. If that doesn't load, the problem is the server, not the app. See your install guide: [Mac](install-macos.md), [Synology](install-synology.md), [Unraid](install-unraid.md) or [Docker](install-docker.md).
2. **Check you're on the same network.** The device must be on the same Wi-Fi as the server, not a guest network and not mobile data.
3. **Check local network access (iPhone and iPad).** The app asks to find devices on your local network the first time it opens. If you said no, turn it back on in the iPhone's **Settings › Privacy & Security › Local Network › Flexarr**.
4. **Type the address instead.** Choose **Enter an address instead** and type the address that worked in the browser, such as `http://192.168.1.20:8383`. You'll still get a code to approve.

If the browser test works but discovery doesn't, your router may block devices from announcing themselves to each other. Typing the address always works.

### The app says the server hasn't been set up

A new Flexarr server has no owner until someone sets it up in a browser. The app shows the steps; here they are in full:

1. On a computer on your home network, open the server's address (the app shows it) in a web browser.
2. Enter the server's setup code (it's in the server's log; [First setup](first-setup.md#enter-the-setup-code) says where for your install) and choose **Set up Flexarr**. That browser becomes the server's owner, with no key needed.
3. Go to **Settings › Security › Connect a device** and type in the code the app is showing.

The app moves on by itself once you have. See [First setup](first-setup.md).

**Set up Flexarr** only appears to a browser on your home network, using the server's own name or IP address. It won't appear over the internet, or once someone has set the server up. If someone else in the house set it up, ask them to approve your code from their browser.

### I can't connect away from home

Flexarr isn't on the public internet. Away from home, the apps reach it over Tailscale. Check each of these:

1. **Remote access is on.** In the web app, **Settings › Remote access › Away from home** should say **On: Flexarr is available on your tailnet**. If it says Tailscale isn't installed or isn't connected, install Tailscale on the server and sign in first.
2. **Tailscale is connected on your phone or tablet**, signed in to the same tailnet.
3. **You have a profile.** Under **People on your tailnet**, each Tailscale account must be linked to a profile. Anyone without one is turned away.
4. **The app has picked up the address.** It learns the Tailscale address from the server and uses it when the home address can't be reached. If remote access was turned on after the app connected, close and reopen the app at home once. In the app, **Settings › Server** shows both addresses and which one it's using.

Full details are in [Away from home](away-from-home.md).

## Your library

### New downloads don't appear

Flexarr rescans each library every 12 hours, and straight away when Sonarr, Radarr or Lidarr tell it something's arrived. If new downloads take hours to show up, the webhook is missing or failing.

1. In Flexarr, open **Settings › Connections** and find **Webhooks**. Copy the address for each app.
2. In Sonarr (and Radarr, and Lidarr), go to **Settings › Connect › + › Webhook**, paste the address, set the method to **POST**, and tick **On Import**, **On Upgrade**, **On Rename** and **On Delete**.
3. Use **Test** in Sonarr to check it gets through.

If Sonarr runs in Docker on the same computer as Flexarr, use `host.docker.internal` in the address in place of the computer's name. If you made a new webhook secret in Flexarr, the old addresses stop working: copy the new ones in.

If the webhook arrives but nothing appears, the paths probably don't match. Sonarr tells Flexarr the folder by its own path, such as `/data/tv/Show`, and Flexarr has to see the same folder at that path. Either mount your media at the same path in both, or check **Settings › Connections › Media folders**: an amber line there is a folder Flexarr hasn't found yet, with a suggestion or a folder to choose. See [Media paths](media-paths.md).

To pick something up now, use the **Scan for new and changed files** icon on the library in **Settings › Libraries**.

### Artwork or details are missing

Films and shows get their artwork and details from Radarr and Sonarr.

- **Are they connected?** Check **Settings › Connections**. Without them, titles show without posters.
- **Is it the wrong title?** Open **Settings › Libraries › Library health** and look under **Missing artwork** and **Check these matches**. Use **Fix match** to pick the right one. See [Library health](library-health.md).
- **Is the folder named clearly?** For titles Sonarr and Radarr don't manage, Flexarr searches by the folder's name. `Title (Year)` works best.
- **Refresh it.** In **Settings › Libraries**, use **Refresh artwork and details** on the library.

For music, artist pictures and album covers come from Lidarr when it's connected, otherwise from the artwork in each album's folder and the files' tags.

## Watching

### Playback buffers or won't start

First, look at what the player says it's doing. The Apple TV, iPhone and iPad players show in a corner whether you're watching the original or a converted stream. Converting takes work on the server; the original takes almost none.

- **"The server is busy."** Flexarr converts up to 3 videos at once by default, so a busy evening can fill it. Wait for another screen to finish, or see [How many streams at once?](faq.md#how-many-people-can-watch-at-once).
- **Turn on Play files directly** (Apple TV, iPhone and iPad, in the app's **Settings › Playback**). It's on by default. The device plays the file itself, so the server only sends bytes.
- **Check hardware acceleration.** In the web app, **Settings › Streaming › Hardware acceleration** should normally be **Automatic (recommended)**; the list only offers what this server's ffmpeg supports. Converting on the graphics chip is far quicker than on the processor. On a Mac, run Flexarr natively rather than in Docker, which can't use the Mac's hardware. In Docker on Linux, the graphics device (usually `/dev/dri`) has to be passed into the container for Intel or AMD graphics. See [Playback and transcoding](playback-and-transcoding.md).
- **Check the network.** Wi-Fi a long way from the router struggles with 4K. A cable to the Apple TV helps most. Away from home, set **Limit each stream to** in **Settings › Streaming**: **4 Mbps** suits most connections.
- **Check the file.** If one title fails and others play, the file may be damaged. **Run health check** in Library health, then **Search again**.

If it still won't play, set the **Playback** area to Debug in **Settings › Logs**, try again, and look for that playback's id. See [Logs and diagnostics](logs-and-diagnostics.md).

### HDR looks washed out or dim

- **On Apple TV, turn on Match Content.** In the Apple TV's own **Settings › Video and Audio › Match Content**, turn on **Match Dynamic Range**. Flexarr's player then asks the TV to switch to HDR or Dolby Vision for titles that have it, and back for ones that don't.
- **Leave Play files directly on**, so HDR10, HLG and Dolby Vision reach the TV untouched.
- **In a web browser,** HDR films are converted to standard dynamic range so they look right on any screen. For HDR, watch on the Apple TV.

More in [HDR and Dolby Atmos](hdr-and-atmos.md).

### No sound, or Dolby Atmos is missing

- **No sound at all, on Apple TV with a receiver or soundbar:** try turning off **Lossless surround** in the app's **Settings › Playback**. With it on, TrueHD and DTS-HD soundtracks go as multichannel PCM, which some soundbars and receivers don't accept. Off, they go as Dolby Digital Plus, which anything plays.
- **Atmos:** Dolby Atmos in Dolby Digital Plus reaches the Apple TV, iPhone and iPad intact. Atmos inside TrueHD doesn't yet keep its height channels; it's on the [roadmap](https://github.com/danleach-glitch/flexarr/discussions/45). The web player doesn't pass Atmos through.
- **Away from home** with a stream limit, the sound is compressed to save data.

More in [HDR and Dolby Atmos](hdr-and-atmos.md).

## Apps and profiles

### The Apple TV app stopped opening after 7 days

Until the Apple TV app is on the App Store, it installs from Xcode. With a free Apple ID, Apple only lets apps installed that way run for 7 days. Run the install command again to refresh it:

```bash
make apple-install TEAM=<your team id> BUNDLE_ID=com.yourname.flexarr.tv
```

With a paid Apple Developer account, an install lasts a year instead. See [Apple TV with Xcode](apple-tv-xcode.md).

### I forgot a profile's PIN

- **Someone else's profile:** choose **Forgot PIN?** under its PIN pad and enter the admin PIN, which takes the PIN off. Or sign in as the main profile, open **Settings › Profiles**, edit their profile, and set a new PIN or remove it. You don't need the old one.
- **The main profile's own PIN:** the admin PIN opens the main profile too. Then set a new one or remove it in **Settings › Profiles**, as for anyone.
- **The admin PIN:** in a browser, open **Settings**, choose **Forgotten it?** under the PIN (or **Change** then **Use the API key instead** in **Settings › Security**), and enter the server's API key with a new PIN. That browser is unlocked, and anywhere the old PIN was given is locked again. The Apple TV and phone apps send you to a browser for this.
- **The Mac app:** choose **Reset Admin PIN…** from Flexarr's menu bar icon and type a new PIN. Being at the Mac is enough; no key is needed.

The API key is in Flexarr's `config.json`: `~/.flexarr/data/config.json` on a Mac, or `/config/config.json` in Docker.

After a few wrong PINs, the profile is locked on that device for a while, longer each time. Other devices aren't affected.

## Getting help

### Where can I get help?

Open an issue on GitHub: [github.com/danleach-glitch/flexarr/issues](https://github.com/danleach-glitch/flexarr/issues).

To help us fix it quickly:

1. Say what you did, what you expected, and what happened instead.
2. Include your Flexarr version (**Settings › About**) and the device and app you were using.
3. If it happened on an Apple TV, iPhone or iPad, use **Send diagnostics** in the app's **Settings › Server** right after it happens, with a short note.
4. In the web app, open **Settings › Logs** and choose **Download diagnostics**. Attach the zip to the issue.

Keys and passwords are taken out of the zip, but titles and file names stay in. See [Logs and diagnostics](logs-and-diagnostics.md).

Ideas and feature requests go in [Discussions](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas).
