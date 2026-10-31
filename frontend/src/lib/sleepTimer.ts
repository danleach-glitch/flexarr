/**
 * The sleep timer the podcast player and the video player share: in so many minutes, or when what's playing ends.
 * `until` is a Date.now() time. A book's can stop at the end of the chapter: `at` is where that is on its timeline.
 */
export type SleepTimer = { until: number } | { endOfEpisode: true } | { endOfChapter: true; at: number }

/** The timer's choices, in minutes. */
export const sleepMinutes = [15, 30, 45, 60] as const

/** The video player asks "Still watching?" this long before it stops, dimming the picture meanwhile. */
export const STILL_WATCHING_SECONDS = 60

/** The sound fades out over the last seconds before it stops. */
export const FADE_SECONDS = 10

/** "+15 min" on the Still watching? prompt. */
export const EXTEND_MINUTES = 15

/** The menu's choices, the last ending with what's playing ("At the end of this episode"); each timer starts when chosen. */
export function sleepChoices(end: string): { label: string; start: () => SleepTimer }[] {
  return [
    ...sleepMinutes.map((minutes) => ({ label: `In ${minutes} minutes`, start: () => ({ until: Date.now() + minutes * 60_000 }) })),
    { label: `At the end of this ${end}`, start: () => ({ endOfEpisode: true }) as const },
  ]
}

/** A book's choices: the minutes, or the end of the chapter, which ends at `chapterEnd` on the book's timeline. */
export function bookSleepChoices(chapterEnd: number | null): { label: string; start: () => SleepTimer }[] {
  return [
    ...sleepMinutes.map((minutes) => ({ label: `In ${minutes} minutes`, start: () => ({ until: Date.now() + minutes * 60_000 }) })),
    ...(chapterEnd !== null ? [{ label: 'At the end of this chapter', start: () => ({ endOfChapter: true, at: chapterEnd }) as const }] : []),
  ]
}

/** Whole minutes left on a running timer (at least 1), or null when it waits for the end instead. */
export function minutesLeft(timer: SleepTimer | null, now: number): number | null {
  return timer && 'until' in timer ? Math.max(1, Math.ceil((timer.until - now) / 60_000)) : null
}

/** The timer's button, said: "Sleep timer, 12 min left". */
export function sleepLabel(timer: SleepTimer | null, now: number, end = 'episode'): string {
  const minutes = minutesLeft(timer, now)
  return timer === null ? 'Sleep timer' : minutes !== null ? `Sleep timer, ${minutes} min left` : `Sleep timer, at the end of this ${end}`
}

/**
 * Seconds of watching left before the timer stops playback: by the clock for a timer in minutes, or `toEnd` (how long
 * what's playing has left, at its speed) for one waiting for the end. Null with no timer.
 */
export function secondsLeft(timer: SleepTimer | null, now: number, toEnd: number): number | null {
  if (!timer) return null
  return 'until' in timer ? (timer.until - now) / 1000 : toEnd
}

/** The timer with another `minutes` on it, from now if it had already run out or was waiting for the end. */
export function extended(timer: SleepTimer | null, now: number, minutes = EXTEND_MINUTES): SleepTimer {
  const from = timer && 'until' in timer ? Math.max(now, timer.until) : now
  return { until: from + minutes * 60_000 }
}
