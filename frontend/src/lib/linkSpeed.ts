/**
 * How fast data reaches this browser from the server lately, for the start request's `measuredBitrate`
 * (docs/fast-start.md): each stream segment big enough is a sample of its bytes over the time they took to arrive,
 * not counting the wait for the first byte (which includes the server converting it). Artwork doesn't count: small
 * files loaded side by side read far below the link. With a few samples, the upper quartile of the last several,
 * kept for half an hour; with fewer, nothing (better to stream as at home than to cap on a guess).
 */

/** Answers smaller than this are mostly the round trip, not the speed. */
export const MIN_SAMPLE_BYTES = 200_000
const KEPT = 12
/** Fewer samples than this say nothing. */
export const MIN_SAMPLES = 3
/** A converted or repackaged stream's segment, which is what's sampled. */
const SEGMENT = /\/api\/v1\/playback\/[^/]+\/\d+\.(ts|m4s)(\?|$)/
const FRESH_MS = 30 * 60_000
const STORE = 'flexarr.linkSpeed'

interface Sample {
  bps: number
  at: number
}

let samples: Sample[] = load()

function load(): Sample[] {
  try {
    const kept = JSON.parse(localStorage.getItem(STORE) ?? '[]') as Sample[]
    return Array.isArray(kept) ? kept.filter((s) => typeof s.bps === 'number' && typeof s.at === 'number') : []
  } catch {
    return []
  }
}

/** Notes `bytes` arriving over `ms` (from the first byte to the last). */
export function sample(bytes: number, ms: number, now = Date.now()) {
  if (bytes < MIN_SAMPLE_BYTES || ms <= 0) return
  samples = [...samples.filter((s) => now - s.at < FRESH_MS), { bps: Math.round((bytes * 8 * 1000) / ms), at: now }].slice(-KEPT)
  // Saved a moment later in one go, not on every segment.
  if (saveTimer === undefined && typeof window !== 'undefined') saveTimer = window.setTimeout(save, 5000)
}

let saveTimer: number | undefined
function save() {
  saveTimer = undefined
  try {
    localStorage.setItem(STORE, JSON.stringify(samples))
  } catch {
    // Private browsing: kept for this page only.
  }
}

/** The measured speed in bits per second, or undefined with nothing recent to go on. */
export function linkSpeed(now = Date.now()): number | undefined {
  const fresh = samples.filter((s) => now - s.at < FRESH_MS).map((s) => s.bps).sort((a, b) => a - b)
  return fresh.length >= MIN_SAMPLES ? fresh[Math.floor(fresh.length * 0.75)] : undefined
}

/** Watches the page's own requests to the server for samples (once). */
export function watchLinkSpeed() {
  if (typeof PerformanceObserver === 'undefined' || watching) return
  watching = true
  try {
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as PerformanceResourceTiming[]) {
        if (!entry.name.startsWith(location.origin) || !entry.transferSize || !SEGMENT.test(new URL(entry.name).pathname + new URL(entry.name).search)) continue
        sample(entry.transferSize, entry.responseEnd - entry.responseStart)
      }
    }).observe({ type: 'resource', buffered: true })
  } catch {
    // A browser without resource timing: no measurement, and streams go as at home.
  }
}
let watching = false

/** For tests. */
export function resetLinkSpeed() {
  samples = []
}
