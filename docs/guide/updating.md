---
title: Updating Flexarr
description: How to update the Flexarr server in Docker or on a Mac, what happens to your data when you do, and how to keep the apps up to date.
---

Updating Flexarr replaces the program and leaves your data alone. Your settings, profiles, watch history and libraries live in the data folder (`/config` in Docker, `~/.flexarr/data` on a Mac), which an update never overwrites.

## Before you update

Read what's changed. Every release is listed, newest first, in:

- **Settings › About** on your server, under **What's new**
- the [changelog](https://github.com/danleach-glitch/flexarr/blob/main/CHANGELOG.md)
- [GitHub releases](https://github.com/danleach-glitch/flexarr/releases)

**Settings › Updates** shows the version you're running now and the newest release, with what's changed in it.

## Settings › Updates

Flexarr asks GitHub once a day whether there's a newer release, and Settings shows a dot beside **Updates** when there is. To look straight away, choose **Check for updates**. To stop the daily check, turn off **Check for updates every day**; nothing else about your server is sent.

What happens next depends on how you installed Flexarr:

- **On a Mac** (installed with the install script), choose **Update now**. Flexarr downloads the new version, checks it against the checksum published with the release, and restarts to install it, which takes a minute or two. If anything is playing, being watched live or recording, it asks first, because the restart interrupts it.
- **In Docker**, a container can't replace itself, so the page shows the commands to update it (see [Docker](#docker) below).
- **Anything else**, such as running from source, is updated the way you installed it.

### Automatic updates on a Mac

Turn on **Install updates automatically** to have a Mac install update itself. It's off unless you turn it on. Flexarr installs a new release between 4 and 5 in the morning, after the nightly backup, and only when nothing is playing, being watched live or recording. A busy night waits for the next. A release that failed to install isn't tried again by itself; you can still install it with **Update now**.

### If an update doesn't start

Before swapping in the new version, Flexarr copies the database aside. If the new version doesn't answer within five minutes, the version you had is put back, with the database as it was, and **Settings › Updates** says what happened. The update's own log is `~/.flexarr/logs/update.log`, and the version that didn't start is kept in `~/.flexarr/app.failed` so you can report what went wrong.

The version before the last successful update is kept in `~/.flexarr/app.previous`.

> **Note** A release can need a newer .NET runtime than your Mac has. Settings › Updates then says so, and the install script, which installs the runtime too, is the way to update.

You don't need to make a backup by hand: Flexarr makes one itself whenever an update is about to change the database (see [below](#what-happens-to-your-data)). If you'd like an extra copy anyway, choose **Back up now** in **Settings › Backup and restore**.

## Docker

Flexarr's image is `ghcr.io/danleach-glitch/flexarr`. Updating means downloading the newer image and making a new container from it, with the same settings and folders as before.

### With Docker Compose

In the folder with your `docker-compose.yml`:

```bash
docker compose pull flexarr
docker compose up -d flexarr
```

Compose replaces the container only if the image has changed.

### With docker run

Download the new image, remove the old container, and start a new one with the same command you used to install:

```bash
docker pull ghcr.io/danleach-glitch/flexarr:latest
docker stop flexarr
docker rm flexarr
docker run -d --name flexarr \
  -p 8383:8383 \
  -v /path/to/config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:latest
```

Removing the container doesn't touch your data, as long as `/config` is a folder or volume you mounted. Use exactly the same `-v` paths as before.

> **Note** On a Synology or Unraid server, use the platform's own way to update a container. See [Install on a Synology NAS](install-synology.md) or [Install on Unraid](install-unraid.md).

### Which tag to use

Images are published for every release with these tags:

| Tag | What it follows |
| --- | --- |
| `latest` | The newest release. Every update you pull may be a bigger step. |
| `0.49` | The newest `0.49.x` release: fixes, without moving to `0.50`. |
| `0.49.1` | Exactly that release, never anything else. |

To have containers updated automatically, a tool such as [Watchtower](https://containrrr.dev/watchtower/) can pull new images and recreate them.

Use `latest` to always get the newest Flexarr, or pin a version like `0.49` and change the number when you're ready to move on. Change the tag in your `docker-compose.yml` (`image: ghcr.io/danleach-glitch/flexarr:0.49`) or in your `docker run` command.

Images are built for 64-bit Intel/AMD and 64-bit ARM, and Docker picks the right one for your machine.

## On a Mac

If you installed Flexarr natively on a Mac with the deploy script (see [Install on a Mac](install-macos.md)), the easiest way is **Update now** in [Settings › Updates](#settings-updates). You can also update by getting the newest source and running the same command again from the machine you deployed from:

```bash
cd flexarr
git pull
make deploy DEPLOY_HOST=<your-mac>
```

The script builds the new version, replaces the program in `~/.flexarr/app`, brings the .NET runtime up to its latest patch, and restarts the server. It waits for the server to come back before saying it's done, and shows the end of the log if it doesn't. Your settings in `~/.flexarr/data` are never touched.

## What happens to your data

When a new version starts for the first time, it updates the database to its new layout if it needs to. Before it does, it makes a **pre-upgrade backup** in your backups folder. The last three pre-upgrade backups are kept, alongside the nightly ones.

You'll find them in **Settings › Backup and restore**. If an update goes wrong, install the version you had before, then restore its pre-upgrade backup from that page. See [Backups](backups.md).

> **Important** Don't try to run an older Flexarr on a database a newer one has already updated. Go back to the older version and restore the pre-upgrade backup instead. A backup made by a newer Flexarr is refused by an older one.

> **Note** If your backups folder is on a drive that isn't connected when you update, the pre-upgrade backup can't be made. Flexarr carries on starting rather than staying down, and says so in its log.

## Keeping the apps up to date

- **iPhone and iPad:** the App Store updates Flexarr like any other app. To update by hand, open the App Store, tap your picture, and pull down to refresh the list of updates.
- **Apple TV:** until it's on the App Store, pull the latest source and run `make apple-install` again. See [Install the Apple TV app with Xcode](apple-tv-xcode.md#updating-the-app).
- **Web:** the web app comes with the server, so it's up to date as soon as the server is. Reload any open tabs.

It's best to keep the server and the apps roughly in step. When a release adds something to both, the app's side of it only works once the server has it too.
