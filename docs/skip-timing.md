# When Skip intro and Skip recap count down

Every player (Apple TV, iPhone, Mac, Android, Android TV, the web) follows these rules. They apply when the profile's
**Skip intros** or **Skip recaps** setting is on and the server found an `intro` or `recap` marker. With the setting
off, nothing changes: a plain *Skip intro* (or *Skip recap*) button shows inside the marker, as before.

Instead of jumping without warning, the player counts down first in the split pill Next Episode uses:
**Skip intro in 3 | ✕** (or **Skip recap in 5 | ✕**). A fill runs across the pill's left half as it counts. Pressing
the pill skips now; **✕** (Cancel) puts the pill away and lets the intro play.

| Marker | Countdown | Runs from | Skips at |
|---|---|---|---|
| intro at `s` (`s ≥ 3`) | 3 seconds | `s − 3` | `s`, to the intro's end |
| recap at `s` (`s ≥ 5`) | 5 seconds | `s − 5` | `s`, to the recap's end |
| intro or recap that starts sooner than its countdown | 3 or 5 seconds | `0` | `3` or `5`, to its end |

- The countdown runs over the picture *before* the marker, so the skip lands as the intro starts and none of it plays.
- **Room:** a countdown needs at least 2 seconds of the marker left after it. Without them there's no countdown, only the
  plain button.
- **It follows the picture:** pausing holds it, at 2× speed it passes twice as fast, and scrubbing back before it puts
  it away (it comes back when the picture reaches it again).
- **Landing inside** a marker that hasn't been skipped or cancelled (a resume, a seek past the countdown) starts a
  fresh countdown from where it landed, when there's room; otherwise the plain button shows. A small seek within the
  countdown carries on counting.
- **Back to back:** skipping a marker that another auto-skipped marker starts within a second of (a recap straight into
  an intro) skips to the end of both, with no second countdown.
- **Once each:** a marker skipped (by itself or by the button) shows the plain button if you seek back into it, so you
  can watch it. A cancelled one shows nothing more for the rest of the episode. Neither changes the setting.
- On the TVs the pill takes focus: Select skips now, Back is ✕, and left and right move between the halves. On the Mac,
  Return skips and Esc cancels.
- The countdown's number uses fixed-width digits and the pill holds its width while counting. Screen readers hear
  it once as it shows.
- The iPhone remote shows a plain *Skip intro* during the countdown; pressing it skips to the marker's end, even before
  the marker starts.

The rule is a pure function on each platform, `SkipTiming` (FlexarrKit `Presentation.swift`, Android
`core/Presentation.kt`, web `frontend/src/lib/skipTiming.ts`). All three test against the cases in
`docs/skip-timing.json`: add a case there, not to one platform.
