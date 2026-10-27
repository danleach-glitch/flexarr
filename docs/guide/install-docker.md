---
title: Install with Docker on Linux or Windows
description: Run Flexarr's Docker image on a Linux machine, or on Windows through Docker Desktop and WSL 2, with docker run or Docker Compose.
---

On Linux and Windows, Flexarr runs as a Docker container. The image is `ghcr.io/danleach-glitch/flexarr`, built for 64-bit Intel/AMD (`amd64`) and 64-bit ARM (`arm64`), so it also runs on a Raspberry Pi 5 with a 64-bit operating system. There's no native Windows build: on Windows, use Docker Desktop.

## What the container needs

| | Inside the container | What to give it |
| --- | --- | --- |
| Port | `8383` | Publish it as `8383` on the host. |
| Settings | `/config` | A folder (or Docker volume) for Flexarr's settings, database and caches. Keep it: it's your whole setup. |
| Media | `/data/media` (your choice) | Your media folder. Mount it at **the same path** Sonarr, Radarr and Lidarr use, so the paths they report match what Flexarr sees. |

The `latest` tag is always the newest [release](https://github.com/danleach-glitch/flexarr/releases). To upgrade only when you choose, use a version instead, such as `ghcr.io/danleach-glitch/flexarr:0.49`.

## Linux

### Permissions

The container runs as user ID `1654`, not as root, and doesn't read `PUID` or `PGID` settings. It needs to write to its settings folder and read your media. The simplest way is to run it as the user who owns your media, with Docker's `--user` option (`user:` in Compose). Find your IDs with `id`:

```bash
id
# uid=1000(you) gid=1000(you) ...
```

Create the settings folder yourself before starting the container, so it belongs to you rather than to root:

```bash
mkdir -p ~/flexarr/config
```

### docker run

```bash
docker run -d --name flexarr --restart unless-stopped \
  --user 1000:1000 \
  -p 8383:8383 \
  -v ~/flexarr/config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:latest
```

> **Tip** If you'd rather not deal with permissions, keep the settings in a named Docker volume instead: leave out `--user` and use `-v flexarr-config:/config`. Docker sets that volume up so Flexarr can write to it. Your media still needs to be readable by everyone, or by user `1654`.

### Docker Compose

Save this as `docker-compose.yml` and run `docker compose up -d` in the same folder.

```yaml
services:
  flexarr:
    image: ghcr.io/danleach-glitch/flexarr:latest
    container_name: flexarr
    user: "1000:1000"
    ports: ["8383:8383"]
    volumes:
      - ./config:/config
      - /path/to/media:/data/media
    restart: unless-stopped
```

If Sonarr, Radarr and Lidarr are in the same Compose file, give them the same media mount (`/path/to/media:/data/media`). They can then reach Flexarr at `http://flexarr:8383` for webhooks.

### Hardware transcoding

Flexarr converts video on the processor unless you pick something else in **Settings › Streaming › Hardware acceleration**. On Linux, **Automatic** means the processor. The menu lists only the encoders the image's FFmpeg was built with, which can include VA-API, Intel Quick Sync and NVIDIA NVENC.

> **Important** Hardware transcoding in Docker isn't ready-made yet. The image doesn't include the Intel or AMD drivers that VA-API and Quick Sync need, so choosing them is likely to make conversions fail. Leave the setting on **Automatic** unless you've tested otherwise. The Apple apps play most files directly, so many homes rarely need conversion at all.

**NVIDIA** is the exception worth trying, because the NVIDIA Container Toolkit supplies the driver libraries to the container. With the toolkit installed on the host, add this to the service in Compose:

```yaml
    runtime: nvidia
    environment:
      - NVIDIA_VISIBLE_DEVICES=all
      - NVIDIA_DRIVER_CAPABILITIES=compute,video,utility
```

Then choose **NVIDIA NVENC** in **Settings › Streaming** and play something that needs converting. If it fails, set it back to **Automatic**.

## Windows

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) and keep its default **WSL 2** backend. It asks to install WSL if you don't have it.
2. In Docker Desktop's settings, turn on **Start Docker Desktop when you sign in**, so Flexarr comes back after a restart.
3. Make a folder, say `C:\flexarr`, and save this in it as `docker-compose.yml`:

   ```yaml
   services:
     flexarr:
       image: ghcr.io/danleach-glitch/flexarr:latest
       container_name: flexarr
       ports: ["8383:8383"]
       volumes:
         - flexarr-config:/config
         - D:/Media:/data/media
       restart: unless-stopped

   volumes:
     flexarr-config:
   ```

   Change `D:/Media` to where your media is, with forward slashes.

4. Open PowerShell in that folder and run `docker compose up -d`.

### Performance on Windows

Docker Desktop runs Linux containers inside WSL 2. Files on your Windows drives (`C:`, `D:`, which WSL sees as `/mnt/c`, `/mnt/d`) are reached across a bridge between Windows and Linux, which is much slower than Linux's own disk. Playback is usually fine, but library scans are slower, and a database there is slow and can be unreliable.

That's why the Compose file above keeps `/config` in a Docker **volume** (`flexarr-config`), which lives inside WSL 2, and only your media on a Windows drive. Don't change `/config` to a folder like `C:\flexarr\config`.

> **Note** Your media can stay on any Windows drive, including a second internal disk. A network share mapped to a drive letter usually isn't visible to containers this way. If your media is on a NAS, consider running Flexarr on the NAS itself.

### Hardware transcoding on Windows

Flexarr converts on the processor. Docker Desktop can pass an NVIDIA graphics card through to containers, so the NVIDIA steps above may work, with `gpus: all` under the service in place of `runtime: nvidia`. Intel and AMD graphics aren't available to containers on Windows.

## Next: set it up

Open **http://&lt;server&gt;:8383** in a browser on your home network, using the computer's name or IP address (or **http://localhost:8383** on the computer itself), and choose **Set up Flexarr**. The first browser to reach a new server becomes its owner. Carry on with [First setup](first-setup.md).
