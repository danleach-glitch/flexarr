---
title: Share with family
description: Invite someone to watch your Flexarr from their own home with a link or a TV code. They need no Tailscale, no account and no sign-in.
---

**Family invites** let someone watch your Flexarr from their own home, a grandparent say, without installing Tailscale, making an account or signing in. You send their phone a link, or type in the code their TV shows, and their app connects.

Their app has Tailscale built in and joins your tailnet as a **Flexarr guest**. A guest can reach Flexarr and nothing else on your network, and only as the profile you chose for them. Your server stays off the public internet.

> **Note**
> Family invites need Tailscale on the server, as in [Watch away from home](away-from-home.md). Flexarr in Docker and the Mac app can't do them yet.

## Set it up, once

In Flexarr on the web, signed in as the main profile, go to **Settings › Access**. **Family invites** walks you through three steps, and checks each one.

1. **Paste the policy.** Flexarr shows a few lines with your server's tailnet address filled in. Copy them into your tailnet policy in Tailscale's admin console (**Access controls**). They create the `tag:flexarr-guest` tag and let guests reach Flexarr's port on your server, and nothing else.
2. **Create an OAuth client.** In Tailscale's admin console, under **Settings › Trust credentials**, make an OAuth client with two scopes: **Auth Keys** (write, with the tag `tag:flexarr-guest`) and **Policy File** (read). Nothing else.
3. **Paste its ID and secret** into Flexarr and choose **Save**.

When it's done, Flexarr says **Guests can reach Flexarr and nothing else**.

> **Important**
> A new tailnet lets every device reach every other one, and that rule would let guests in everywhere too. Flexarr won't make invites until your policy keeps guests to Flexarr. If it says guests could reach more, find the rule whose source is `"*"` and change it to `"autogroup:member"`: your own devices keep their access, and guests don't get it.

Flexarr checks your policy again every hour. If it's ever changed so guests could reach more, invites stop working and your phone is told.

### Why it's safe

- Flexarr's OAuth client can only make guest keys and read your policy. It can't change your policy or touch your devices.
- Each guest key works once, is tagged as a guest, and runs out with its invite.
- A guest's device is refused everything on the server unless it has its own Flexarr sign-in, and that sign-in only works from that device.
- Guests can't switch profiles, change settings, use child lock, or see or control your household's TVs and phones.

## Invite someone

Under **Guests** in **Settings › Access**, choose **Add new guest** and type their name ("Dad"). Flexarr makes them a profile of their own, which they watch as and can't leave. You can set its age limit or hidden libraries later in [Profiles](profiles.md). Someone who already has a profile, type their name and it's used.

1. On their phone, tablet or Apple TV, they install Flexarr, open it and choose **Invited by someone?**. It shows a code like `H42K-FM5W`.
2. They read it to you over the phone, or send it.
3. You type it in and choose **Connect**. Their device joins as their profile.

Each device joins this way, so for someone with a phone and a TV, do it on each. Rather send a link? Choose **Or send them a link instead**: message them the link, or scan its QR code with their phone's camera. Tapping it opens Flexarr, which asks **Join Dan's Flexarr?**.

An invite works once and lasts 3 days. Invites not used yet show under the person, with **Cancel**. When a device joins, your phone tells you, so one you didn't expect shows up straight away.

## Guests

Each guest's device is listed under **Guests**, with how it's connected:

- **Direct:** their device and your server connect straight to each other. The best.
- **Through your relay:** the faster relay below is carrying it.
- **Slow relay:** their network and yours can't connect directly, so the connection goes through Tailscale's shared relays, which slow to about 2 Mbps. Flexarr lowers their quality to fit, so it plays rather than buffers.

**Remove** signs the device out, and the server refuses everything from it from then on. It's still listed in Tailscale until you delete it there, under **Machines**. Your free Tailscale plan has room for 50 guest devices.

## Faster relay

Turn on **Faster relay** to make your server relay its own guests' connections, instead of Tailscale's shared relays. Then:

1. On your router, forward the UDP port it shows (40000 unless you choose another) to the server.
2. Update your tailnet policy with the lines in setup, which now include the relay.

## What guests can't do

- **AirPlay** and **Chromecast** aren't offered: those devices can't reach the private connection inside the guest's phone.
- **Downloads** on iPhone and iPad carry on only while Flexarr is open.
- **Top Shelf** on a guest's Apple TV stays empty.
- **In a browser**, guests can't use an invite. Browsers still need Tailscale, as in [Watch away from home](away-from-home.md).
