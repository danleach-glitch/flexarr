---
title: Requests
description: Find movies, shows and music you don't have yet, request them from any of the apps, and choose which library they go to.
---

With Seerr connected, Flexarr can show you movies and shows you don't have yet, and request them in one go. Seerr passes the request to Radarr or Sonarr, they download it, and it turns up in your library.

Seerr, Overseerr and Jellyseerr all work.

## Before you start

Connect Seerr in **Settings › Connections**: under **Apps & Integrations**, choose **Connect** beside **Seerr**, type its **Address** (usually `http://<host>:5055`) and its **API key** (in Seerr, under **Settings › General**), and choose **Connect**. See [Connect Sonarr, Radarr, Lidarr and Seerr](connect-arrs.md).

Seerr itself needs Radarr and Sonarr set up in its own settings, since it hands requests to them. Connect Sonarr and Radarr to Flexarr too, so requested titles show up the moment they're downloaded (see [Webhooks](connect-arrs.md#webhooks-new-downloads-appear-straight-away)).

> **Note** Music is requested through Lidarr rather than Seerr: see [Music](#music). Audiobooks aren't requested.

## Where you can request

| Where | Discover | Request |
| --- | --- | --- |
| Web app | Yes | From Discover, its search and collections |
| Apple TV | Yes | From Discover and its search |
| iPhone and iPad | No | From **Search** |

## On the web

Choose **Discover** in the sidebar. With Seerr connected, it starts with rows from Seerr:

- **Trending this week**
- **Popular movies**
- **Popular new shows**
- **Coming to cinemas**

Below them is **Your library by genre**, which is everything you already have.

Each poster is marked if you already have it or it's already been asked for. For anything else, choose **Request** under the poster, or open it to see more about it first and choose **Request** there.

### Search for anything

The search box at the top of Discover, **Search for any movie or show**, looks beyond your library. Type a title and you get every matching movie and show, with the ones you already have marked. Request anything else from the results.

### Missing movies in a collection

A movie series collection lists the movies you don't have under **Not in your library**, each with its own **Request** button.

![The Discover page with Trending this week and a Request button under each poster](../../site/guide/img/web-discover.webp "Discover in the web app")

## On the Apple TV

**Discover** is in the sidebar, above **Search**. It shows the same rows as the web: what's trending, popular movies, popular new shows, and what's coming to cinemas.

1. Select a title you don't have to open it. You'll see its IMDb and Rotten Tomatoes scores, cast, who made it, and more like it.
2. Select **Request**.
3. Press **Menu** to go back to Discover.

A title you already have is marked with a gold play badge on its poster, and opens as itself.

**Search**, at the top of Discover next to **Browse**, finds any movie or show, not only what's in your library, so you can request it from there.

Without Seerr connected, Discover on the Apple TV shows your library by genre, and says where to connect Seerr.

## On iPhone and iPad

With Seerr connected, **Search** looks beyond your library too. Movies and shows you don't have are listed under **Not in your library**. Tap one and choose **Request**. Once it's asked for, it's marked **Requested**.

## Who can request

Any profile can request, with two exceptions. Kids profiles, and any profile with an age limit, can't request at all. A profile that can't see any of your movie libraries can't request movies, and one that can't see any TV libraries can't request shows. Libraries a profile sees are set in **Settings › Profiles**. Hidden libraries are left out of Discover and requests for that profile too. See [Profiles](profiles.md).

Flexarr sends every request to Seerr with its own API key. Seerr doesn't know which profile asked: it handles the request under the rules of the Seerr account that key belongs to, which decides whether it's approved automatically. Shows are requested with all their seasons.

## Where requests go

With two or more TV or movie libraries Seerr can add to, the main profile chooses which one each profile's requests land in, by editing the profile in **Settings › Profiles**: **New shows go to** and **New movies go to**. When two people have a TV library each, the shows each of them asks for go to their own. The choice applies to requests from every device. With one library of each kind there's nothing to choose.

## Status and when it arrives

A title's button changes to show where it stands:

| You see | What it means |
| --- | --- |
| **Request** | Not in your library and not asked for. |
| **Requested** | Seerr has it. Radarr or Sonarr will download it when they find it. |
| **Partly in your library** | Some of it is here already, such as some seasons of a show. |
| **In your library** | You have it. |
| **Try again** | The request didn't go through, perhaps because it's already been requested. |

Flexarr remembers who asked for what. When the movie or show arrives, whoever requested it gets a **Your request is ready** notification on their iPhone or iPad, if notifications are on for that profile. See [Notifications and widgets](notifications-and-widgets.md).

To follow a request while it downloads, use Seerr itself, or **Coming soon** in Flexarr's sidebar for what Sonarr and Radarr are expecting.

## Music

Seerr doesn't do music, so music requests go straight to Lidarr. With Lidarr connected, you can find artists and albums you don't have and add them.

**Before you start:** connect Lidarr in **Settings › Connections** (see [Connect Sonarr, Radarr, Lidarr and Seerr](connect-arrs.md)). **Music requests** is on below Lidarr there; turn it off to keep Lidarr connected without anyone requesting music. Lidarr needs a root folder, an indexer and a download client of its own.

Suggestions and charts come from Deezer, which needs no account. Requests use Lidarr's own search, which looks artists and albums up on MusicBrainz.

| Where | Find music |
| --- | --- |
| Web app, Apple TV and Android TV | **Discover**, then **Music** |
| iPhone, iPad and Android phones | **Search**, under **Not in your library** |
| Every app | An artist's page in your music library |

**Discover › Music** has these rows:

- **Like artists you play**: artists like the ones you listen to most.
- **New from your artists**: this year's releases by artists you have, that you don't have yet.
- **Top albums**: what's charting now.

Below them are genres. Each one opens its best-known artists.

There are two kinds of music request:

- **Request album** downloads that album and nothing else. If Lidarr doesn't have the artist yet, it adds them without their other albums.
- **Follow artist** has Lidarr download the artist's new releases from now on. Their older albums are still requested one at a time.

An artist's page in your library ends with **Not in your library**: the albums of theirs you don't have, each with **Request album**. Below that, **Fans also like** suggests artists like them.

When an album you asked for arrives, you get a **Your request is ready** notification. When an artist you follow puts out an album, you get **New from** the artist.

The same profiles can request music as can request movies and shows. Kids profiles, profiles with an age limit and profiles that can't see a music library can't. With two or more music libraries Lidarr adds to, **New music goes to** in the profile's settings picks which one gets the profile's requests.

## If requests don't work

- **Discover has no Seerr rows:** check Seerr shows a green dot in **Settings › Connections**. If it says it can't be reached, or the API key isn't right, edit it there.
- **"Requests need a Seerr server":** Seerr isn't connected yet.
- **"This profile can't request movies" (or shows):** that kind of library is hidden from the profile you're using.
- **"Seerr didn't accept the request":** it may already be requested, or Seerr turned it down. Seerr's own requests page says why.
- **"Lidarr doesn't know" an artist or album:** Lidarr's search couldn't find it on MusicBrainz, or found more than one artist with that name. Add it in Lidarr itself.
- **"Lidarr didn't take the request":** Lidarr isn't reachable, or turned the request down. Lidarr's **Activity** page says why.
