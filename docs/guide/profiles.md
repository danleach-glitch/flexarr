---
title: Profiles
description: Give everyone in the house their own Continue Watching, history and settings, with an optional PIN, and choose which libraries each profile sees.
---

A profile is one person's view of Flexarr. Everyone in the household can have their own, so your half-watched film doesn't turn up in someone else's Continue Watching, and their cartoons don't fill your "Because you watched" row.

## What each profile keeps to itself

Each profile has its own:

- **Continue Watching**, watched history and Up Next
- **Playback settings**: auto-play the next episode, skip intros, recaps and credits, and subtitle size. These follow the profile to every device, the Apple TV included.
- **Podcasts**: the shows it follows, its Up Next queue and its podcast settings
- **Music**: playlists, daily mixes, and recently and most played
- **Collections** of its own (the main profile can also make collections for the whole household)
- **Live TV favourites** and the channel it watched last
- **Libraries**: which ones it can see at all (see below)
- A picture, a name and a colour

Things that belong to the server, such as your libraries, connections to Sonarr, Radarr and Lidarr, and the layout of Home, are the same for everyone.

## The main profile

The first profile, made when you set Flexarr up, is the **main profile**. It manages the server, and it has a crown beside its name in **Settings › Profiles** and on its picture when you choose who's watching.

Only the main profile can:

- change the server's settings, libraries and connections
- add, edit and remove profiles
- connect new devices, and see or replace the API key
- fix a wrong match, or delete a title through Sonarr or Radarr

Every other profile can browse, watch, listen, make requests through Seerr, and open **Settings** to change its own playback and podcast settings. Under **Settings › Server** they see **Manage the server**, which asks for the main profile.

> **Important**
> Give the main profile a PIN. Without one, anyone using a browser or device you've signed in can switch to it and change settings.

## Adding a profile

On the web, signed in as the main profile:

1. Go to **Settings › Profiles** and choose **Add a profile**.
2. Give it a name, and if you like a picture and a colour.
3. Choose **Add a PIN** if it should be private.
4. Untick any libraries it shouldn't see.
5. Choose **Add profile**.

<!-- screenshot: Settings › Profiles on the web, with the edit form open showing picture, name, colour, PIN and the library switches -->

If you bring your history over from Plex, your Plex Home profile pictures can come too. See [Import from Plex](import-from-plex.md).

## PINs

A PIN is 4 to 8 digits. Without one, anyone can switch to that profile. With one, Flexarr asks for it whenever someone chooses the profile.

- To change or remove the main profile's PIN, you need its current PIN. If you've forgotten it, choose **Use the API key instead**; the key is in `config.json` in the server's data folder.
- Wrong guesses lock out the device doing the guessing for a while, longer each time. Someone else guessing can't lock you out of your own profile.
- The main profile's PIN also unlocks Settings when you're away from home over Tailscale. See [Watch away from home](away-from-home.md).

## Choosing which libraries a profile sees

When you have more than one library, each profile has a switch for each. A library that's switched off is hidden everywhere on that profile: Home, search, Discover, Coming soon and requests. Podcasts can be left out the same way.

### Profiles for children

Flexarr doesn't have age ratings or parental controls. What it does have is library visibility, which goes a long way:

1. Keep children's films and shows in their own folders, and add them as their own libraries (for example "Kids' Films" and "Kids' TV").
2. Give the children a profile that sees only those libraries.
3. Give your own profile, and the main profile, a PIN so they can't switch to them.

> **Note**
> Hiding libraries keeps titles off the profile in Flexarr. It doesn't stop anyone requesting new titles through Seerr from that profile, though anything they request still lands in whichever library Sonarr or Radarr puts it in.

## Who's watching when Flexarr opens

When a household has more than one profile, Flexarr asks who's watching the first time a browser or device connects, and remembers the answer.

### On the web

The page is called **Choose a profile**. Profiles with a PIN say **Needs a PIN**. To switch later, open the menu under your profile picture at the top right and choose **Switch profile**. With two profiles, it says **Switch to** and the other person's name, and switches straight away unless that profile has a PIN. A non-main profile also has **Switch to the main profile** in Settings.

### On iPhone and iPad

Tap your picture on Home, or go to **Settings** and tap **Switch** beside your profile.

### On Apple TV

Who's watching is at the top of the sidebar, with your profile picture. Select it to switch. A profile with a PIN asks for it on an on-screen keypad.

An Apple TV in a shared living room can ask every time Flexarr opens, rather than opening as whoever watched last:

1. Open **Settings › Profiles** on the Apple TV.
2. Under **When Flexarr opens**, choose **Ask who's watching**.

Profiles with a PIN then ask for it every time too.

## Notifications and profiles

An iPhone or iPad gets notifications for one profile: whichever it was on when notifications were turned on. Switching profile on the phone doesn't change that, though **Settings › Notifications** offers to move them to the profile you're on now. See [Notifications and widgets](notifications-and-widgets.md).
