import type { Marker, MarkerType } from '../api/types'

/**
 * When Skip intros / Skip recaps count down before skipping (docs/skip-timing.md): from `countdownFrom` to `skipAt`,
 * over the picture before the marker, so the skip lands as it starts. It follows the playing position.
 */
export interface SkipTiming {
  countdownFrom: number
  skipAt: number
}

/** A countdown needs this much of the marker left after it, or there's only the plain button. */
const MINIMUM_LEFT = 2
/** Markers starting within this of where a skip lands are skipped with it (a recap straight into an intro). */
const CHAIN_GAP = 1

/** The countdown's length: 3 seconds for an intro, 5 for a recap, null for anything else. */
export function skipSeconds(type: MarkerType | string): number | null {
  return type === 'Intro' ? 3 : type === 'Recap' ? 5 : null
}

const fitting = (timing: SkipTiming, marker: Marker): SkipTiming | null => (marker.end - timing.skipAt >= MINIMUM_LEFT ? timing : null)

/** The countdown before `marker` starts (from 0 when it starts sooner than that); null without room for one. */
export function skipTiming(marker: Marker): SkipTiming | null {
  const length = skipSeconds(marker.type)
  if (length === null) return null
  const countdownFrom = Math.max(0, marker.start - length)
  return fitting({ countdownFrom, skipAt: countdownFrom + length }, marker)
}

/** A fresh countdown from `time`, where a seek or resume landed inside `marker`; null outside it or without room. */
export function skipLanding(marker: Marker, time: number): SkipTiming | null {
  const length = skipSeconds(marker.type)
  if (length === null || time < marker.start || time >= marker.end) return null
  return fitting({ countdownFrom: time, skipAt: time + length }, marker)
}

/** Whole seconds left at `time`: null before the countdown, 0 once it's time to skip. */
export function skipLeft(timing: SkipTiming, time: number): number | null {
  if (time < timing.countdownFrom) return null
  return Math.min(timing.skipAt - timing.countdownFrom, Math.max(0, Math.ceil(timing.skipAt - time)))
}

/** How far through the countdown `time` is, 0 to 1, for the pill's fill. */
export function skipProgress(timing: SkipTiming, time: number): number {
  const length = timing.skipAt - timing.countdownFrom
  return length > 0 ? Math.min(1, Math.max(0, (time - timing.countdownFrom) / length)) : 1
}

/** Where skipping `marker` lands: its end, or past any auto-skipped intro or recap starting straight after it. */
export function skipEnd(marker: Marker, markers: Marker[], skipIntros: boolean, skipRecaps: boolean): number {
  let end = marker.end
  let moved = true
  while (moved) {
    moved = false
    for (const other of markers) {
      if (other.end <= end || other.start > end + CHAIN_GAP) continue
      const auto = other.type === 'Intro' ? skipIntros : other.type === 'Recap' ? skipRecaps : false
      if (auto) {
        end = other.end
        moved = true
      }
    }
  }
  return end
}

export const markerKey = (marker: Marker) => `${marker.type}@${marker.start}`

/** What the player has learnt about skipping during this episode. */
export interface SkipState {
  /** Skipped (by themselves or the button): seeking back into one offers the plain button. */
  skipped: string[]
  /** Countdown cancelled: nothing more for that marker this episode. */
  cancelled: string[]
  /** Countdowns started where a seek or resume landed inside a marker. */
  landed: Record<string, SkipTiming>
  /** The marker counting down at the last tick. */
  counting?: string
}

export const skipFresh: SkipState = { skipped: [], cancelled: [], landed: {} }

/** What the player shows or does at a moment. */
export interface SkipNow {
  state: SkipState
  /** Counting down: the marker, seconds left and the fill. */
  counting?: { marker: Marker; left: number; progress: number }
  /** The plain button's marker. */
  button?: Marker
  /** Time to skip, to here. */
  skipTo?: number
}

/**
 * The skip countdown at `time` (docs/skip-timing.md), as a pure step: the player keeps `state` between ticks. A seek
 * clears `counting` first (skipSeeked), so landing past a countdown starts a fresh one rather than skipping.
 */
export function skipAt({ markers, time, skipIntros, skipRecaps, state }: { markers: Marker[]; time: number; skipIntros: boolean; skipRecaps: boolean; state: SkipState }): SkipNow {
  const skippable = markers.filter((m) => m.type === 'Intro' || m.type === 'Recap')
  const auto = (m: Marker) => (m.type === 'Intro' ? skipIntros : skipRecaps)
  let landed = state.landed
  for (const marker of skippable) {
    const key = markerKey(marker)
    if (!auto(marker) || state.skipped.includes(key) || state.cancelled.includes(key) || time >= marker.end - 1) continue
    // Scrubbed back before a countdown that started where a seek landed: the usual one stands again.
    if (landed[key] && time < landed[key].countdownFrom) {
      landed = { ...landed }
      delete landed[key]
    }
    const timing = landed[key] ?? skipTiming(marker)
    if (!timing || time < timing.countdownFrom) continue
    let counting = timing
    if (time >= timing.skipAt) {
      if (state.counting === key) {
        // Counted all the way down while playing: skip, with anything auto-skipped straight after.
        const end = skipEnd(marker, markers, skipIntros, skipRecaps)
        const passed = skippable.filter((m) => m.start >= marker.start && m.end <= end).map(markerKey)
        return { state: { ...state, landed, counting: undefined, skipped: [...state.skipped, ...passed] }, skipTo: end }
      }
      // Landed past the countdown without counting (a resume, a seek): a fresh one from here.
      const fresh = skipLanding(marker, time)
      if (!fresh) continue
      landed = { ...landed, [key]: fresh }
      counting = fresh
    }
    return {
      state: { ...state, landed, counting: key },
      counting: { marker, left: skipLeft(counting, time) ?? 0, progress: skipProgress(counting, time) },
    }
  }
  const next = { ...state, landed, counting: undefined }
  // The plain button, not in the marker's last second (so it doesn't flash as it ends), and not after a Cancel.
  const button = skippable.find((m) => time >= m.start && time < m.end - 1 && !state.cancelled.includes(markerKey(m)))
  return { state: next, button }
}

/** After a seek: the next tick works out afresh where it stands (a small seek within a countdown counts on). */
export const skipSeeked = (state: SkipState): SkipState => ({ ...state, counting: undefined })

/** The pill was pressed (or the countdown's skip half): skip `marker` and anything auto-skipped straight after. */
export function skipPressed(state: SkipState, marker: Marker, markers: Marker[], skipIntros: boolean, skipRecaps: boolean): { state: SkipState; skipTo: number } {
  const end = skipEnd(marker, markers, skipIntros, skipRecaps)
  const passed = markers.filter((m) => (m.type === 'Intro' || m.type === 'Recap') && m.start >= marker.start && m.end <= end).map(markerKey)
  return { state: { ...state, counting: undefined, skipped: [...state.skipped, ...passed] }, skipTo: end }
}

/** ✕ on a countdown: the marker plays, with nothing more offered for it this episode. */
export const skipCancelled = (state: SkipState, marker: Marker): SkipState => ({ ...state, counting: undefined, cancelled: [...state.cancelled, markerKey(marker)] })
