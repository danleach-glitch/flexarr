---
title: Watchlist
description: Keep a list of films and shows to watch later, whether you have them yet or not, and hear when they arrive.
---

Each profile has its own watchlist: films and shows you want to watch later, whether they're in your library yet or not. When something on it arrives, you hear about it, and it waits on Home under **Ready from your watchlist**.

## Adding and removing

| Where | How |
| --- | --- |
| Web app | **Watchlist** on a title's page, the bookmark under a poster in Discover and search, or **Add to watchlist** in a poster's menu. |
| iPhone and iPad | **Watchlist** on a film's or show's page, **Add to watchlist** when you press and hold a poster, or tap a title under **Not in your library** in Search. |
| Apple TV | **Watchlist** on a title's page (Discover's too), or **Add to watchlist** when you press and hold a poster. |
| Android | **Watchlist** on a film's or show's page, **Add to watchlist** when you press and hold a poster, or tap a title under **Not in your library** in Search. |
| Siri | "Save Severance in Flexarr" for a title in your library, or "Add to my watchlist in Flexarr" and then say any title, whether you have it yet or not. |
| Assistants | Ask an assistant connected over [MCP](assistant-mcp.md) to add or remove a title, even one you don't have yet. |

Choose **On watchlist** on its page to take a title off again, or **Remove from watchlist** when you press and hold its poster (on the web, the bookmark on its card). Adding a title twice keeps one, and adding one you took off brings it back.

Titles leave the list by themselves once you've dealt with them: a film when you've watched it, a show when you've started it (it carries on in **Continue watching**). A title you've seen before stays until you watch it again.

## Your watchlist

On the web, **Watchlist** is in the sidebar under **Browse**. On iPhone, iPad and Android it's at the top of **Libraries**, and on the Apple TV it's in the sidebar beside **Discover**. Android and iPhone have a **Watchlist** widget too (see [Notifications and widgets](notifications-and-widgets.md)).

Each title says where it's got to:

| You see | What it means |
| --- | --- |
| Its usual poster | It's in your library. Play it. |
| **On its way** | It's been requested, or Sonarr or Radarr are getting it. |
| **Out** and a date | It isn't released yet. |
| **Request** | It's out, but nobody's asked for it. Choose **Request** to add it to your library (see [Requests](requests.md)). |

A title you don't have also says which streaming services it's on, such as **On Netflix**, for the country set as **Streaming Region** in Seerr.

Every app can show only one kind (**Ready to watch**, **On its way**, **Coming** or **Not available**), only films or only shows, and sort the list: **Ready first** (the default), **Recently added**, **Release date** (newest first) or **Title**.

Titles you don't have yet need Seerr connected (see [Requests](requests.md)). Without it, you can still keep a watchlist of what's in your library.

## When something arrives

When a title on your watchlist arrives in your library, your iPhone, iPad or Android phone says **From your watchlist**, if that profile has notifications on. It follows the **Requests** switch in the app's notification settings. If you also requested the title, you hear once, as **Your request is ready**. See [Notifications and widgets](notifications-and-widgets.md).

## Requesting what you add

The main profile can have a profile's watchlist request what it adds: open **Settings › Profiles**, edit the profile, and turn on **Request what … adds to the watchlist**. It's off to start with, and only shows when Seerr is connected.

With it on, a film or show that isn't in your library and hasn't been asked for is requested as soon as it's added. It never requests:

- titles that are already in your library or already requested
- films for a profile that can't see any film library, or shows for one that can't see any TV library
- titles brought across from another service rather than added in Flexarr

Taking a title off the watchlist doesn't cancel its request.
