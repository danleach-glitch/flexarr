---
title: 4K and 1080p versions
description: Keep a film or show in more than one version, such as 4K and 1080p or a director's cut. Flexarr plays the best one each device can take, and you can choose.
---

You can keep the same film or show more than once: a 4K copy for the TV and a 1080p one for phones, or a director's cut beside the original. Flexarr treats them as versions of one title. It's listed once, has one page, and you carry on from the same place whichever version you play.

## Setting it up

Either of these works:

- **Two libraries.** Add a library for each, such as **Movies** and **Movies in 4K**, typically with [a second Radarr or Sonarr](connect-arrs.md). Flexarr links titles that share a TMDB, TVDB, TVmaze or IMDb ID, so there's nothing else to do. A show's page brings together the seasons and episodes of both.
- **One folder.** Put the files side by side in the film's folder.

To name a cut, put it in the file's name, such as `Blade Runner (1982) {edition-Final Cut}.mkv`. Without a name, a cut still counts as its own version when its length is clearly different.

Each library keeps its own page, so **Movies in 4K** still lists everything in it.

## Which one plays

**Automatic** picks the best version your device can play without converting it, keeping to the cut of your best-quality copy. On a 4K TV that's the 4K copy. On a phone that can't take 4K it's the 1080p copy of that cut, so nothing has to be converted.

## Choosing one

On a film's or episode's page, choose **⋯**, then **Version**. Your choice is remembered for your profile on all your devices. A choice is for that one episode: the next one plays with **Automatic**, which is also how you go back to the best pick.

If the file you chose is replaced, a file with the same name and cut takes its place. If your choice would need converting on this device while another version of that cut wouldn't, the other one plays, and your choice stays for next time.

Skip intro carries across versions of the same length. Your place is kept in seconds, so going from one cut to a longer one can land you somewhere slightly different.

## Downloads

A download at a lower quality starts from the smallest version that's good enough, so less needs converting. **Original** uses your chosen version, or the best one.

## Hidden libraries and age limits

A version in a library a profile can't see isn't a version for that profile. It doesn't appear in the list and it can't play.

## Deleting

**Delete** on a film or show in the web app deletes every copy, in every library, including libraries hidden from your profile (those take the admin PIN). The dialog names each library first. To keep one copy, delete the other in Sonarr or Radarr, or from its folder, instead.

## Avoiding spare copies

Two files with the same resolution, HDR type and length aren't versions, they're spares. They don't appear under **Version**. Side by side in one folder, [Library health](library-health.md) can remove them for you. The same file in two libraries is listed there under **Versions** instead, so you can delete the one you don't want in its Sonarr or Radarr.
