---
title: Media paths
description: Why Flexarr needs to see your files at the same paths as Sonarr, Radarr and Lidarr, how to line them up in Docker, and how to use path mappings when you can't.
---

Sonarr, Radarr and Lidarr talk to Flexarr about files by their **path**. When Sonarr imports an episode, its webhook says "I've just put a file in `/tv/Some Show/Season 1`", and Flexarr rescans that folder. When you delete a film, Flexarr works out whether Radarr manages it by comparing paths. If Flexarr sees the same file somewhere else, say `/data/media/tv/Some Show/Season 1`, it can't tell they're the same folder.

This mostly matters in Docker, where each container sees your disks at whatever paths its volumes give it. There are two ways to fix it: give every container the same paths (best), or tell Flexarr how to translate them.

## The best fix: the same paths everywhere

Mount your media at the same path in every container. A common layout is one parent folder, `/data/media`, with a folder for each kind:

```yaml
services:
  flexarr:
    image: ghcr.io/danleach-glitch/flexarr:latest
    ports: ["8383:8383"]
    volumes:
      - /srv/flexarr:/config
      - /srv/media:/data/media

  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    volumes:
      - /srv/sonarr:/config
      - /srv/media:/data/media

  radarr:
    image: lscr.io/linuxserver/radarr:latest
    volumes:
      - /srv/radarr:/config
      - /srv/media:/data/media

  lidarr:
    image: lscr.io/linuxserver/lidarr:latest
    volumes:
      - /srv/lidarr:/config
      - /srv/media:/data/media
```

The left side (`/srv/media`) is where the media is on the host. The right side (`/data/media`) is where every container sees it. Because the right side is the same everywhere, a file is `/data/media/tv/Some Show/…` to all of them.

Then use those paths everywhere:

- Root folders in Sonarr, Radarr and Lidarr: `/data/media/tv`, `/data/media/movies`, `/data/media/music`
- Library folders in Flexarr's **Settings › Libraries**: the same three

> **Tip** If your downloads folder is on the same disk, mount the parent of both (for example `/srv/data:/data`, with `media` and `downloads` inside) in the *arrs. That lets them move files instead of copying them. Flexarr only needs to see the media.

## When the paths can't match: path mappings

Sometimes you can't change the volumes, or the *arrs run in Docker and Flexarr runs natively on a Mac. Then add a **path mapping**: "what Sonarr calls *this*, Flexarr calls *that*".

### In Settings

Go to **Settings › Connections** and find **Media folders**. Each root folder of a connected Sonarr, Radarr or Lidarr has a line saying where it is on this server.

Usually Flexarr works this out by itself. It compares the folders the apps keep their shows, films and artists in with the folders Flexarr found the same titles in. Sonarr's `/tv/Severance (2022)` and Flexarr's `/data/media/tv/Severance (2022)` mean `/tv` is `/data/media/tv`. When enough titles agree, Flexarr saves the mapping itself, on its next library scan, and the line goes green.

When it's less sure, the line is amber:

- **Use this** saves the folder Flexarr suggests.
- **Choose another** or **Choose folder** lets you pick the folder on the server yourself.

The app's line under **Apps & Integrations** also says when one of its folders isn't found, with **Find it** to jump to it.

To write a mapping yourself, go to **Path mappings** at the bottom of **Media folders** and choose **Edit mappings** (or **Add a mapping** if there are none yet). In **Path in the app**, type the path the *arr app uses, such as `/tv`. In **Path on this server**, type where Flexarr sees the same folder, such as `/data/media/tv`. **Add a mapping** adds another line, and the bin takes one out. Then choose **Save**. Flexarr rescans your libraries straight away, and titles that weren't linked to the apps are matched again as the scan finishes.

If you take out a mapping Flexarr saved itself, it doesn't add it back.

### In config.json

The same mappings are kept in the server's `config.json` (in `/config` in Docker, `~/.flexarr/data` on a Mac) as `pathMappings`:

```json
{
  "pathMappings": [
    { "from": "/tv", "to": "/data/media/tv" },
    { "from": "/movies", "to": "/data/media/movies" }
  ]
}
```

`from` is the path in Sonarr, Radarr or Lidarr; `to` is the path Flexarr sees. Leave the rest of the file as it is. Flexarr notices the change by itself; restart it if it doesn't seem to.

### How mappings are applied

- A mapping covers its folder and everything inside it: `/tv` maps `/tv/Some Show/Season 1/episode.mkv` to `/data/media/tv/Some Show/Season 1/episode.mkv`.
- It matches whole folder names, so `/tv` doesn't match `/tv4k`.
- If more than one mapping fits, the longest one wins. You can map `/data` broadly and `/data/4k` somewhere else.
- Paths that no mapping covers are used as they are.

## Worked examples

### Sonarr in Docker at `/tv`, Flexarr at `/data/media/tv`

Sonarr's container has `-v /srv/media/tv:/tv`, so its root folder is `/tv`. Flexarr's has `-v /srv/media:/data/media`, so its TV library is `/data/media/tv`.

| Path in the app | Path on this server |
| --- | --- |
| `/tv` | `/data/media/tv` |

Or change Sonarr's volume to `-v /srv/media:/data/media`, set its root folder to `/data/media/tv`, and you won't need a mapping at all.

### *arrs in Docker on a Mac, Flexarr native

Sonarr and Radarr run in Docker Desktop with `-v /Volumes/Media/data:/data`. Flexarr runs on the Mac itself and sees the drive as `/Volumes/Media/data`.

| Path in the app | Path on this server |
| --- | --- |
| `/data` | `/Volumes/Media/data` |

## Signs the paths don't match

- **New downloads don't appear until the next scan.** Webhooks arrive, but Flexarr can't find the folder they name, so nothing happens until its scheduled scan every 12 hours.
- **Deleting a film or show offers to delete the files itself.** The delete box says Radarr or Sonarr "doesn't manage" a title you know it does, because Flexarr couldn't match the paths.
- **Importing your history from Plex** matches fewer titles than expected, if Plex saw your files at other paths too.
- **Albums don't pick up Lidarr's artwork**, because Flexarr matches them to Lidarr by folder.

To check, compare a file's path in the *arr app (on a title's **Files** tab, say) with its path in Flexarr, shown under each library's folders in **Settings › Libraries**. If the start of the path differs, add a mapping for that part.

> **Note** Path mappings only translate what the *arr apps and Plex tell Flexarr. Flexarr's own library folders must always be paths it can see.
