---
title: Siri and Shortcuts
description: Ask Siri on your iPhone or iPad to play a film, the next episode of a show or a podcast, here or on your Apple TV, and build your own shortcuts.
---

Once the Flexarr app on your iPhone or iPad has signed in to your server, Siri understands it. There's nothing to set up. Siri learns the titles in your library when the app signs in, so it knows what you mean by "*The Traitors*" or "*Heat*".

Every phrase needs the word **Flexarr** in it, so Siri knows which app you mean.

## Things to say

### Films and shows

- "Play the next episode of *Curb Your Enthusiasm* in Flexarr"
- "Play *Heat* in Flexarr" or "Watch *Heat* in Flexarr"
- "Shuffle *Family Guy* in Flexarr"
- "Continue watching in Flexarr"

A show plays the episode you're up to. A film carries on from where you left off. Shuffle plays a show's episodes in a random order, one after another. Continue watching picks up whatever you were in the middle of most recently.

### Podcasts

- "Play *The Rest Is History* in Flexarr" plays the episode you'd hear next
- "Play the latest *The Rest Is History* in Flexarr" plays the newest one you haven't heard
- "What's new in my podcasts in Flexarr" reads out the newest episodes, without opening anything
- "Add *The Rest Is History* to Up Next in Flexarr"
- "Play my Up Next in Flexarr"

These work for podcasts the profile follows. See [Podcasts](podcasts.md).

### Controlling what's playing

- "Pause Flexarr"
- "Resume Flexarr"
- "Stop Flexarr"
- "Turn on night mode in Flexarr" (and "Turn off night mode in Flexarr")

## Playing on your Apple TV

By default, what you ask for plays on the iPhone or iPad you asked. To send it to an Apple TV instead:

1. On the iPhone or iPad, open Flexarr and go to **Settings › Siri**.
2. Under **Siri plays on**, choose the Apple TV.


Now "Play the next episode of *Severance* in Flexarr" starts it on the TV, and Siri tells you where it's playing. Pause, resume and stop go to the same TV.

You can also name a device as you ask, for example "… on the living room TV". The name is the one the device has in Flexarr's list of signed-in devices.

> **Important**
> The Apple TV only takes commands while Flexarr is open on it. If it isn't, Siri says so, and what you asked for starts if you open Flexarr on the TV within two minutes.

Siri isn't limited to Apple TVs: any iPhone or iPad signed in to your server can be chosen in the same way, as long as Flexarr is open on it.

### Siri on the Apple TV itself

The Apple TV app carries the same actions for the Siri Remote, but it hasn't been confirmed that tvOS passes these requests to apps like Flexarr. For now, the dependable way is to ask your iPhone or iPad.

## The Shortcuts app

Everything Siri can do is also in the Shortcuts app on iPhone and iPad, as actions you can put together yourself or run from an automation. Search for Flexarr when adding an action. You'll find:

| Action | What it does |
| --- | --- |
| **Play a film or show** | Plays a film, or the next episode of a show. Can start from the beginning. |
| **Shuffle a show** | Plays a show's episodes in a random order. |
| **Continue watching** | Picks up what you were in the middle of. |
| **Pause, resume or stop** | Controls what Flexarr is playing. |
| **Night mode** | Turns night mode on or off, here or on another device. |
| **Dialogue boost** | Sets dialogue boost to off, low, medium or high, here or on another device. |
| **Play a podcast's latest episode** | The newest episode you haven't heard. |
| **What's new in my podcasts** | Says the newest episodes you haven't heard. |
| **Add a podcast to Up Next** | Puts the next episode at the end of Up Next. |
| **Play Up Next** | Plays your podcast queue from the start. |
| **Search Flexarr** | Opens the app on a search. |

The play and control actions have an **On** setting, where you choose this device or another one signed in to your server. That's handy for a shortcut of your own. A "Film night" shortcut could use Apple's own actions to dim the lights and wake the Apple TV, open Flexarr on it, then use **Play a film or show** with **On** set to that TV.

**Play a film or show** and **Shuffle a show** also have an **As** setting, for the profile to watch as. The device switches to that profile first, so the next episode is that person's and what they watch lands in their Continue Watching, whoever used the TV last. A "Kids' bedtime episode" shortcut can play their show on the TV as their profile. If the profile has a PIN, the device asks for it before playing.

> **Tip**
> A shortcut you make can have a phrase of its own. Name it, and Siri runs it when you say that name.

## Links

Flexarr's links open the app on the iPhone, iPad or Apple TV they're opened on. Use them with Shortcuts' **Open URLs** action, an NFC tag or Home Assistant:

| Link | What it does |
| --- | --- |
| `flexarr://item/<id>` | Shows a title's page. |
| `flexarr://play/<id>` | Plays a film or episode, or a show's next episode. Add `?t=<seconds>` to start at a time, or `?shuffle=1` to shuffle a show. |

Add `profile=<name>` (or the profile's id) to either to switch to that profile first, for example `flexarr://play/<id>?profile=Sam`. If the profile has a PIN, Flexarr asks for it before going on. A title's id is the last part of its address on the web, after `/item/`.

## If Siri doesn't understand

- **"Open Flexarr and connect it to your server first."** The app on this device isn't signed in. Open it and connect. See [Connect your devices](connect-devices.md).
- **"Nothing in your library is called …"** Siri didn't recognise the title. Try saying it the way it's written in Flexarr. Siri learns titles when the app loads your profile, so open Flexarr once after adding something new.
- **"… isn't signed in to the server any more."** The device chosen in **Settings › Siri** was signed out. Choose another.
- **Siri opens a different app.** Make sure "Flexarr" is in the phrase.

Siri works on what the profile chosen on that iPhone or iPad sees: its libraries, its next episodes and its podcasts. See [Profiles](profiles.md).
