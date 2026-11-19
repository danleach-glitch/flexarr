---
title: Share with family
description: Let someone watch your Flexarr from their own home. Their app shows a code, you type it in, and they're watching. No Tailscale, no account, no sign-in for them.
---

**Family invites** let someone watch your Flexarr from their own home, a grandparent say, without installing Tailscale, making an account or signing in. Their app shows a code, you type it in, and their app connects.

Their app has Tailscale built in and joins your tailnet as a **Flexarr guest**. A guest can reach Flexarr and nothing else on your network, and only as their own profile. Your server stays off the public internet.

> **Note**
> Family invites need Tailscale on the server, as in [Watch away from home](away-from-home.md). Flexarr in Docker and the Mac app can't do them yet.

## Set it up, once

In Flexarr on the web, signed in as the main profile, go to **Settings › Remote access**. **Tailscale setup** has three steps:

1. **Paste this into your tailnet policy.** Flexarr shows a few lines with your server's tailnet address filled in. Copy them into your tailnet policy in Tailscale's admin console (**Access controls**). They create the `tag:flexarr-guest` tag and let guests reach Flexarr's port on your server, and nothing else.
2. **Create an OAuth client.** In Tailscale's admin console, under **Settings › Trust credentials**, make an OAuth client with two scopes: **Auth Keys** (write, with the tag `tag:flexarr-guest`) and **Policy File** (read). Tailscale adds two **Devices** read scopes itself when you pick Policy File; that's fine. Nothing else: Flexarr refuses a client that can do more.
3. **Paste its ID and secret** into Flexarr and choose **Save**.

Flexarr then checks your policy. When it's done, it says **Devices that join with a code can reach Flexarr and nothing else**, and the steps fold away.

> **Important**
> A new tailnet lets every device reach every other one, and that rule would let guests in everywhere too. Flexarr won't add guests until your policy keeps them to Flexarr. If it says guests could reach more, it names the rules: change a rule whose source is `"*"` to `"autogroup:member"`, so your own devices keep their access and guests don't get it.

Flexarr checks your policy again every hour. If it's ever changed so guests could reach more, adding guests pauses and your phone is told. Guests who've already joined keep whatever the policy now allows them, so fix it straight away.

### Why it's safe

- Flexarr's OAuth client can only make guest keys and read your policy. It can't change your policy or touch your devices.
- Each guest key works once, is tagged as a guest, and is deleted once used.
- Flexarr reads your policy's rules as well as testing it, so a rule that lets guests into another device, your home network or an exit node is caught.
- A guest's device is refused everything on the server unless it has its own Flexarr sign-in, and that sign-in only works from that device.
- Guests can't switch profiles, change settings, use child lock, or see or control your household's TVs and phones.

## Someone who lives with you

Someone in your household (a partner, a teenager) shouldn't have to be a guest to watch on their iPad on the train. Add their device under **Your household** instead, above **Guests** in **Settings › Remote access**. It works like a guest's, with Tailscale built into the app and nothing for them to set up, but the device watches as **their own profile**, with what they've already watched, and can do everything a device at home can: switch to another of your household's profiles, see and control your TVs, find subtitles.

1. **Your household** lists everyone with a profile except the main one. Not there? Add them in [Profiles](profiles.md) first.
2. Under their name, choose **+ Add device**.
3. On their device, in Flexarr, they go to **Settings › Server** and choose **Use away from home**. Not signed in on it yet? They choose **Join someone’s Flexarr** instead. It shows a code like `H42K-FM5W`.
4. They read it to you, you type it in and choose **Connect**. Their app asks **Join Dan’s Flexarr?** and they choose **Join**.

That's it: from then on the app connects by itself, at home or away. **Sign out** beside a device (the arrow leaving a door) signs it out, and deletes its downloads; their profile and what they've watched stay. If you type someone who lives with you under **Add new guest**, Flexarr offers to add their device under **Your household** instead.

The main profile isn't in **Your household**: away from home, it uses Tailscale, so the server's settings stay out of reach of a lost phone. AirPlay, background downloads and Top Shelf don't work over the built-in connection, for household devices as for guests.

## Add a guest

Under **Guests**, choose **Add new guest** and type their name ("Dad"). Flexarr makes them a profile of their own, which they watch as and can't leave. Each guest's card says which libraries they see: choose **Change** to pick them, or set an age limit, in their profile in [Profiles](profiles.md). Names are unique: if someone already has the name, Flexarr offers to add a device for them instead.

Then add their device:

1. On their iPhone, iPad, Apple TV, Android phone or Android TV, they install Flexarr, open it and choose **Join someone’s Flexarr**. It shows a code like `H42K-FM5W`.
2. They read it to you over the phone, or send it.
3. You type it in and choose **Connect**. Their app asks **Join Dan’s Flexarr?** and they choose **Join**.

A code changes every 10 minutes, so use the one on their screen now. For someone with a phone and a TV, choose **+ Add device** under their name and do it again on the other. When a device joins, your phone tells you, so one you didn't expect shows up straight away.

## Guests

Each guest has a card with their devices and how each is connected:

- **Direct:** their device and your server connect straight to each other. The best.
- **Through your relay:** the faster relay below is carrying it.
- **Slow relay:** their network and yours can't connect directly, so the connection goes through Tailscale's shared relays, which slow to about 2 Mbps. Flexarr lowers their quality to fit, so it plays rather than buffers.

The bin beside a device signs it out, and the server refuses everything from it from then on. Its app goes back to **Join someone’s Flexarr**. The bin beside a guest's name removes them: their devices, their profile and what they've watched. A device stays listed in Tailscale until you delete it there, under **Machines**. Your free Tailscale plan has room for 50 guest devices.

If your server's tailnet address or Flexarr's port changes (Tailscale reinstalled, say), guests' devices can't reach it any more, and their rows say so: remove each and add it again.

## Faster relay

In **Tailscale setup** (choose **Show**), turn on **Faster relay** to make your server relay its own guests' connections, instead of Tailscale's shared relays. Then:

1. On your router, forward the UDP port it shows to the server.
2. Update your tailnet policy with the lines in setup, which now include the relay.

## For guests

- **Leave** is in **Settings** in the app. Joining again takes a new code.
- If the app can't reach your Flexarr, it says so, with **Try again** and **Leave**. Downloads on an iPhone, iPad or Android phone still play from there.
- **AirPlay** and **Chromecast** aren't offered: those devices can't reach the private connection inside the guest's phone.
- **Downloads** on iPhone and iPad carry on only while Flexarr is open.
- **Top Shelf** on a guest's Apple TV stays empty.
- **In a browser**, guests can't join. Browsers still need Tailscale, as in [Watch away from home](away-from-home.md).
