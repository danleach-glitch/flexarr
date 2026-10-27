---
title: Logs and diagnostics
description: Read Flexarr's log, turn up the detail for one area, follow a single playback, collect diagnostics from the apps, and choose whether to send error reports.
---

Flexarr keeps a log of what the server and its apps do. When a film won't play, a scan stalls or Sonarr can't be reached, the log usually says why. It stays on your server: nothing in it is sent anywhere unless you download it and attach it to a bug report yourself.

## Reading the log

Open **Settings › Logs** in the web app, as the main profile. The newest entries are at the top. Select an entry to see its details.

To narrow it down:

- **Minimum level**: show only warnings and errors, say, or everything down to Debug.
- **Area**: one part of Flexarr, such as Playback or Library scans, or **All areas**.
- **Search**: any text in the entry.
- **Live**: turn it on to watch new entries arrive as they happen. It's handy while you reproduce a problem.

**Load older** at the bottom goes further back.

![The Logs page with error reports switched on, the level, area and search filters, and recent log lines](../../site/guide/img/logs.webp "Settings › Logs")

## Following one playback

Every time something starts playing it gets a short id, such as `3f9a1c07`. The server's lines about that playback show it as `#3f9a1c07`, and the Apple TV, iPhone and iPad apps attach the same id to what they report. Open an entry and select its `playbackId` to see only that playback: the whole story, from the server and the device, from start to finish.

## Turning up the detail

Under **Levels**, the **Default level** applies to the whole server. Each area can be set to log more (**Debug**) or less (**Warning**) than that. Changes apply straight away, with no restart.

| Area | What it covers |
| --- | --- |
| General | Everything not below, including startup |
| Playback | Direct play, transcoding, ffmpeg, downloads to phones |
| Library scans | Scanning, reading files, imports |
| Sonarr, Radarr and Seerr | Calls to the \*arr apps and Seerr |
| Metadata and artwork | TVmaze, TMDB, artwork |
| Live TV | Dispatcharr channels, the guide, recordings |
| Podcasts | Feeds and episode downloads |
| Notifications | Push notifications |
| Web requests | One line per request. Failures show at Information; set Debug to see every request |
| Apps and browsers | What the Apple TV, iPhone, iPad and web app report |

> **Tip** Chasing a problem? Set its area to Debug, make the problem happen again, then set the area back to Default so the log doesn't fill up.

## What the apps send

- **Automatically:** warnings and errors from the Apple TV, iPhone and iPad apps, and errors from the web app. They appear under the **Apps and browsers** area, named after the device.
- **When you ask:** **Send diagnostics** sends a device's recent log in full. On Apple TV, iPhone and iPad it's in the app's **Settings › Server › Send diagnostics**. Add a note saying what happened and roughly when, then send.

Diagnostics go to your server and nowhere else. They're listed under **Diagnostics from apps** at the bottom of the Logs page, with the device, app version, profile and when they arrived, and a **Download** button for each.

## Download diagnostics

**Download diagnostics**, at the top of the Logs page, makes a zip to attach to a bug report. It holds:

- the server's logs
- diagnostics sent from the apps
- your settings, with every key and password blanked
- a description of the server: Flexarr version, operating system, ffmpeg, free space and library sizes

## What's taken out

API keys, passwords, sign-in tokens and playback session ids are removed before anything is written: keys and passwords become `***`, and long tokens become a short `#id`. That's true of the server's log files, what the apps send and the diagnostics zip, so the zip is safe to attach to a public bug report.

Titles and file names do appear in the log, because they're what you need to work out what went wrong. Look through the zip first if that matters to you.

## How long logs are kept

Logs are kept for **14 days**, and the log folder is capped at **100 MB**, oldest files first. Change both under **Retention** on the Logs page: **Keep for (days)** and **Largest size (MB)**.

The server starts a new file each day (`flexarr-20261027.log`, say), and another if one day's passes 10 MB. Each line is a JSON object, so other tools can read them.

| Install | Log folder |
| --- | --- |
| Mac | `~/.flexarr/logs` |
| Docker | `/config/logs` (and `docker logs flexarr`) |
| Anywhere else | `logs` in Flexarr's data folder, or set `Flexarr__Logs__Directory` |

## Error reports

You can choose to send error reports to Flexarr's developer, so bugs get found and fixed sooner. **It's off unless you turn it on.**

The switch, **Send error reports to Flexarr's developer**, is at the top of the Logs page. A green dot means on and a red dot off. One switch covers the server and every Apple TV, iPhone, iPad and browser connected to it, and turning it off stops it everywhere at once. **What's sent, and what never is** opens the full list.

Reports go to Sentry, an error-tracking service, which stores them in the EU and deletes them after at most 90 days.

**What's sent**, and only when something goes wrong:

- what went wrong and where in Flexarr's code (the error and its stack trace)
- the Flexarr version, the operating system and the device model
- a random id for your server, made when you turn reports on, so repeats from one server can be told apart; it isn't linked to you

**What's never sent:**

- titles, file names or paths, or anything about your library
- what anyone watches, and your logs
- profile names and device names
- addresses: IP addresses, your server's address, network names
- keys, passwords and tokens

Reports are cleaned on your server or device before they leave it. The server sends its message templates ("Couldn't scan {Folder}") without the values filled in. The apps send only the type and code of an error, never its message. A further pass removes web addresses, file paths, file names, email and IP addresses, and anything in quotes. A report that can't be cleaned isn't sent.

See also [Troubleshooting](troubleshooting.md), which starts most fixes from this page.
