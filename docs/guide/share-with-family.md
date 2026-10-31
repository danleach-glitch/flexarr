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
2. **Create an OAuth client.** In Tailscale's admin console, under **Settings › Trust credentials**, make an OAuth client with two scopes: **Auth Keys** (write, with the tag `tag:flexarr-guest`) and **Policy File** (read). Nothing else: Flexarr refuses a client that can do more.
3. **Paste its ID and secret** into Flexarr and choose **Save**.

Flexarr then checks your policy. When it's done, it says **Guests can reach Flexarr and nothing else**, and the steps fold away.

> **Important**
> A new tailnet lets every device reach every other one, and that rule would let guests in everywhere too. Flexarr won't add guests until your policy keeps them to Flexarr. If it says guests could reach more, it names the rules: change a rule whose source is `"*"` to `"autogroup:member"`, so your own devices keep their access and guests don't get it.

Flexarr checks your policy again every hour. If it's ever changed so guests could reach more, adding guests pauses and your phone is told. Guests who've already joined keep whatever the policy now allows them, so fix it straight away.

### Why it's safe

- Flexarr's OAuth client can only make guest keys and read your policy. It can't change your policy or touch your devices.
- Each guest key works once, is tagged as a guest, and is deleted once used.
- Flexarr reads your policy's rules as well as testing it, so a rule that lets guests into another device, your home network or an exit node is caught.
- A guest's device is refused everything on the server unless it has its own Flexarr sign-in, and that sign-in only works from that device.
- Guests can't switch profiles, change settings, use child lock, or see or control your household's TVs and phones.

## Add a guest

Under **Guests**, choose **Add new guest** and type their name ("Dad"). Flexarr makes them a profile of their own, which they watch as and can't leave. You can set its age limit or hidden libraries later in [Profiles](profiles.md). Names are unique: if someone already has the name, Flexarr offers to add a device for them instead.

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
