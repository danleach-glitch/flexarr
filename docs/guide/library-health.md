---
title: Library health
description: Find spare copies and the space they use, files that can't be read, standard-definition movies, wrong matches and music problems, and fix them with Sonarr, Radarr and Lidarr.
---

Library health is a check-up for your movies, TV and music. It shows what's wrong, how much space spare copies take, and a button to fix each problem, usually by asking Sonarr or Radarr to do the work.

Open it in the web app from **Settings › Libraries › Library health**, as the main profile with settings unlocked. The **Overview** page in Settings links to it too.

![Library health with a score of 99, Run health check, a movie that only exists in standard definition and one missing artwork](../../site/guide/img/health.webp "Settings › Libraries › Library health")

## The health score

At the top is a score out of 100: the share of your movies, episodes and albums with nothing wrong. Next to it is a breakdown of what's affecting the rest, such as spare copies, unreadable files or missing artwork.

Groups with nothing in them are left out, and a line underneath says what's all clear ("every title has a single copy", "every movie has an HD copy").

## Spare copies

Sometimes a movie or episode ends up with more than one file: an upgrade that didn't replace the old one, or the same file in two folders. **Spare copies** lists each one, how many copies it has and how much space the spares use. The total you'd free is in the group's description.

- **Remove spares** on a title deletes its extra files.
- **Remove all spare copies** does the whole list, after asking first, with a progress bar.

Flexarr always keeps two copies safe: the best one (highest quality), and the one Sonarr or Radarr are tracking. Usually they're the same file. When they're not, both stay and the summary tells you; open the title's row to see each copy with its resolution, codec and size, and use **Delete this copy** on the one you don't want. Copies that are the same file twice are marked **Identical**.

> **Important** Removing spares deletes files from your disk. It can't be undone.

## Run a health check

The **Health check** panel looks at every file: whether it's still on disk, whether it's empty, and whether it can be read. Choose **Run health check** and it works through your library in the background, with a progress bar. Problems are listed under **Missing from disk**, **Empty files** and **Still unreadable**, each with **Search again** to have Sonarr or Radarr fetch it again.

## Files that can't be read

Files Flexarr couldn't open when it scanned them are listed under **Files that can't be read**, with the reason. This is usually a damaged or unfinished download. **Get a new copy** asks Sonarr or Radarr to search for the title again; a fresh copy fixes playback.

### Audible books

From the next release, audiobooks bought from Audible as `.aax` or `.aaxc` files are listed here too, as protected by Audible's DRM. Flexarr can't play them. Convert them to `.m4b` with a tool that uses your own Audible account, put the `.m4b` in the book's folder in place of the original, and rescan.

## Standard-definition movies

**Only in standard definition** lists movies whose best copy is below 720p. **Find a better copy** asks Radarr to search for the movie again, so it can upgrade it within the quality profile you've set in Radarr.

When Sonarr or Radarr are already fetching a new copy, the row shows how far along it is ("Downloading 45%") instead of the button.

## Check these matches

Movies and shows that Sonarr and Radarr don't manage get their details by searching for the folder's name. That can find the wrong title: an older show with the same name, or a remake. They're listed under **Check these matches**.

- If a match is right, choose **Ignore**. It leaves the list and stays as it is.
- If it's wrong, choose **Fix match** (below).

## Missing artwork

Titles with no poster or details are listed under **Missing artwork**. Artwork comes from `.nfo` files and pictures beside the title, from Radarr and Sonarr, from TVmaze for shows, and from TMDB for movies if you've added a key. If Radarr and Sonarr aren't connected, connect them in **Settings › Connections** ([Connect Sonarr, Radarr, Lidarr and Seerr](connect-arrs.md)), or see [Without Sonarr and Radarr](without-arrs.md). Otherwise the title wasn't found: fix its match, or rename its folder to the form `Title (Year)` and rescan.

## Music

Music has four groups of its own:

