/** The countdown to the next episode, in seconds. The picture plays on under it. */
export const NEXT_EPISODE_COUNTDOWN_SECONDS = 10

/** With Skip credits on, the countdown starts this long before the credits (so the next episode plays 2s into them). */
export const SKIP_CREDITS_LEAD_SECONDS = 8

/** With no credits marker, Next Episode shows this long before the end. */
export const UNMARKED_LEAD_SECONDS = 60

/** An episode this short (or shorter) with no credits marker shows no Next Episode before the end. */
const SHORTEST_UNMARKED_SECONDS = 120

/**
 * When Next Episode shows, and what follows, on the episode's timeline (seconds). `countdownFrom` and `nextAt` come
 * together: the countdown runs between them and the next episode plays at `nextAt`. `closeAt` is where the player
 * closes before the end (Skip credits on, auto-play off, credits found); without it, the end closes the player.
 */
export interface NextEpisodeTiming {
  showAt: number
  countdownFrom?: number
  nextAt?: number
  closeAt?: number
}

/**
 * The rule every player follows (docs/next-episode-timing.md), for an episode with a next one: `credits` is where the
 * server found the credits start, if it did. Null when there's no button before the end.
 */
export function nextEpisodeTiming({
  credits,
  duration,
  skipCredits,
  autoPlayNext,
}: {
  credits?: number
  duration: number
  skipCredits: boolean
  autoPlayNext: boolean
}): NextEpisodeTiming | null {
  if (!(duration > 0)) return null
  const lastSeconds = () => ({ countdownFrom: Math.max(0, duration - NEXT_EPISODE_COUNTDOWN_SECONDS), nextAt: duration })
  if (credits === undefined) {
    if (duration <= SHORTEST_UNMARKED_SECONDS) return null
    const showAt = duration - UNMARKED_LEAD_SECONDS
    return autoPlayNext ? { showAt, ...lastSeconds() } : { showAt }
  }
  if (skipCredits && autoPlayNext) {
    // Always 2 seconds into the credits (never past the end): credits in the first seconds get a shorter countdown.
    const nextAt = Math.min(credits + NEXT_EPISODE_COUNTDOWN_SECONDS - SKIP_CREDITS_LEAD_SECONDS, duration)
    const countdownFrom = Math.max(0, credits - SKIP_CREDITS_LEAD_SECONDS)
    return { showAt: countdownFrom, countdownFrom, nextAt }
  }
  if (autoPlayNext) {
    const counting = lastSeconds()
    // Credits in the last 10 seconds: the button comes with the countdown.
    return { showAt: Math.min(credits, counting.countdownFrom), ...counting }
  }
  // Auto-play off: Skip credits ends the episode (closing the player) as the credits start; otherwise the end does.
  return skipCredits ? { showAt: credits, closeAt: credits } : { showAt: credits }
}

/** Whole seconds left on a countdown that ends at `nextAt`, at position `time`: 0 to 10. */
export function countdownLeft(nextAt: number, time: number): number {
  return Math.min(NEXT_EPISODE_COUNTDOWN_SECONDS, Math.max(0, Math.ceil(nextAt - time)))
}

/** How long before the next episode would play it's got ready: the warm-up keeps a stream only so long. */
export const PREPARE_LEAD_SECONDS = 60

/** What the player has learnt about Next Episode during this episode. */
export interface UpNextState {
  /** A seek or start landed past `nextAt` (not counting down): the countdown runs over the last 10 seconds instead. */
  late: boolean
  /** Cancel was pressed: no countdown for the rest of the episode, Play now stays, and the end closes the player. */
  cancelled: boolean
}

export const upNextFresh: UpNextState = { late: false, cancelled: false }

/**
 * The state after the position moved to `time`, by playing on or (`jumped`) by a seek or a start. Landing past `nextAt`
 * by a jump plays on with the card up; scrubbing back before the countdown puts that right again. Cancel holds.
 */
export function upNextMoved(timing: NextEpisodeTiming | null, state: UpNextState, time: number, jumped: boolean): UpNextState {
  if (!timing || timing.countdownFrom === undefined || timing.nextAt === undefined) return state
  if (time < timing.countdownFrom) return state.late ? { ...state, late: false } : state
  if (jumped && !state.late && time >= timing.nextAt) return { ...state, late: true }
  return state
}

/** The timing as it stands: after a late landing, the countdown runs over the last 10 seconds, as with Skip credits off. */
function effective(timing: NextEpisodeTiming, duration: number, state: UpNextState): NextEpisodeTiming {
  if (!state.late || timing.nextAt === undefined) return timing
  return { ...timing, countdownFrom: Math.max(0, duration - NEXT_EPISODE_COUNTDOWN_SECONDS), nextAt: duration }
}

export interface UpNextNow {
  /** The card shows (Play now, and Cancel while counting). */
  show: boolean
  counting: boolean
  /** Whole seconds left on the countdown, while counting. */
  countdown: number
  /** The next episode should be got ready. */
  prepare: boolean
  /** What happens now: the next episode plays, or the player closes. Undefined: play on. */
  action?: 'next' | 'close'
}

/**
 * Next Episode at `time` (docs/next-episode-timing.md), before the end. `holds(at)` says the sleep timer stops
 * playback before `at`: no countdown and no closing then. Changing a setting changes `timing`, which acts at once.
 */
export function upNextAt({
  timing,
  duration,
  state,
  time,
  holds = () => false,
}: {
  timing: NextEpisodeTiming | null
  duration: number
  state: UpNextState
  time: number
  holds?: (at: number) => boolean
}): UpNextNow {
  if (!timing) return { show: false, counting: false, countdown: 0, prepare: false }
  const t = effective(timing, duration, state)
  const counting = !state.cancelled && t.countdownFrom !== undefined && t.nextAt !== undefined && time >= t.countdownFrom && !holds(t.nextAt)
  const countdown = counting ? countdownLeft(t.nextAt!, time) : 0
  const show = counting || time >= t.showAt
  // Ready no earlier than a minute before it would play (the warm-up doesn't keep longer), nor before the card shows.
  const prepare = show && !state.cancelled && time >= Math.max(t.showAt, (t.nextAt ?? duration) - PREPARE_LEAD_SECONDS)
  let action: UpNextNow['action']
  if (counting && time >= t.nextAt!) action = 'next'
  else if (t.closeAt !== undefined && time >= t.closeAt && !state.cancelled && !holds(t.closeAt)) action = 'close'
  return { show, counting, countdown, prepare, action }
}

/**
 * At the end of the file, with no countdown already done: the next episode if the countdown had started, the player
 * closes after Cancel or with auto-play off, or else a countdown runs over the last frame (the file was shorter than
 * the server said). Null: hold (the sleep timer stopped it).
 */
export function upNextAtEnd({
  counting,
  state,
  autoPlayNext,
  holds = false,
}: {
  counting: boolean
  state: UpNextState
  autoPlayNext: boolean
  holds?: boolean
}): 'next' | 'close' | 'countdown' | null {
  if (counting) return 'next'
  if (state.cancelled || !autoPlayNext) return 'close'
  return holds ? null : 'countdown'
}
