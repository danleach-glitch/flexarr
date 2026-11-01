---
title: Trakt
description: Keep what you watch, where you're up to and your watchlist in step with Trakt, both ways.
---

Connect a profile to your [Trakt](https://trakt.tv) account and Flexarr keeps the two in step: what you watch, where you're up to, and your [watchlist](watchlist.md). Each profile connects its own account, and nothing is shared until that person connects.

## Connecting

1. Open **Settings › Trakt** in the web app, on iPhone or iPad, or on the Apple TV.
2. Choose **Connect**. Flexarr shows a code.
3. On any phone or computer, go to **trakt.tv/activate** and enter the code. On a TV, scan the QR code to open that page on your phone. On iPhone, **Copy code and open Trakt** copies the code and opens the page; on the web, **Copy code**.

You don't need to wait on that screen: your server waits for the code for 10 minutes. Once you've entered it, Settings › Trakt shows your Trakt name.

A Trakt account can be connected to one profile only, so nothing is counted twice.

## Bringing your history across

Once you're connected, **Bring your Trakt history across** shows you what would change before anything does (**See what changes** on the web). For example: *Trakt has 12 films and 340 episodes. 200 are here, 41 not yet watched in Flexarr*. Choose **Bring across** to go ahead.

- Anything watched on Trakt is marked watched in Flexarr, with the date you watched it.
- Anything watched in Flexarr that Trakt doesn't have goes to Trakt.
- Titles you've watched that aren't on your server yet are remembered. When one arrives, it's already marked watched.
- Nothing is ever marked unwatched, on either side.

It's offered once. When it's done, Settings says **History brought across**; after that, everything stays in step by itself. To bring it across again, disconnect and connect again.

## Staying in step

After that, it all happens by itself:

| What | How |
| --- | --- |
| Watching | Trakt shows what you're watching while it plays. When Flexarr counts a film or episode as watched (near the end, or once the end credits start), so does Trakt. |
| Marking watched | **Mark as watched** in Flexarr goes to Trakt, and so does anything you watched on a download away from the server. |
| Watched elsewhere | Plays in other apps connected to Trakt come to Flexarr within a few minutes. |
| Where you're up to | Stop part way and Trakt keeps your place. Stop in another app connected to Trakt, and Flexarr picks up from there. |
| Your watchlist | Add or remove a title in either place, and it's added or removed in the other. Titles that come from Trakt are never requested by themselves; they show **Request** if they're not here. |

**Marking something unwatched** in Flexarr leaves Trakt as it is: your Trakt history stays true, and Flexarr won't mark it watched again.

**Sync now** in **Settings › Trakt** checks straight away rather than waiting (once a minute at most).

Everything a connected profile watches goes to Trakt, whichever library it's in, and Trakt profiles are public unless you make yours private on trakt.tv.

## Lists and picks

- **Lists**: in **Settings › Trakt**, choose any of your Trakt lists, or ones you've liked, and each shows up in **Collections** on the web, just for you, kept in step with Trakt. Titles on the list that are here show as usual. Films that aren't here show with **Request**.
- **For you, from Trakt**: once you're connected, Discover on the web and Apple TV has a row of Trakt's picks for you, with titles you have first and ones you've watched left out.

## If something's wrong

| You see | What to do |
| --- | --- |
| **Trakt signed Flexarr out** | You revoked Flexarr on trakt.tv, or it wasn't used for months. Choose **Connect** again; your history stays. |
| **Trakt's watchlist is full** | A free Trakt account holds a limited number of titles. New titles stay on Flexarr's watchlist; make room on Trakt and they'll go across. |
| **Waiting to send** | Trakt or your internet is down. Flexarr keeps the changes and sends them when it can. |
| No **Trakt** in Settings, or **Trakt isn't available on this server.** on the web | Kids profiles don't have Trakt. Otherwise, your server can't reach Flexarr's Trakt service. Check it can reach the internet. |

**Disconnect** in **Settings › Trakt** stops it all and makes Trakt forget Flexarr. What you've watched stays as it is, in both places. Making a profile a kids profile, or removing it, disconnects it too.

[Backups](backups.md) include each profile's Trakt sign-in. Restoring an older backup may mean connecting again, as Trakt replaces a sign-in each time it's renewed.

## Your own Trakt app

Flexarr uses its own Trakt app, so there's nothing to set up. If you'd rather use your own (Trakt only lets VIP members create one), the main profile can enter its **Client ID** and **Client secret** in **Settings › Connections › Trakt**. Everyone then connects again, as sign-ins belong to the app that gave them.