- **Albums without a cover**: no cover file beside the tracks, none in them, and none from Lidarr. **Fix match** on the row finds the album, and its cover with it.
- **Albums twice**: the same release more than once, usually two folders of one album. **Merge** asks which to keep, then moves the others' tracks to it.
- **Artists under two names**: one artist filed under different spellings, such as *Beyoncé* and *Beyonce*, usually because their files' tags differ. **Merge** makes them one artist with all their albums, and an album both spellings had becomes one album, unless both copies have the same tracks: those stay under **Duplicate albums**.
- **Tracks twice**: an album with the same track more than once, usually two copies in one folder. Remove the spare in Lidarr or the Finder.

Merging happens in Flexarr only: no files are moved or deleted, and nothing changes in Lidarr. New tracks under the merged name or in the merged folder land in the one you kept.

## Fix match

**Fix match** puts right a movie or show that's been matched to the wrong title. It's on each row in Check these matches and Missing artwork, and in the **⋯** menu on any movie's or show's page (main profile only).

1. Choose **Fix match**. The dialog shows the full path of the movie's files, or the show's folder, so you can see what's being matched.
2. Search for the right title. Try the title as it was first released, or add the year.
3. Pick it. Each result says whether it's the **Current match**, already in Sonarr or Radarr, or would be added to them.

Flexarr keeps your choice through every later refresh. If the title wasn't in Sonarr or Radarr, fixing the match adds it there using the folder it's already in, so they manage it from then on. Without Sonarr, the search is TVmaze's; without Radarr, it's TMDB's, which needs a TMDB key (see [Without Sonarr and Radarr](without-arrs.md)). Then your choice is kept in Flexarr, and nothing is added anywhere.

> **Note** If Sonarr or Radarr already use that folder for a different title, Flexarr won't change it for you. Remove that title in Sonarr or Radarr (keeping its files), then fix the match again.

To undo a fix, open Fix match again and choose **Match automatically again**.

### Albums and artists

Albums and artists have **Fix match…** in the **⋯** on their page too (main profile only). The results are what Lidarr has, when it's connected, then Deezer's, so a music library without Lidarr can be fixed as well. Once you pick one, choose its cover: the match's own, another result's, or **Keep this one**. Nothing is added to or followed in Lidarr, and refreshes keep your choice. **Match automatically again** hands the album back to its files' tags and Lidarr's own matching.

## Edit details

To change what a movie, show or album says without matching it to something else, choose **⋯ › Edit…** on its page (main profile only, on the web):

- Movies and shows: title, **Sort as**, original title, year, description, genres, and studio or network.
- Albums: title, year, type and genres.

Edits are **Locked** so refreshes keep them, while artwork, ratings, cast and episodes still update. The editor says **Edited**, and **Back to automatic** hands the details back to Radarr, Sonarr, Lidarr or wherever they came from. Fixing the match to a different title replaces your edits with the new title's, and the dialog says so first. Age ratings aren't edited here: use **Allow for kids** and **Block for kids** (see [Profiles](profiles.md)).

## Deleting a title

To delete a movie, show, episode or audiobook, open its page, then **⋯ › Delete…** (main profile only). The dialog says who'll do the deleting before you confirm:

- **A movie Radarr manages:** Radarr deletes the movie's files from disk and stops tracking it.
- **A show Sonarr manages:** Sonarr deletes every episode's files and stops tracking the show.
- **An episode:** Sonarr deletes the episode's file and stops downloading it, so it doesn't come back.
- **A title neither manages, or an audiobook** (from the next release): Flexarr deletes the files itself and lists them first. It also removes folders left empty, but never a library's own folder or anything outside it.

If Sonarr or Radarr can't be reached or refuse, nothing is removed from Flexarr either.

> **Important** Deleting can't be undone. Back up anything you want to keep first.

## Folders on hold

If a drive or network share isn't connected when Flexarr scans, its titles aren't removed; the folder is put **on hold** instead, and your watch history is kept. Held folders are listed in Library health with the reason, such as "The folder isn't there. Is its drive or share connected?". Reconnect the drive and the next scan carries on as normal.

If you really did empty the folder on purpose, choose **Confirm removal** and its titles go.
