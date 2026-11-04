# When Next Episode appears

Every player (Apple TV, iPhone, Android, Android TV, the web) follows these rules. They depend on two profile
settings, **Skip credits** and **Auto-play the next episode**, and on whether the server found where the credits
start (a `credits` marker). They apply only when there is a next episode.

The countdown is **10 seconds**, and the picture keeps playing under it: it never pauses for it. The countdown follows
the playing position, so pausing during it holds it, and scrubbing back before it started puts it away.

| Credits | Skip credits | Auto-play | Next Episode shows | Countdown runs | Then |
|---|---|---|---|---|---|
| found at `c` | on | on | `c − 8` | `c − 8` to `c + 2` | the next episode plays, 2s into the credits |
| found at `c` | off | on | `c` | the last 10s | the next episode plays at the end |
| found at `c` | on | off | `c` | none | the player closes as the credits start |
| found at `c` | off | off | `c` | none | the player closes at the end |
| none | either | on | 60s before the end | the last 10s | the next episode plays at the end |
| none | either | off | 60s before the end | none | the player closes at the end |

- With no credits marker, an episode of 2 minutes or less shows no button before the end. With no known length (0),
  there's no timing at all.
- **A seek or start that lands past `nextAt`** (scrubbing into the credits for an after-credits scene, or resuming
  there) doesn't move on at once: the episode plays on with Next Episode up, and the countdown runs over its last 10
  seconds, as with Skip credits off. Only playing into `nextAt` while counting down moves on by itself.
- The countdown is in the episode's seconds: at 2× speed it passes in 5.
- Credits in the first 8 seconds: the countdown starts at 0 and still ends 2 seconds into them (so it's shorter).
- Credits in the last 10 seconds, with Skip credits off: the full countdown runs over the last 10 seconds, and the
  button shows with it, just before the credits.
- Cancel after the file has ended closes the player.
- **Cancel** during the countdown lets the episode play on, with a plain Next Episode button still up (the web keeps
  the card with only Play now). It then closes at the end, unless Next Episode is pressed. Cancel holds for the rest of
  that episode, through an audio change or a stream reopened mid-play. There's no dismiss before the countdown.
- Cancel is an **✕**, the right half of the Next Episode pill (named Cancel for screen readers). Each half is its own
  button: on a TV the focused half is white and the other grey; elsewhere Next Episode is white and the ✕ grey. The
  pill keeps its "in 10" width while counting (fixed-width digits), and fades out as it is when moving on.
- On the TVs, Back during the countdown is Cancel, and the remote's left and right move between the countdown's
  buttons. After Cancel, Select pauses as usual.
- Moving on from the Next Episode button (or the countdown) counts the episode as watched, whatever its length.
- If the file ends before the countdown has run (its real length was shorter than the server said), the next
  episode plays straight away when the countdown had started, or a 10-second countdown runs over the last frame when
  it hadn't.

The rule is a pure function on each platform, `nextEpisodeTiming` (FlexarrKit `Presentation.swift`, Android
`core/Presentation.kt`, web `frontend/src/lib/upNext.ts`). All three test against the cases in
`docs/next-episode-timing.json`, so they can't drift: add a case there, not to one platform.
