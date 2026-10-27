# Logs

Flexarr keeps a log of what the server and its apps do, so when something goes wrong (a film won't play, a scan stalls, Sonarr can't be reached) you can see why. Everything stays on your server. The one exception is [error reports](#error-reports), which are off unless you turn them on.

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

## Error reports

You can choose to send error reports to Flexarr's developer, so bugs get found and fixed sooner. **It's off unless you turn it on**, in **Settings › Server › Logs** (main profile only). One switch covers the server and every Apple TV, iPhone and browser connected to it, and turning it off stops it at once everywhere.

Reports go to [Sentry](https://sentry.io), an error-tracking service, which stores them in the EU (Germany).

**What's sent**, and only when something goes wrong:

- what went wrong and where in Flexarr's code (the error and its stack trace)
- the Flexarr version, the operating system, and the device model
- a random id for your server, made when you turn reports on, so repeats from one server can be told apart. It isn't linked to you, your account or your address.

**What's never sent:**

- titles, file names or paths, or anything about your library
- what anyone watches, and your logs
- profile names and device names
- addresses: IP addresses, your server's address, network names
- keys, passwords and tokens

**How that's enforced:** reports are cleaned on your server or device before they leave it:

1. The server sends its log message *templates* ("Couldn't scan {Folder}"), never the values filled in.
2. The same redaction the logs use removes keys, passwords and tokens.
3. A stricter pass removes web addresses, file paths, media file names, email and IP addresses, and anything in quotes, where titles and names usually appear.
4. Sentry's own collection of personal data is turned off, and so are its extras: no breadcrumbs, request details, screenshots, performance tracing or session tracking.
5. The apps send only the type and code of an error they log, never the message text. Crashes include the stack trace.

A report that can't be cleaned isn't sent. Reports are rate-limited, and Sentry deletes them after at most 90 days.
