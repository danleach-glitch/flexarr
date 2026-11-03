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
message `Playback <playbackId> started` (the id keeps each start its own entry), with the steps in its properties:

| Property | Meaning |
| --- | --- |
| `kind` | `video` or `book` |
| `method` | `directPlay`, `transcode` (from `PlaybackInfo.method`) |
| `warmed` | `true` when Play took over a warm-up |
| `remote` | `true` when the app isn't on the server's network (best guess on each app) |
| `requestMs` | Play pressed → the server's start answer (0 when warmed) |
| `firstFrameMs` | Play pressed → the first picture (video) or first sound (books) |
| `playbackId` | the server's playback id, to line up with server logs |

The numbers are in the properties, not the message. A stall within 30 seconds of the first picture (not a seek) gets
one more entry, `Playback <playbackId> stalled soon after starting`, with `afterMs`.

## Getting ready ahead of Play (warm-ups)

**When.** After about 2 s on a film, episode or book page. On the Apple TV and Android TV, also when Play, or a Continue
watching card, has had focus for about 1 s. Nothing warms while the device is already playing something, for a
download, offline, or on a server before 0.59 (which would show the warm-up as playing and give it a transcode slot).

**What.** The same start request Play would make, with `prepare: true`. The server starts the session (and, for a
stream it converts, FFmpeg at the start point, with a 30-second burst rather than three minutes). For a file the Apple
engine plays itself, a detail page also prewarms its opening bytes (not a focused card: scrolling would download the
start of every file).

**Handing over.** Play takes over the warm-up only when everything matches: title, file, start (the resume point, or
Start over / a chosen place), audio track, subtitles, quality, shuffle, and the profile that made it. It's also not used
once it's more than two minutes old (the resume point may have moved). Otherwise the warm-up is stopped and Play starts
the usual way. The next episode got ready during the credits is used for up to half an hour.

**Dropping.** Leaving the page (or the focus moving on), going to the background, signing out and switching profile
stop the warm-up (best effort; the web uses a keepalive request).

**On the server.** A warm-up:
- is not *now playing*: it stays out of the device feeds, remotes, Home Assistant and playback events until its stream
  is first asked for (a playlist or segment, or a file still being read 30 seconds after it was got ready) or it reports
  progress. Settings › Overview lists one that's converting as *Getting ready to play*;
- puts off previews, intro detection and subtitle extraction until it's used, and its log line names no title until
  then;
- never takes a slot from a real stream: it doesn't count as busy for Live TV or download preparation, and when a slot
  is needed (a start, or a viewer back from a long pause) warm-ups are ended, with their playback, oldest first, without
  telling anyone. A player that takes one up after all finds it gone and starts afresh;
- one profile keeps two at most, a newer one replacing the oldest;
- lasts as long as any session (apps already out reuse a next episode prepared in the credits); its FFmpeg stops when
  idle, as any does.

## Converting sooner

- Every transcoded start asks FFmpeg for its opening segment as soon as the session exists, rather than when the
  player gets round to asking for it after the playlists.
- A converted stream that starts from the beginning cuts its first 6 seconds into three 2-second segments, then carries
  on in 6-second ones. Resuming mid-way keeps 6-second segments. Repackaged streams (the picture copied as it is) cut at
  the file's keyframes as before. Only encoders that keyframe where told get the short opening (libx264 and NVENC with
  scene cuts off, VideoToolbox); Quick Sync and VA-API may add their own, which would split segments off the playlist.

## Starting the players sooner

- Android: a `DefaultLoadControl` that starts playing with 1 s buffered (2.5 s by default), and waits 2 s after a stall
  (5 s by default).
- Apple: AVPlayer starts without waiting to minimise stalling, then goes back to normal once playing, and counts as
  playing once its picture is ready to show; AetherEngine gets `progressiveSegmentDelivery`.
- Web: hls.js fetches the first segment while it reads the playlist.
- Books resume within a second of the saved place (resuming only; chapters, bookmarks and seeks stay exact).
- The apps make a request to `/health` when they open or come back to the front, so the first real request doesn't
  pay for setting the connection up.

## Away from home quality

A server setting, `Transcoding.AwayFromHome` (Settings › Streaming › *Away from home*): `Automatic` (the default) or
`Original`.

Apps measure how fast stream segments arrive (not artwork: small files loaded side by side read far below the link, and
not audio) and send the upper quartile of recent samples as `measuredBitrate` (bits per second) on the start request:
the web from its segments' resource timing (excluding the wait for the first byte), Apple from AVPlayer's observed
bitrate, Android from the video player's segment loads. Too few samples, and nothing is sent.

When the setting is Automatic, the viewer is remote, the request names no quality of its own (the web player's menu
wins) and the file's known bitrate is more than three quarters of the measured speed, the stream is capped at three
quarters of it (never under 1.5 Mbps). The reason given is the connection. If converting needs a slot the server
doesn't have, and the cap was the only reason, the file plays as it is. Home viewing never changes. Audiobooks and
podcasts are below the floor, so they always play as they are.

Remote means anything the server's network rules (`NetworkZones`) don't count as the home network: Tailscale and
relayed requests (Cloudflare Tunnel, Funnel, untrusted proxies) included.

Older servers ignore `measuredBitrate`; older apps don't send it and stream as before.

## Books converted early

Books in someone's Continue listening whose files need converting for some players (anything that isn't MP3 or AAC)
have the file at the saved place, and the one after, converted in the background, so the first press doesn't wait for
a whole file to convert: two files a pass, the most recently played books first, never while anything plays.
