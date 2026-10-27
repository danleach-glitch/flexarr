# iPhone and iPad notifications

Flexarr tells a phone when something new is ready: a film, a show or new episodes, a podcast episode, a finished
recording, or a request coming in. It can also send a morning list of what's airing that day (off by default).

## How a notification travels

```
Flexarr server ──(encrypted payload)──▶ relay (flexarr.io/api/push) ──▶ Apple Push Notification service ──▶ phone
                                                                                                         │
                                                         the app's notification extension decrypts it ◀───┘
```

- Apple only takes pushes signed with the App Store app's own key, which can't be handed to every server, so the
  servers send their pushes through a small relay that holds the key (`site/api/push.js`, deployed with flexarr.io).
- The relay never sees what a notification says. When notifications are turned on, the phone makes a random
  256-bit key and gives it to its own server. The server encrypts each notification with it (AES-256-GCM). The relay
  forwards only the encrypted payload, under a generic alert ("Something new is ready"), and the extension on the
  phone decrypts it and fills in the real title, text, artwork and buttons.
- A server can use a different relay (`Flexarr:Notifications:RelayUrl`) or none
  (`Flexarr:Notifications:Enabled=false`).

## Whose notifications

Each phone gets notifications for one profile: whichever profile it's on when notifications are turned on (Settings ›
Notifications on the phone). A household sharing profiles on one phone gets one set, not one per profile. Switching
to another profile doesn't change it; the Settings screen offers to switch notifications to the current profile.

Signing the phone out (or revoking it from the web) stops its notifications.

## What counts as new

- **Films and episodes** newly in the library that the profile hasn't watched, in libraries the profile can see.
  Upgrades and replacements don't count: the scanner keeps the same item when a file is swapped for a better one,
  and a title whose old copy is still remembered (missing, or kept as a watch-history tombstone) is skipped.
  A brand-new show says so ("New show: …"); several episodes of one show arriving together become one notification.
  A library's first scan never notifies, and a flood (more than a handful at once) is summed up in one notification.
- **Podcast episodes** of shows the profile follows, published after it followed them.
- **Recordings** that finished, sent to whoever scheduled them (or everyone, if nobody's recorded as scheduling it).
- **Requests** made from Flexarr, sent to the profile that asked, when the film or the show's first episode arrives.
  That replaces the ordinary "new" notification for it.
- **Upcoming** (off by default): once a day, at the hour chosen, what's airing or released that day for shows the
  profile watches and titles it requested.

## Server API (`/api/v1/notifications`)

All calls are made by the phone with its own sign-in. The device is the sign-in.

`PUT /device` registers or updates this phone:

```json
{
  "token": "<APNs device token, hex>",
  "environment": "production",          // or "sandbox" for development builds
  "key": "<base64 of 32 random bytes>", // the phone keeps it in the shared keychain
  "timeZone": "Europe/London",
  "films": true, "shows": true, "podcasts": true, "recordings": true, "requests": true,
  "upcoming": false, "upcomingHour": 9
}
```

A new registration is for the profile the request is made as. After that the phone keeps its profile, whoever is
watching on it (a phone's sign-in follows the profile last chosen on it), unless the request says
`"useCurrentProfile": true`: the app sends that when notifications are turned on and when someone taps "Get
notifications for … instead". Returns the device, as `GET` does.

`GET /device` returns `{ profileId, profileName, films, shows, podcasts, recordings, requests, upcoming,
upcomingHour, timeZone, relay }` (no key, no token), or 404 when this phone hasn't registered. `relay` is false when
the server has notifications turned off.

`DELETE /device` stops this phone's notifications. `POST /device/test` sends a test notification now (502 with a
reason when the relay or Apple refuses it).

## The encrypted payload

Base64 of `nonce (12 bytes) | ciphertext | tag (16 bytes)` (CryptoKit's `AES.GCM.SealedBox.combined`), sealing UTF-8
JSON:

```json
{
  "t": "New show: Severance",                  // title
  "b": "Season 1 · 9 episodes",                // body
  "c": "flexarr.video",                        // category (decides the buttons)
  "g": "series:0196…",                         // thread, to group a show's notifications
  "i": "https://image.tmdb.org/t/p/w500/….jpg",// artwork, public URLs only (optional)
  "u": { "item": "0196…", "play": "0196…" }    // what tapping opens and what Play plays
}
```

Categories and their buttons:

| Category | Buttons | `u` keys |
|---|---|---|
| `flexarr.video` | Play, Download, Play on Apple TV | `item` (film or show to open), `play` (film or episode to play) |
| `flexarr.podcast` | Play, Download | `podcast`, `episode` |
| `flexarr.recording` | Play | `item`, `play` |
| `flexarr.upcoming` | none | `item` (optional) |
| `flexarr.test` | none | none |

## Relay API

`POST https://flexarr.io/api/push`

```json
{ "token": "<hex>", "environment": "production", "payload": "<base64>", "collapseId": "<≤64 chars, optional>" }
```

The relay sends Apple:

```json
{ "aps": { "alert": { "title": "Flexarr", "body": "Something new is ready." }, "sound": "default",
           "mutable-content": 1 }, "e": "<payload>" }
```

It answers `200 {"ok":true}`, `410 {"reason":"Unregistered"}` or `400 {"reason":"BadDeviceToken"}` (the server forgets
the phone), `413` for a sealed payload over 2,800 bytes (Apple takes 4 KB in all, and base64 adds a third), `429` when rate-limited, or `502` with Apple's reason.

The relay needs these environment variables on the Vercel project: `APNS_KEY` (the .p8 file's contents),
`APNS_KEY_ID`, `APNS_TEAM_ID` (`KZRA8PTJ5Z`) and `APNS_TOPIC` (`com.danleach.flexarr.ios`). Make the key in the Apple
Developer site under Certificates, Identifiers & Profiles › Keys, with Apple Push Notifications service enabled. The
app ID needs the Push Notifications capability.
