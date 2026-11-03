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

- With no credits marker, an episode of 2 minutes or less shows no button before the end.
- **Cancel** during the countdown lets the episode play on. It then closes at the end, unless Next Episode is pressed.
- If the file ends before the countdown has run (its real length was shorter than the server said), the next
  episode plays straight away when the countdown had started, or a 10-second countdown runs over the last frame when
  it hadn't.

The rule is a pure function on each platform, `nextEpisodeTiming` (FlexarrKit `Presentation.swift`, Android
`core/Presentation.kt`, web `frontend/src/lib/upNext.ts`), each with the same tests.
