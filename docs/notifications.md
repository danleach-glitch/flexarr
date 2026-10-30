# Phone notifications

Flexarr tells a phone (an iPhone or iPad, or an Android phone or tablet) when something new is ready: a film, a show or new episodes, a podcast episode, a finished
recording, or a request coming in. It can also send a morning list of what's airing that day (off by default).

## How a notification travels

```
Flexarr server ──(encrypted payload)──▶ relay (push.flexarr.io) ──▶ Apple Push Notification service ──▶ iPhone
                                                                 └─▶ Firebase Cloud Messaging ─────────▶ Android
                                                                                                          │
                     the iPhone's notification extension, or the Android app, decrypts it ◀───────────────┘
```

- Apple only takes pushes signed with the App Store app's own key, and Google only takes them from the Firebase
  project's service account, neither of which can be handed to every server, so the servers send their pushes through
  a small relay that holds them (`relay/api/push.js`, the Vercel project flexarr-push at push.flexarr.io, deployed on
  its own when `relay/` changes).
- The relay never sees what a notification says. When notifications are turned on, the phone makes a random
  256-bit key and gives it to its own server. The server encrypts each notification with it (AES-256-GCM). The relay
  forwards only the encrypted payload, under a generic alert ("Something new is ready"), and the extension on the
  phone decrypts it and fills in the real title, text, artwork and buttons. To Android it goes as a data message
  with no alert at all: the app decrypts it and shows the notification itself.
- A server can use a different relay (`Flexarr:Notifications:RelayUrl`) or none
  (`Flexarr:Notifications:Enabled=false`).

## Whose notifications

Each phone gets notifications for one profile: whichever profile it's on when notifications are turned on (Settings ›
Notifications on the phone). A household sharing profiles on one phone gets one set, not one per profile. Switching
to another profile doesn't change it; the Settings screen offers to switch notifications to the current profile.

Signing the phone out (or revoking it from the web) stops its notifications. The phone remembers they were on, though, and turns them back on
(for whichever profile it's on) when it next signs in, unless they were turned off in its Settings. If iOS allows
Flexarr notifications but this server isn't sending any, Settings on the phone offers to turn them on.

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
  "platform": "apple",                  // or "android"; leaving it out means "apple"
  "token": "<APNs device token, hex>",  // on Android, the Firebase registration token, exactly as given
  "environment": "production",          // or "sandbox" for development builds (Apple only: Android sends "production")
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

Base64 of `nonce (12 bytes) | ciphertext | tag (16 bytes)` (CryptoKit's `AES.GCM.SealedBox.combined`; on Android,
`Cipher.getInstance("AES/GCM/NoPadding")` with a 128-bit tag, the nonce taken off the front), sealing UTF-8 JSON:

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
| `flexarr.pairing` | Allow, Don't allow (both need the phone unlocked) | `pairing` (the code of the device asking to connect) |

`flexarr.pairing` goes to every phone registered on the main profile, whatever news it's chosen, when a device asks
to connect (see `PairingAlerts`). Its buttons approve or decline that code with the phone's own sign-in, as the app
would; tapping it opens the app's "Allow?" prompt for that code, read from `GET /api/v1/pairing/waiting`. While the app
is open it hears the same on its command stream (`event: pairing` and `event: pairing-ended`) instead. A device still
showing its code keeps that code, so there's one notification per device. The code a device asks for by itself just
after "Don't allow" isn't announced: it's listed with `turnedAway: true`, and apps list it without asking. Apps from
0.56 send `stopsWhenDeclined: true` with `POST /pairing` and stop on `declined: true` from `GET /pairing/{id}` instead,
so every code they ask for is announced.

## Relay API

`POST https://push.flexarr.io/api/push`, with `Content-Type: application/json` (anything else gets `415`, so a web
page can't send through its visitors' browsers).

Servers from before 0.51.0 post to `https://www.flexarr.io/api/push`, which the site passes on to the relay.

```json
{ "token": "<hex>", "environment": "production", "payload": "<base64>", "collapseId": "<≤64 chars, optional>",
  "platform": "android" }
```

`platform` is left out for an iPhone (relays from before Android don't know it) and is `"android"` for an Android
phone, whose `token` is then a Firebase registration token (letters, digits, `-`, `_` and `:`) rather than hex.

The relay sends Apple:

```json
{ "aps": { "alert": { "title": "Flexarr", "body": "Something new is ready." }, "sound": "default",
           "mutable-content": 1 }, "e": "<payload>" }
```

and sends Google (FCM's HTTP v1 API, `projects/<project>/messages:send`) a high-priority data message:

```json
{ "message": { "token": "<token>", "data": { "e": "<payload>" },
               "android": { "priority": "HIGH", "collapse_key": "<collapseId>" } } }
```

Google's refusals come back in the same words as Apple's: `UNREGISTERED` as `410 Unregistered`, and
`INVALID_ARGUMENT` or `SENDER_ID_MISMATCH` (a token from another Firebase project) as `400 BadDeviceToken`.

It answers `200 {"ok":true}`, `410 {"reason":"Unregistered"}` or `400 {"reason":"BadDeviceToken"}` (the server forgets
the phone), `413` for a sealed payload over 2,800 bytes (Apple takes 4 KB in all, and base64 adds a third), `429` when rate-limited (12 a minute to one phone; 600 a minute from one address, as a backstop), or `502`
with Apple's reason.

### Live Activities

When the iPhone's remote is following a TV, the phone puts a Live Activity on its Lock Screen and gives the server the
activity's push token (`PUT /api/v1/devices/{tv}/activity`). As the TV pauses, seeks, changes speed or stops, the server
posts:

```json
{ "kind": "liveactivity", "token": "<hex>", "environment": "production", "event": "update",
  "state": { "paused": false, "position": 754.2, "at": 1793700000, "duration": 3120, "rate": 1 } }
```

`event` is `update` or `end`. The system reads Live Activity updates itself, so unlike notifications they can't be
sealed: they carry times and nothing else. The phone put the title and the TV's name on when it started the activity,
and the relay refuses a `state` with any other key (`400 BadState`). It sends Apple `apns-push-type: liveactivity` on the
topic `<APNS_TOPIC>.push-type.liveactivity`, with `aps.content-state` set to `state`, a `stale-date` for when it should
have ended (twenty minutes on for a pause), and for `end` a `dismissal-date` of now. The 12-a-minute limit per token
applies; the server sends only when the times change more than the activity's own clock shows.

The relay needs these environment variables on the flexarr-push Vercel project: `APNS_KEY` (the .p8 file's contents),
`APNS_KEY_ID`, `APNS_TEAM_ID` (the Apple developer team's ID) and `APNS_TOPIC` (the iPhone app's bundle ID).
For Android it needs `FCM_SERVICE_ACCOUNT`: the contents of a service account key (the JSON file) for the Firebase
project the Android app is built with, from the Firebase console under Project settings › Service accounts › Generate
new private key. Without it, Android pushes get `503 {"reason":"NotConfigured"}`.
`KV_REST_API_URL` and `KV_REST_API_TOKEN` are optional: with an Upstash Redis store connected (Vercel's marketplace
sets both), the per-phone limit is counted across every instance of the relay rather than by each one alone. Make the key in the Apple
Developer site under Certificates, Identifiers & Profiles › Keys, with Apple Push Notifications service enabled. The
app ID needs the Push Notifications capability.
