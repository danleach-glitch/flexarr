---
title: Home Assistant
description: Add Flexarr to Home Assistant so every Apple TV, iPhone and iPad becomes a media player, and dim the lights when a film starts.
---

[Home Assistant](https://www.home-assistant.io) is a popular app for running your smart home yourself. Flexarr has an integration for it, so your home can react to what you're watching: lights down when a film starts, up again when the credits roll.

The integration lives in its own repository, [flexarr-home-assistant](https://github.com/danleach-glitch/flexarr-home-assistant), and needs Flexarr 0.45.0 or later.

## What you get

- **A media player for each device.** Every Apple TV, iPhone and iPad signed in to Flexarr appears in Home Assistant, named as it is in Flexarr's list of signed-in devices. It shows what's playing (title, show, season and episode, artwork and position) and has play, pause and stop. It's *idle* while Flexarr is open with nothing playing, and *off* when Flexarr isn't open on it. Devices you connect later appear by themselves.
- **Device triggers** for *started*, *paused*, *resumed*, *credits*, *ended* and *stopped*, to pick in an automation's Device trigger.
- **A `flexarr_playback` event** for every change, which covers browsers too.

## Install it

The easiest way is through [HACS](https://hacs.xyz), the community store for Home Assistant, which also tells you about updates.

1. In Home Assistant, open **HACS**, then the **⋮** menu, then **Custom repositories**.
2. Add `https://github.com/danleach-glitch/flexarr-home-assistant` with the type **Integration**.
3. Find **Flexarr** in HACS, download it, and restart Home Assistant.
4. Go to **Settings › Devices & services › Add integration** and choose **Flexarr**.
5. Enter the **Server address**, for example `http://192.168.1.20:8383`, and the **API key**.

The API key is in Flexarr under **Settings › Access**, signed in as the main profile. Showing it asks for the admin PIN.


> **Note**
> If you copied the integration into `custom_components` by hand before it had its own repository, it keeps working. Remove the old copy and install it through HACS to get updates.

## Play something from Home Assistant

Call `media_player.play_media` with a Flexarr title's id. You'll find the id in the title's address in the web app. A show's id plays the episode that device's profile is up to.

```yaml
action: media_player.play_media
target:
  entity_id: media_player.living_room_tv
data:
  media_content_id: 01a17ab1-0000-0000-0000-000000000000
  media_content_type: tvshow
```

As with Siri, Flexarr has to be open on the device for it to play.

## The playback event

`flexarr_playback` carries:

| Field | What it is |
| --- | --- |
| `type` | `started`, `paused`, `resumed`, `credits`, `ended` or `stopped` |
| `device_id` | the Home Assistant device (none for a browser) |
| `device_name` | the device or browser, as named in Flexarr |
| `profile` | whose profile is watching |
| `media_type` | `movie`, `episode`, `music` or `podcast` |
| `title`, `series_title`, `season`, `episode` | what's playing |
| `media_id` | its Flexarr id |
| `position`, `duration` | in seconds |

*Credits* fires where Flexarr has found the end credits, within about ten seconds of them starting. A title without credits found goes straight to *ended*. See [Subtitles and skipping](subtitles-and-skipping.md) for how credits are found.

A playback that stops reporting counts as stopped after two minutes, or fifteen if it was paused.

## Example: lights down for films

This dims the living room when a film starts on the living room TV, and brings the lights up at the credits or when you stop.

```yaml
automation:
  - alias: Lights down for a film
    triggers:
      - trigger: event
        event_type: flexarr_playback
        event_data:
          type: started
          media_type: movie
          device_name: Living room TV
    actions:
      - action: light.turn_on
        target: { entity_id: light.living_room }
        data: { brightness_pct: 10, transition: 5 }

  - alias: Lights up at the credits
    triggers:
      - trigger: event
        event_type: flexarr_playback
        event_data: { type: credits, device_name: Living room TV }
      - trigger: event
        event_type: flexarr_playback
        event_data: { type: stopped, device_name: Living room TV }
    actions:
      - action: light.turn_on
        target: { entity_id: light.living_room }
        data: { brightness_pct: 80, transition: 10 }
```

Change `Living room TV` to your device's name in Flexarr, and `light.living_room` to your own light.

> **Tip**
> Leave out `media_type: movie` to dim the lights for episodes too, or add a `profile` to make it only happen when you're the one watching.

## For other automation tools

The integration is built on Flexarr's event stream, which anything else can use too. `GET /api/v1/events` on your server is a stream of server-sent events: one `playback` event, as JSON, for each change, with a keep-alive comment every twenty seconds. Because it shows what everyone is watching, it needs the API key, sent as an `X-Api-Key` header.

```bash
curl -N -H "X-Api-Key: <your key>" http://<server>:8383/api/v1/events
```

> **Important**
> The API key gives full control of your server. Keep Home Assistant and anything else holding it on your home network or your tailnet. See [Watch away from home](away-from-home.md).
