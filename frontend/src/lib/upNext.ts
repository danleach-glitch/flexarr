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
