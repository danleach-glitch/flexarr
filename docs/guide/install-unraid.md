---
title: Install on Unraid
description: Add Flexarr to Unraid as a Docker container by hand, with its appdata folder, your media share and the right user.
---

On Unraid, Flexarr runs as a Docker container. There's no Community Applications template for it yet, so you add the container by hand. It takes a few minutes and you only do it once.

## Before you start

- Make sure Docker is enabled, under **Settings › Docker**.
- Know where your media lives, for example the `data` share at `/mnt/user/data/media`. If you run Sonarr, Radarr and Lidarr on this server, look at the media path in their containers' settings: you'll give Flexarr the same one.

## Add the container

1. Go to the **Docker** tab and click **Add Container** at the bottom of the page.
2. Fill in the top of the form:
   - **Name**: `flexarr`
   - **Repository**: `ghcr.io/danleach-glitch/flexarr:latest`
   - **Network Type**: `Bridge`
3. Switch the form to the advanced view with the **Basic View** toggle at the top right, and fill in:
   - **WebUI**: `http://[IP]:[PORT:8383]/`
   - **Extra Parameters**: `--user 99:100`
4. Add the port, the settings folder and your media with **Add another Path, Port, Variable, Label or Device** at the bottom, one at a time:

   | Config Type | Name | Container | Host |
   | --- | --- | --- | --- |
   | Port | Web UI | `8383` | `8383` |
   | Path | Config | `/config` | `/mnt/user/appdata/flexarr` |
   | Path | Media | `/data/media` | `/mnt/user/data/media` |

   Change the media paths to match yours. The container path should be the one your \*arrs use inside their containers.
5. Click **Apply**. Unraid downloads the image and starts Flexarr.


### Why `--user 99:100`

The Flexarr container runs as its own user rather than root, and doesn't read `PUID` or `PGID` variables. `--user 99:100` runs it as Unraid's usual `nobody:users`, the owner of files on your shares and in appdata, so it can write its settings and read your media. If your other containers use a different `PUID` and `PGID`, use those numbers instead.

### Media paths

Sonarr, Radarr and Lidarr tell Flexarr where each new file is. When all of them see your media at the same container path (here `/data/media`), those paths match and new downloads appear straight away. If they don't, map one to the other in **Settings › Connections › Path mappings**. See [Media paths](media-paths.md).

## Hardware transcoding

Flexarr converts video on the processor by default. On Linux, **Settings › Streaming › Hardware acceleration › Automatic** means the processor.

**Intel and AMD graphics.** Passing `/dev/dri` to the container isn't enough yet: the Flexarr image doesn't include the Intel or AMD drivers that VA-API and Quick Sync need. Leave hardware acceleration on **Automatic**.

**NVIDIA graphics** may work, because Unraid's NVIDIA driver supplies the libraries to the container. This is untested, so treat it as an experiment:

1. Install the **Nvidia Driver** plugin from the Apps tab and reboot if it asks.
2. Edit the Flexarr container. Add `--runtime=nvidia` to **Extra Parameters**, after `--user 99:100`.
3. Add two **Variables**: `NVIDIA_VISIBLE_DEVICES` set to `all` (or your card's ID from the plugin's page), and `NVIDIA_DRIVER_CAPABILITIES` set to `compute,video,utility`.
4. Apply, then choose **NVIDIA NVENC** in **Settings › Streaming** and play something that needs converting. If it fails, set it back to **Automatic**.

> **Note** Conversion matters less than you might think. The iPhone, iPad and Apple TV apps play most files directly, so the server mainly converts for web browsers, quality-limited streams away from home and some downloads.

## Updating

The Docker tab shows when a newer Flexarr image is available. Click **apply update** under the container to move to it. Your settings stay in `appdata/flexarr`. See [Updating](updating.md).

## Next: set it up

Click Flexarr's icon in the Docker tab and choose **WebUI**, or open **http://&lt;your-unraid&gt;:8383** in a browser on your home network. Choose **Set up Flexarr**: the first browser to reach a new server becomes its owner. Carry on with [First setup](first-setup.md).
