# iPhone remote for the Apple TV

When an Apple TV in the house is playing Flexarr, the iPhone app shows a **Playing on TV** bar; tapping it opens a
full Now Playing remote. It does what the Siri Remote does badly (tracks, episodes, sound, scrubbing) and leaves
menu navigation and volume to Apple's own Remote in Control Centre: no d-pad.

Decisions:
- Any signed-in household phone sees and controls any TV, whatever profile either is on.
- The bar appears on its own; the remote never takes over the app.
- Android and web follow once the iPhone version settles.

## What exists

- `GET /devices` lists devices with `NowPlaying` (item, position, duration, paused), from the TV's progress reports
  (`PlaybackSessions`, stale after 15 minutes).
- Device commands over the TV's SSE stream: Play, Pause, Resume, Stop, Watch, Sound (`DeviceCommands.cs`);
  the TV handles them in `AppModel.swift`.
- The phone keeps `deviceEvents()` open (pairing prompts), and FlexarrKit has `devices()`, `control(device:)`,
  `setSound(device:)`.

## Rules to keep

- API only grows: new `DeviceCommandKind` values, new optional fields. Older TV apps read new kinds as `unknown` and
  must ignore them; the phone hides controls a TV can't take (`accepts`, below).
- The phone treats every new field as optional ("Absent from servers before …").
- Main stays releasable: the feature lands complete, so nothing hides behind a setting.

## Phase 1: bar and Now Playing with live state

Server
- [x] New command kinds: `Seek` (to seconds), `Skip` (by ± seconds), `Next`, `Previous`, `SkipSegment` (intro/recap/credits).
- [x] `POST /devices/{id}/seek`, `/skip`, `/next`, `/previous`, `/skip-segment`: same trust check and 200/202/404 as pause.
- [x] The TV says which commands it takes (the stream's `accepts` query); `DeviceDto.Accepts`, absent for old apps.
- [x] Richer `NowPlayingDto`: playback rate, current segment (intro/credits, for the Skip button), next episode, show/season ids. All optional.
- [x] Push now-playing changes: a `nowPlaying` event on the device event stream to every household phone, sent on start, pause/resume, seek, track change and stop (not just on the timer).
- [x] Tests: commands reach the listener, `accepts` round-trips, the event fires on pause and seek, an old-style device that says nothing.

Apple TV
- [x] Handle Seek, Skip, Next, Previous, SkipSegment in `AppModel` → `PlaybackController` (reuse its existing skip and next-episode paths).
- [x] Say which commands it takes (`accepts`).
- [x] Report progress immediately on pause, resume, seek and segment changes, as well as on the timer.
- [ ] Show who's controlling ("Dan's iPhone paused") briefly, like the existing `SentBy`.

FlexarrKit
- [x] Models: new command kinds, `accepts`, the richer `NowPlaying`, the `nowPlaying` device event (all forward-compatible).
- [x] Client calls for the new endpoints.
- [x] `swift test`.

iPhone
- [x] `RemoteModel`: the household's playing TVs, kept live from `deviceEvents()` with `devices()` as a fallback when the stream reconnects.
- [x] Playing on TV bar above the tab bar (and beside the music mini-player when both play): artwork, title, TV name, play/pause.
- [x] Now Playing remote screen: artwork/backdrop, title and episode, scrubber (drag, seek on release), ±10s, play/pause, Next/Previous, Skip Intro/Credits when the TV is in one, Stop.
- [x] More than one TV playing: a picker in the bar/screen.
- [x] Optimistic updates (scrubber and play/pause move at once, snap back if the TV disagrees); a stale or offline TV shows as such.
- [x] Haptics on play/pause; VoiceOver labels; Dynamic Type.
- [x] Changelog entry under Unreleased (### iPhone).

## Phase 2: audio, subtitles and sound

- [x] `NowPlayingDto`: the TV's audio and subtitle tracks and which are selected (the TV reports them).
- [x] Commands `AudioTrack` and `SubtitleTrack` (id, or off), handled through `PlaybackController`'s existing `selectAudio`/subtitle paths.
- [x] Remote screen: audio and subtitles menu. Find Subtitles (Bazarr) from the remote: not yet.
- [x] Sound sheet: dialogue boost, night mode, speakers through the existing `Sound` command; show the TV's current values.
- [x] Playback speed, if the TV supports it for the item.

## Phase 3: episodes and up next

- [x] Remote screen: Up Next row and the season's episodes (from the item's show/season ids).
- [x] Tap an episode → `play(on:)` to the same TV; mark watched/unwatched from the list.
- [ ] Browsing the app while a TV is playing: "Play on TV" offered first on detail pages. (Not built; the episode list in the remote covers the season.)

## Phase 4: lock screen and Dynamic Island

- [x] Widget extension Live Activity: artwork, title, progress, play/pause and skip buttons (App Intents calling `control(device:)`).
- [x] Start the activity when the phone sees a TV start playing (and the user has the app); end it on stop.
- [x] Background updates: the server sends ActivityKit push updates through the relay (`relay/`, push.flexarr.io): a new push type, activity push tokens registered with the server, rate-limited to state changes.
- [x] ~~Settings toggle~~: iOS has its own (Settings › Flexarr › Live Activities), so no toggle of ours.
- [ ] Relay changes deployed and tested before the server change lands.

## Phase 5: continue on iPhone

- [x] "Continue on iPhone" on the remote screen: stop on the TV (saving position), start the same item on the phone at that position, on the phone's profile.
- [ ] The reverse, from the phone's player: "Play on TV" at the current position (extends the existing play command with a start position).
- [ ] Offer it when a TV stops? Decide after trying it.

## Before landing

- [ ] Try it end to end: TV simulator + iPhone simulator against a branch server (done), then the real TV and iPhone (to do; a Live Activity update arriving by push can only be seen on a real phone).
- [ ] Old TV app (from main) with the new phone: new buttons hidden, pause/resume still work.
- [ ] New TV with an old server: nothing breaks.
- [x] Guide page in `docs/guide` (`make guide`): iphone-remote.
- [ ] `/audit`, then `/land`.

## Later

- [ ] Android phone remote (`clients/android` already has the devices client).
- [ ] Web app remote (cheap: the same endpoints and events).
- [ ] Control an Android TV once that app exists.
