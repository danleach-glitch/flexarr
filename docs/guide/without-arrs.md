---
title: Without Sonarr and Radarr
description: Artwork and details for films and shows that Sonarr and Radarr don't manage, from the files beside them, TVmaze and TMDB, and home video libraries.
---

Flexarr works best alongside Sonarr and Radarr, but it doesn't need them. Films and shows they don't manage, or every film and show if you don't run them, get their artwork and details in three ways, best first:

1. **What's in their folders.** A `.nfo` file and artwork beside a film or show always win, even over Sonarr's and Radarr's.
2. **TVmaze, for shows.** It needs no account or key.
3. **TMDB, for films**, with a free key of your own.

Anything none of these find still plays, named from its folder and files.

## Artwork and .nfo files in your folders

If you've used Kodi, Jellyfin, Emby, Plex or tinyMediaManager, your folders may already hold everything Flexarr needs. Flexarr reads:

| What | Where |
|---|---|
| Details (title, year, plot, genres, rating, age rating, IDs) | `movie.nfo` (or `Film name.nfo`) beside a film, `tvshow.nfo` in a show's folder, `Episode name.nfo` beside an episode |
| Poster | `poster.jpg`, `folder.jpg` or `cover.jpg` |
| Background | `fanart.jpg`, `backdrop.jpg` or `background.jpg` |
| Logo | `clearlogo.png` or `logo.png` |
| Season poster | `season01-poster.jpg` (or `season-specials-poster.jpg`) in the show's folder, or `poster.jpg` in the season's folder |
| Episode still | `Episode name-thumb.jpg` or `Episode name.jpg` beside the episode |

A film on its own in the library's folder, rather than in a folder of its own, uses the same names with its file name in front: `Heat (1995)-poster.jpg`, `Heat (1995)-fanart.jpg`.

Images can be JPEG, PNG or WebP. Flexarr only reads these files: it never writes or changes anything in your media folders.

> **Tip** The IDs in a `.nfo` file (TMDB, IMDb or TheTVDB) help Flexarr find exactly the right title, in Sonarr and Radarr as well as online. A `.nfo` that holds only a link to the title's IMDb or TMDB page works too.

## Shows from TVmaze

Shows Sonarr doesn't know are looked up on [TVmaze](https://www.tvmaze.com), by the show's folder name (and its IDs, when there are any). They get a poster, background, overview, genres, rating and cast, with episode titles, descriptions and stills. There's nothing to set up.

## Films from TMDB

For films Radarr doesn't know, Flexarr can ask [TMDB](https://www.themoviedb.org), using a key of your own:

1. Sign up at [themoviedb.org](https://www.themoviedb.org/signup) (it's free) and open **Settings › API**.
2. Ask for a key. TMDB asks you to register an application first; that's the form for everyone, not something only developers do:
   - Choose personal (non-commercial) use.
   - **Application name**: something like *Flexarr at home*.
   - **Application URL**: your Flexarr's address, or `https://flexarr.io`.
   - **Application summary**: something like *My own media server, showing details and artwork for films I own.*
   - Fill in your contact details and accept the terms. The key appears straight away.
3. In Flexarr, open **Settings › Connections**, choose **Connect** on **TMDB**, and paste the **API key** or the **API read access token**.
4. Choose **Test**, then **Save**.

Films that weren't matched are looked up again straight away. They get a poster, background, logo, overview, genres, age rating, rating, trailer and cast.

## When the match is wrong

A title search can pick the wrong one: a remake, or an older show of the same name. Titles matched by searching are listed under **Check these matches** in [Library health](library-health.md). To choose the right one, open the title, choose **Fix match…** in its **⋯** menu, and search. Without Sonarr and Radarr, the search is TVmaze's (shows) or TMDB's (films), and your choice is kept in Flexarr through every refresh. Fixing a film's match without Radarr needs a TMDB key.

## Home videos

For videos of your own, add a library of the kind **Home videos** in **Settings › Libraries**. Nothing in it is looked up online. Flexarr arranges it by folder:

- Each folder in the library's folder is shown like a show.
- The folders inside that are its seasons, named after the folders. Any folders deeper down are part of the season they're in.
- Each video is an episode, named after its file.

Folders and videos are in name order, with numbers counted as numbers, so `Part 2` comes before `Part 10`. Name them by date (`2019-07 Cornwall`) to keep them in date order. Videos straight in a show's folder come first, and videos straight in the library's folder make a show named after the library's folder.

Each video gets a still from a little way in. A `poster.jpg` or `folder.jpg` in a folder, or a `-thumb.jpg` beside a video, is used instead, and a folder without a poster shows its first video's still.
