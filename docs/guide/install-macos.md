---
title: Install on a Mac
description: Build Flexarr from source and run it natively on a Mac as a background service, with hardware video conversion through VideoToolbox.
---

On a Mac, Flexarr runs natively rather than in Docker. That way it can use the Mac's graphics hardware (Apple VideoToolbox) to convert video, which Docker on macOS can't. It works on Apple silicon and Intel Macs.

There's no downloadable Mac app or installer yet. You build Flexarr from its source code with one command, which installs it as a background service that starts when you log in. It takes some time in Terminal, but you only do it once.

> **Note** Prefer not to build anything? Docker Desktop for Mac runs the [Docker image](install-docker.md) too, but without VideoToolbox: video is converted on the processor.

## Before you start

You'll build on one Mac and install on another over SSH. They can be the same Mac, which is what this page assumes. You need:

- **macOS command line tools** (git, make, Python). Run `xcode-select --install` in Terminal, or install Xcode.
- **[Homebrew](https://brew.sh)**, for FFmpeg and Node.js.
- **The .NET 10 SDK**, from [Microsoft's download page](https://dotnet.microsoft.com/download/dotnet/10.0). Choose the latest SDK installer for your Mac (Arm64 for Apple silicon, x64 for Intel).
- **Node.js 24 or later**: `brew install node`.
- **FFmpeg**, on the Mac that will run Flexarr: `brew install ffmpeg`. Flexarr uses it to read your files and convert video.

## Turn on Remote Login

The install script copies Flexarr into place and starts it over SSH, even on the same Mac.

1. Open **System Settings › General › Sharing** and turn on **Remote Login**.
2. In Terminal, make an SSH key if you don't have one, and let it sign in to this Mac without a password:

   ```bash
   ssh-keygen -t ed25519        # press Return to accept the defaults
   ssh-copy-id localhost
   ssh localhost true           # should finish without asking for a password
   ```

## Build and install

1. Get the source code:

   ```bash
   git clone https://github.com/danleach-glitch/flexarr.git
   cd flexarr
   ```

2. Build and install:

   ```bash
   make deploy DEPLOY_HOST=localhost
   ```

The script builds the web app and the server, downloads Microsoft's ASP.NET Core runtime into `~/.dotnet` (checking it against Microsoft's published checksum), copies Flexarr into `~/.flexarr`, and starts it. It waits until Flexarr answers, then prints its address, something like `http://your-mac.local:8383`.

To install on a different Mac, use its SSH host name in place of `localhost`, for example `make deploy DEPLOY_HOST=mini`. That Mac needs Remote Login and FFmpeg, but not the SDK or Node.js.

## Where things live

| Path | What's there |
| --- | --- |
| `~/.flexarr/app` | Flexarr itself. Replaced each time you install. |
| `~/.flexarr/data` | Your settings (`config.json`), database and caches. Installing never touches it. |
| `~/.flexarr/logs` | Log files, one a day. `console.log` has errors from when the server couldn't start. |
| `~/Library/LaunchAgents/io.github.flexarr.server.plist` | The service: starts Flexarr at login and restarts it if it stops. |

## Keep it running

Flexarr runs as a LaunchAgent, which means it runs while you're logged in. On a Mac that acts as a server:

- Turn on automatic login for your user in **System Settings › Users & Groups**. (macOS doesn't offer it while FileVault is on.)
- Stop the Mac going to sleep on its own, in System Settings' energy or battery settings.

To restart, stop or start Flexarr by hand:

```bash
launchctl kickstart -k gui/$(id -u)/io.github.flexarr.server   # restart
launchctl bootout gui/$(id -u)/io.github.flexarr.server        # stop
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/io.github.flexarr.server.plist  # start
```

## Media on an external drive

The first time Flexarr reads a library on an external drive, macOS may ask whether to allow access. Allow it. If scans can't see your media, look in **System Settings › Privacy & Security** under **Files & Folders** (or **Full Disk Access**) for `dotnet`, the program Flexarr runs under, and turn its access on.

## Hardware transcoding

Nothing to set up. **Settings › Streaming › Hardware acceleration** starts on **Automatic**, which on a Mac means Apple VideoToolbox. The graphics hardware decodes H.264 and HEVC; other formats (older AVI files, VC-1 and the like) are decoded on the processor and still encoded in hardware.

## If your \*arrs run in Docker on this Mac

Sonarr, Radarr and Lidarr in Docker see your media under their own paths, such as `/data/tv`. Tell Flexarr where those are on the Mac in **Settings › Connections › Path mappings**. From inside a container, the Mac is `host.docker.internal`, so webhooks go to `http://host.docker.internal:8383`. [Connect Sonarr, Radarr and Lidarr](connect-arrs.md) and [Media paths](media-paths.md) cover both.

## Updating

Fetch the latest code and install again. Your settings and library stay as they are.

```bash
cd flexarr
git pull
make deploy DEPLOY_HOST=localhost
```

See [Updating](updating.md) for more.

## Next: set it up

Open **http://your-mac.local:8383** (or **http://localhost:8383** on the Mac itself) in a browser and choose **Set up Flexarr**. The first browser to reach a new server becomes its owner. Carry on with [First setup](first-setup.md).
