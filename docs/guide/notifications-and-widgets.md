---
title: Notifications and widgets
description: Get told on your iPhone or iPad when something new is ready, add podcast widgets to your Home Screen, and fill the Apple TV's Top Shelf.
---

Flexarr can tell your phone when something new arrives, keep podcasts on your Home Screen and Lock Screen, and put what you're watching at the top of the Apple TV's Home screen.

## Notifications (iPhone and iPad)

### Turn them on

1. In the Flexarr app, go to **Settings › Notifications**.
2. Turn on **Notifications**, and allow them when iOS asks.
3. Under **Tell me about**, choose what you want to hear about.
4. Tap **Send test notification** to check one arrives.


If iOS has notifications switched off for Flexarr, the page says so and offers **Open Settings** to allow them there first.

### What you can be told about

- **Films** and **Shows**: something new in the library that you haven't seen, in a library your profile can see. A brand-new show says so, and several episodes of one show arriving together are one notification. Upgrades and replacements of things you already have don't count.
- **Podcasts**: a new episode of a show you follow.
- **Recordings**: a Live TV recording has finished. This appears when Live TV is set up.
- **Requests**: something you asked for through Flexarr is ready. This takes the place of the ordinary "new" notification for it.
- **Upcoming** (off to start with): each morning, at the hour you choose under **At**, what's airing or out today from the shows you watch and the films you asked for.

A library's first scan never notifies, and a big batch arriving at once is summed up in one notification, so adding a new library or a season pack won't flood your phone.

Notifications have buttons. Films and episodes have **Play**, **Download** and **Play on Apple TV**. Podcast episodes have **Play** and **Download**. Tapping the notification opens the title.

### One profile per phone

Each phone gets notifications for one profile: the one it was on when you turned them on. A phone the household shares gets one set, not one for each person. If you switch profile, **Settings › Notifications** offers **Get notifications for … instead**. See [Profiles](profiles.md).

Signing the phone out, or signing it out from **Settings › Security** on the web, stops its notifications.

### How they reach your phone, and what's shared

Apple only delivers notifications that come from the app maker's own key, and that key can't be handed to every Flexarr server. So your server sends each notification through a small relay at flexarr.io, which hands it to Apple to deliver.

Here's what goes where:

1. When you turn notifications on, your phone makes a random secret key and gives it to **your own server**. It never leaves the two of them.
2. When something new arrives, your server writes the notification (title, text, artwork link and buttons) and **encrypts it** with that key.
3. Your server sends the relay the encrypted notification and your phone's Apple push address. The relay can't read the notification, and keeps nothing.
4. The relay passes it to **Apple** under a plain "Something new is ready" message.
5. On your phone, Flexarr **decrypts** it and shows the real title, text, artwork and buttons.

So the relay and Apple see that your phone got a notification, and when, but not what it says. Artwork in a notification is a public poster address, which your phone fetches to show it.

The one exception is the Lock Screen panel for [a TV you're following](iphone-remote.md#on-the-lock-screen). Apple reads its updates directly, so they can't be encrypted: they carry only whether the TV is paused, how far in it is, how long it is and its speed. The title and picture are put there by your phone and never pass through the relay. The [privacy policy](https://flexarr.io/privacy) has the details.

> **Note**
> A server can use no relay at all. Set the environment variable `Flexarr__Notifications__Enabled=false` (or `Flexarr:Notifications:Enabled` set to `false` in the server's settings) and restart. The app then says your server has notifications turned off.

## Widgets (iPhone and iPad)

Flexarr has three widgets. Add them the usual way: touch and hold an empty part of the Home Screen, tap **Edit › Add Widget**, and search for Flexarr.

- **Continue listening** shows the episode you're on, with a play and pause button. Small and medium sizes for the Home Screen, and a Lock Screen size too.
- **Up Next** shows the episodes waiting in your Up Next queue. Small and medium sizes.
- **Watchlist** shows what's on your [watchlist](watchlist.md) and ready to watch. Tap it to open your watchlist, or, in the medium size, a poster to open that title. Small and medium sizes. Android has it too.

On iPhone and iPad the widgets show what the app last saved, and update when your podcasts or watchlist change in the app; they don't fetch anything from your server themselves. Android's Watchlist widget checks your server itself, every hour and when your watchlist changes. Signing out, or switching to another profile, empties it. See [Podcasts](podcasts.md) for Up Next.

## Top Shelf (Apple TV)

Put Flexarr in the top row of the Apple TV's Home screen, and the Top Shelf above it shows your Continue Watching. Select a title to open it in Flexarr, or press Play on the remote to carry on watching straight away. With nothing on the go, it shows featured titles instead.

To choose how it looks:

1. In Flexarr on the Apple TV, open **Settings › Home screen**.
2. Choose **Posters**, which fit more titles in, or **Wide artwork**, with each title's logo on it.

Each title shows how far through it you are. The Top Shelf shows the Continue Watching of the profile currently chosen on the Apple TV.

> **Tip**
> To move Flexarr to the top row, highlight its icon on the Apple TV's Home screen, press and hold until it wobbles, move it up, then press the remote's centre to drop it.
