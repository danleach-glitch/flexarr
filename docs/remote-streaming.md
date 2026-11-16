# Remote streaming

Away from home, a film or episode should start at once and never keep buffering, the way a YouTube video does: start
on a quality the connection surely carries, then move up or down by itself. This builds on *Away from home quality*
in [fast-start.md](fast-start.md).

## Why it buffered

The first play away from home had no speed reading (the apps kept it in memory, and measured only when the player
closed), so the server sent the original file. A stream had one quality, so the player couldn't move down, and
buffering changed nothing: the apps only logged it. The apps had no Quality menu.

## The speed

- **Speed test.** `GET /api/v1/speedtest?bytes=<n>` sends `n` incompressible bytes (default 2 MB, at most 4 MiB) to
  any signed-in device, with `Cache-Control: no-store` and no compression. The apps run it when they open or come to
  the front away from home, at most every 10 minutes or after a network change: 4 MB on Wi-Fi, 1 MB on mobile data,
  timed from the first byte to the last. The title page runs it too when there's no fresh reading. Servers before 0.67
  answer 404, and the apps carry on without it.
- **Readings while playing.** The players note the rate segments arrive at while they play (AVPlayer's access log,
  ExoPlayer's loads, the web's resource timing), not only when they close. The iPad's own engine, which downloads for
  itself, gives *(buffer seconds gained ÷ seconds taken) × the file's bitrate* while its buffer fills.
- **Kept.** Readings are saved per server and per address in use (home, Tailscale, guest): fresh for 30 minutes, then
  a starting point for 24 hours. The median goes as `measuredBitrate` on every start, reopen, recovery and warm-up.

*Away* is the address the app is using: the server's away address (Tailscale or a relay) or a guest's tunnel, not its
home one.

## What the server sends away from home

With *Away from home* on Automatic (Settings › Streaming) and no quality asked for:

1. **The speed comfortably fits the original** (its busy stretches, 1.5 × its bitrate, within three quarters of the
   speed: `AwayFromHome.Cap` returns nothing): the original, as at home. HDR and Dolby Vision are kept.
2. **Otherwise: the ladder.** With no reading yet the speed is taken as 8 Mbps (`AwayFromHome.StartCap`), so a file
   over 4 Mbps gets the ladder and a smaller one plays as it is. One stream with several qualities, lowest listed first, which
   AVPlayer, ExoPlayer and hls.js move between by themselves every few seconds:

   | Quality | Height | Bitrate |
   |---|---|---|
   | 1080p | 1080 | 8 Mbps |
   | 720p | 720 | 4 Mbps |
   | 480p | 480 | 1.5 Mbps |

   Only qualities at or below the file's own height are offered, each at no more than the file's own bitrate; with
   fewer than two left, it's one stream (3). The ladder needs a hardware encoder that puts keyframes only where told
   (VideoToolbox, NVENC); others get (3).
3. **No ladder** (software encoding, the server full, the disk low): one converted stream, capped by the speed as
   before, or at 720p 4 Mbps with no reading.

A capped stream also lowers its resolution to what the bitrate suits (`PlaybackDecider.HeightFor`, `BitrateFor` read
backwards at the table's points): 8 Mbps and up 1080p (12 Mbps 1440p, 20 Mbps any size), 4 Mbps 720p, 2.5 Mbps 540p,
below that 480p. So a 4K file capped to 3 Mbps is 540p, not 4K at 3 Mbps. Only a cap without a height of its own does
this (the connection's, the server's limit, a guest's link); a height asked for (the menu, a download) is kept. A conversion started for a remote viewer opens with 1-second
segments (2 seconds at home).

### How the ladder works

- One playback session holds one conversion per quality, each started by the first request for one of its segments
  or its init segment (playlists are written from the duration, so fetching a playlist starts nothing). The start
  itself gets the lowest quality's opening going, as it does a single stream's; a quality's init segment, fetched as a
  player moves to it, starts it where the player was in the quality it leaves. Every quality uses the same
  segment grid, opening and forced keyframes, so a player can switch at any segment.
- The session counts as **one** converting slot and one viewer, whatever it holds. It never takes another screen's
  place: with the server full it's one stream, which may. A quality not fetched for 30 seconds stops (the one being
  watched keeps the usual two minutes, for pauses). The low-disk guard is checked as each one starts. Each quality is
  the single stream's plan at its size and bitrate, so sound, subtitles and colour are the same in all (sound is AAC
  or the file's own, never lossless, in a ladder).
- Routes: `/api/v1/playback/{sid}/v/{k}/main.m3u8`, `init.mp4` and `{n}.m4s`, beside the single-quality ones, which
  stay. `master.m3u8` lists one `EXT-X-STREAM-INF` per quality with `BANDWIDTH` (busy stretches), `AVERAGE-BANDWIDTH`,
  `RESOLUTION`, `CODECS` and `FRAME-RATE`.

## API (all optional, older apps and servers ignore them)

- Request `original: true`: the file as it is (the Quality menu's *Original*): no away cap, no ladder.
- Request `maxHeight`/`maxBitrate` (existing): the menu's 1080p, 720p and 480p, the same values as the table; no ladder.
- Response `adaptive: true` when the stream is the ladder, so the apps don't step down themselves. Absent before 0.67.
- Response `fileBitrate`: the file's average bitrate in bits per second. Absent before 0.67.
- `GET /api/v1/remote` (admin) gains `remoteSpeed`: `{bestBitrate, measuredAt, busiestBitrate}`, the best
  `measuredBitrate` a remote device sent in the last 30 days and the bitrate of the library's busiest tenth of files,
  for Settings › Remote access's upload warning. Absent with no remote reading.

## The players

- **Quality menu** in every player (iPhone, iPad, Apple TV, Mac, Android phone and TV, web): *Automatic*, *Original*,
  *1080p*, *720p*, *480p*. A pick is remembered on that device for plays away from home; at home every play starts on
  Automatic. On the web, Automatic keeps its label *Best for this device*.
- **Step down** (Automatic, not on the ladder): two stalls within a minute, or one over 10 seconds, not counting the
  first 3 seconds after a start or seek, reopen at the same place on the first quality below what's playing, keeping
  audio, subtitles and speed, and show *Lowered quality for your connection* briefly. The lowered quality lasts for that
  title only. On the iPad's engine, the first step goes to the server's stream instead.
- **Step back up** (not on the ladder): after 5 minutes without a stall, when the speed fits the next quality up with
  the usual headroom; at most once every 5 minutes.
- **No black gap.** A switch keeps the last picture on screen (sound off) until the new stream plays.
- On the ladder, the players start from the saved speed (ExoPlayer's bandwidth meter, hls.js's
  `abrEwmaDefaultEstimate`) and are capped to the screen's size.

## Upload warning

Settings › Remote access says when the best speed remote devices have measured is below what the library's busiest
files need, so the owner knows the home connection's upload, not Flexarr, is the limit.
