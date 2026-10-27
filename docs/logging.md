# Logs

Flexarr keeps a log of what the server and its apps do, so when something goes wrong (a film won't play, a scan stalls, Sonarr can't be reached) you can see why. Everything stays on your server: nothing is sent anywhere else.

## Reading the log

**Settings › Server › Logs** (main profile only) shows the newest entries first. Narrow it by level (only warnings and errors, say), by area, or by searching. Turn on **Live** to watch entries arrive.

Every playback gets a short id, such as `3f9a1c07`. The server's lines about that playback show it as `#3f9a1c07`, and the apps attach it to what they report. Click a `playbackId` to see the whole story of one playback from both ends.

On the Mac it runs on, `make logs` tails today's file (`make logs LEVEL=Warning` for warnings and errors only).

## Turning up the detail

The **Default level** applies to the whole server. Each area can write more (**Debug**) or less (**Warning**) than that, and changes apply at once, without a restart:

| Area | What it covers |
| --- | --- |
| General | Everything not below, including startup |
| Playback | Direct play, transcoding, ffmpeg, downloads to phones |
| Library scans | Scanning, probing files, imports |
| Sonarr, Radarr and Seerr | Calls to the *arr apps and Seerr |
| Metadata and artwork | TVmaze, TMDB, artwork |
| Live TV | Dispatcharr channels, the guide, recordings |
| Podcasts | Feeds and episode downloads |
| Notifications | Push notifications |
| Web requests | One line per request. Failures show at Information; set Debug to see every request |
| Apps and browsers | What the Apple TV, iPhone and web app report |

Chasing a problem? Set its area to Debug, reproduce it, then set it back to Default.

## What the apps send

- **Automatically:** warnings and errors from the Apple TV and iPhone apps, and errors from the web app. They appear under *Apps and browsers*, named after the device.
- **When you ask:** **Send diagnostics** in an app's settings uploads its recent log in full, with an optional note. They are listed under **Diagnostics from apps** on the Logs page.

## Privacy

API keys, passwords, sign-in tokens and playback session ids are removed before anything is written: keys and passwords become `***`, and long hex tokens become a short one-way `#id`. That's true of the server's files, what the apps send, and the diagnostics zip, so the zip is safe to attach to a bug report.

## Keeping the folder small

Logs are kept for **14 days** and the folder is capped at **100 MB**; the oldest files go first. Both can be changed on the Logs page. The server writes one file a day (`flexarr-20261027.log`), starting a new one if a day's passes 10 MB. Each line is a JSON object, so other tools can read it too.

| Install | Log folder |
| --- | --- |
| macOS (deploy script) | `~/.flexarr/logs` |
| Docker | `/config/logs` (also on `docker logs`) |
| Anywhere else | `<data directory>/logs`, or set `Flexarr__Logs__Directory` |

**Download diagnostics** on the Logs page makes a zip of the logs, diagnostics from apps, your settings (with every key and password blanked) and a description of the server (version, OS, ffmpeg, free space, library sizes).
