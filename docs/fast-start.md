# Fast start

Films, episodes and audiobooks should start as close to instantly as possible, at home and away. At home the wait is
mostly FFmpeg and the player's own buffering; away from home every request costs a round trip of 50–200 ms and the file
may be more than the connection carries. This page is the set of rules every app follows.

## What already happened before this

- The next episode's session is started during the credits (`prepare: true`) on the web, Apple and Android apps, and
  the Apple apps fetch the file's opening bytes for AetherEngine (`AetherEngine.prewarm`).
- Book files a player can't decode are converted a few files ahead of the listener.

## Start timings

Every start is timed and sent to the server's logs (Settings › Logs) as one Information entry, category `playback`,
message `Playback started`, with the steps in its properties:

| Property | Meaning |
| --- | --- |
| `kind` | `video` or `book` |
| `method` | `directPlay`, `transcode` (from `PlaybackInfo.method`) |
| `warmed` | `true` when Play took over a warm-up |
| `remote` | `true` when the app isn't on the server's network (best guess on each app) |
| `requestMs` | Play pressed → the server's start answer (0 when warmed) |
| `firstFrameMs` | Play pressed → the first picture (video) or first sound (books) |
| `playbackId` | the server's playback id, to line up with server logs |

The message stays the same for every start, so the property values carry the numbers.

## Getting ready ahead of Play (warm-ups)

**When.** After about 2 s on a film, episode or book page. On the Apple TV and Android TV, also when Play, or a Continue
watching card, has had focus for about 1 s. Nothing warms while the device is already playing something, for a
download, or offline.

**What.** The same start request Play would make, with `prepare: true`. The server starts the session (and, for a
stream it converts, FFmpeg at the start point). For a file the Apple engine plays itself, the app also prewarms its
opening bytes.

**Handing over.** Play takes over the warm-up only when everything matches: title, file, start (the resume point, or
Start over / a chosen place), audio track, subtitles, quality, shuffle. It's also not used once it's more than five
minutes old (the resume point may have moved). Otherwise the warm-up is stopped and Play starts the usual way.

**Dropping.** Leaving the page (or the focus moving on), going to the background, signing out and switching profile
stop the warm-up (best effort; the web uses a keepalive request).

**On the server.** A warm-up:
- is not *now playing*: it stays out of Activity, the device feeds, remotes, Home Assistant and playback events until
  its stream is first asked for or it reports progress;
- never takes a slot from a real stream: it doesn't count towards the transcode limit for a real start, and when a slot
  is needed, warm-ups are ended first, without telling anyone their stream was taken;
- ends after ten minutes if nobody uses it (long credits, a while on the page), rather than the twelve hours of an ordinary session.

## Converting sooner

- Every transcoded start asks FFmpeg for its opening segment as soon as the session exists, rather than when the
  player gets round to asking for it after the playlists.
- A converted stream that starts from the beginning cuts its first 6 seconds into three 2-second segments, then carries
  on in 6-second ones. Resuming mid-way keeps 6-second segments. Repackaged streams (the picture copied as it is) cut at
  the file's keyframes as before.

## Starting the players sooner

- Android: a `DefaultLoadControl` that starts playing with 1 s buffered (2.5 s by default), keeping the usual wait after
  a stall.
- Apple: AVPlayer starts without waiting to minimise stalling, then goes back to normal once playing; AetherEngine gets
  `progressiveSegmentDelivery`.
- Web: hls.js fetches the first segment while it reads the playlist.
- Books resume within a second of the saved place (the first seek only; seeks the listener makes stay exact).
- The apps make a request to `/health` when they open or come back to the front, so the first real request doesn't
  pay for setting the connection up.

## Away from home quality

A server setting, `Transcoding.AwayFromHome` (Settings › Streaming › *Away from home*): `Automatic` (the default) or
`Original`.

Apps keep a rolling estimate of how fast media arrives and send it as `measuredBitrate` (bits per second) on the start
request. When the setting is Automatic, the viewer is remote and the file's bitrate is more than three quarters of the
measured speed, the stream is capped at three quarters of it (never under 1.5 Mbps). Home viewing never changes.

Remote means the request didn't come from the home network: not loopback, and not a private address (10/8, 172.16/12,
192.168/16, 169.254/16, fc00::/7, fe80::/10). Tailscale addresses (100.64/10) count as remote. Behind a reverse proxy
this relies on X-Forwarded-For.

Older servers ignore `measuredBitrate`; older apps don't send it and stream as before.

## Books converted early

Books in someone's Continue listening whose files need converting for some players (anything that isn't MP3 or AAC)
have the file at the saved place, and the one after, converted in the background, so the first press doesn't wait for
a whole file to convert.
