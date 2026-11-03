/**
 * How fast data reaches this browser from the server lately, for the start request's `measuredBitrate`
 * (docs/fast-start.md): each big enough answer (artwork, stream segments) is a sample of bytes over the time they
 * took to arrive, not counting the wait for the first byte. The median of the last few, kept for half an hour.
 */

/** Answers smaller than this are mostly the round trip, not the speed. */
export const MIN_SAMPLE_BYTES = 200_000
const KEPT = 8
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
  try {
    localStorage.setItem(STORE, JSON.stringify(samples))
  } catch {
    // Private browsing: kept for this page only.
  }
}

/** The measured speed in bits per second, or undefined with nothing recent to go on. */
export function linkSpeed(now = Date.now()): number | undefined {
  const fresh = samples.filter((s) => now - s.at < FRESH_MS).map((s) => s.bps).sort((a, b) => a - b)
  return fresh.length ? fresh[Math.floor(fresh.length / 2)] : undefined
}

/** Watches the page's own requests to the server for samples (once). */
export function watchLinkSpeed() {
  if (typeof PerformanceObserver === 'undefined' || watching) return
  watching = true
  try {
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries() as PerformanceResourceTiming[]) {
        if (!entry.name.startsWith(location.origin) || !entry.transferSize) continue
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
