---
title: Install on a Synology NAS
description: Run Flexarr on a Synology NAS as a Container Manager project, with its settings folder, your media and the right permissions.
---

On a Synology NAS, Flexarr runs as a container in **Container Manager**, Synology's Docker app. You need DSM 7.2 or later and a model that supports Container Manager. Flexarr's image is built for both Intel/AMD and 64-bit ARM processors, so if Container Manager installs on your model, Flexarr should run.

## 1. Install Container Manager

Open **Package Center**, search for **Container Manager** and install it. Installing it creates a shared folder called `docker`.

## 2. Make the folders

Container Manager won't create missing folders for you, so make them first in **File Station**:

1. In the `docker` shared folder, create a folder called `flexarr`.
2. Inside it, create a folder called `config`. This holds Flexarr's settings, database and caches.

Note where your media is, for example `/volume1/data/media` with `movies`, `tv` and `music` inside. If you run Sonarr, Radarr and Lidarr on the NAS, use the folder they use.

## 3. Find your user ID

You tell the Flexarr container which user to run as, with `PUID` and `PGID`, so it can write to its folder and read your media. You need that user's numeric ID and group ID.

1. In **Control Panel › Terminal & SNMP**, tick **Enable SSH service** and apply.
2. From a computer, connect with `ssh yourname@your-nas` and run `id`:

   ```bash
   id
   # uid=1026(yourname) gid=100(users) ...
   ```

3. Note the two numbers (here `1026` and `100`). You can turn SSH off again afterwards.

> **Tip** Use an account that can read your media shares and write to the `docker` share. If your \*arrs already run with a `PUID` and `PGID`, use those same numbers.

## 4. Create the project

1. Open **Container Manager** and go to **Project**, then **Create**.
2. **Project name**: `flexarr`.
3. **Path**: choose **Set path** and pick `docker/flexarr`.
4. **Source**: choose **Create docker-compose.yml** and paste this, changing the IDs and the media path to yours:

   ```yaml
   services:
     flexarr:
       image: ghcr.io/danleach-glitch/flexarr:latest
       container_name: flexarr
       environment: [PUID=1026, PGID=100]
       ports: ["8383:8383"]
       volumes:
         - /volume1/docker/flexarr/config:/config
         - /volume1/data/media:/data/media
       restart: unless-stopped
   ```

5. Click **Next**. You can skip the web portal settings. Click **Next** again, then **Done**.

Container Manager downloads the image and starts Flexarr. The project shows as running when it's ready.


### About the media path

Mount your media at the **same path** inside the container that Sonarr, Radarr and Lidarr use inside theirs. If they all see `/data/media/tv/...`, the paths they report to Flexarr match what Flexarr sees and everything lines up. If they don't, you can map one to the other in **Settings › Connections › Path mappings**. See [Media paths](media-paths.md).

### Port

Flexarr listens on port `8383`. If you use DSM's firewall (**Control Panel › Security › Firewall**), allow that port from your home network.

## Hardware transcoding

Flexarr converts video on the processor by default. Many Intel-based Synology models have a graphics chip that can do this work, and the Flexarr image includes the Intel drivers. If your NAS has `/dev/dri` (check with `ls /dev/dri` over SSH), add this to the service in the project's `docker-compose.yml`:

```yaml
    devices:
      - /dev/dri:/dev/dri
```

Then choose **Intel Quick Sync** or **VA-API (Intel and AMD on Linux)** in **Settings › Streaming › Hardware acceleration** and play something that needs converting. If it fails, set it back to **Automatic**.

This matters less than you might expect: the iPhone, iPad and Apple TV apps play most files directly, so the server only converts for web browsers, quality-limited streams away from home and some downloads. On a NAS with a low-power processor, keep those to one or two at a time.

## Updating

When a new release is out, Container Manager's **Image** page marks the Flexarr image as having an update. Select it and choose **Update**, and Container Manager restarts Flexarr on the new version. Your settings stay in `docker/flexarr/config`. See [Updating](updating.md).

## Next: set it up

Open **http://&lt;your-nas&gt;:8383** in a browser on your home network, using the NAS's name or IP address, and choose **Set up Flexarr**. The first browser to reach a new server becomes its owner. Carry on with [First setup](first-setup.md).
