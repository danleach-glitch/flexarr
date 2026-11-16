/**
 * The player's Quality menu and what each choice asks the server for (docs/remote-streaming.md).
 */

/** Automatic (labelled Best for this device), the file as it is, or a cap. */
export type Quality = 'auto' | 'original' | Step
/** The capped qualities, highest first: the same as the server's ladder. */
export type Step = 1080 | 720 | 480

export const STEPS: { height: Step; bitrate: number }[] = [
  { height: 1080, bitrate: 8_000_000 },
  { height: 720, bitrate: 4_000_000 },
  { height: 480, bitrate: 1_500_000 },
]

/** The menu, in order. */
export const QUALITIES: Quality[] = ['auto', 'original', ...STEPS.map((s) => s.height)]

/** What a start request carries for `q`. Automatic leaves it to the server. */
export function requestFor(q: Quality): { maxHeight?: number; maxBitrate?: number; forceTranscode?: boolean; original?: boolean } {
  if (q === 'auto') return {}
  if (q === 'original') return { original: true }
  return { maxHeight: q, maxBitrate: STEPS.find((s) => s.height === q)?.bitrate, forceTranscode: true }
}

// ---------- Stepping down and back up (Automatic, a single stream only: the ladder moves by itself)

/** Two stalls this close together step down. */
export const STALL_WINDOW_MS = 60_000
/** One stall this long steps down. */
export const LONG_STALL_MS = 10_000
/** Stalls this soon after a start or seek don't count: the buffer is still filling. */
export const SETTLE_MS = 3000
/** Without a stall for this long, and no change for as long, the next quality up is tried. */
export const SMOOTH_MS = 5 * 60_000
/** Of the speed measured, this much is counted on (the same headroom as the server's). */
export const HEADROOM = 0.75
/** A stream's busy stretches run this far over its average. */
export const PEAK = 1.5

/** Whether a stall beginning at `at` counts, the stream having started (or been seeked) at `settledFrom`. */
export const stallCounts = (at: number, settledFrom: number) => at - settledFrom >= SETTLE_MS

/**
 * Whether to step down: two counted stalls (by when they began) within a minute of each other, or the one still
 * going (begun at `stalledSince`) longer than 10 seconds.
 */
export function stepDownDue(stalls: number[], now: number, stalledSince?: number): boolean {
  if (stalledSince !== undefined && now - stalledSince > LONG_STALL_MS) return true
  const sorted = [...stalls].sort((a, b) => a - b)
  return sorted.some((at, i) => i > 0 && at - sorted[i - 1] <= STALL_WINDOW_MS)
}

/**
 * The first quality below what's playing (its height and bit rate, either unknown), or null at the bottom. With
 * neither known, 720p: what the server sends away from home with no speed reading.
 */
export function stepBelow(playing: { height?: number | null; bitrate?: number | null }): Step | null {
  const { height, bitrate } = playing
  if (height == null && bitrate == null) return 720
  return STEPS.find((s) => (height == null || s.height < height) && (bitrate == null || s.bitrate < bitrate))?.height ?? null
}

/** Whether a stream of `bitrate` fits a link of `speed` with the usual headroom. */
export const fits = (speed: number, bitrate: number) => bitrate * PEAK <= speed * HEADROOM

/**
 * After stepping down to `lowered`: the next quality up to go to ('auto' when that's back to Automatic), or null to
 * stay. Only after 5 minutes without a stall or a change, and when the speed measured fits it (Automatic: the file's
 * own bit rate, when known).
 */
export function stepUp(
  lowered: Step,
  o: { now: number; lastStall: number; lastChange: number; speed?: number; fileBitrate?: number },
): Step | 'auto' | null {
  if (o.now - Math.max(o.lastStall, o.lastChange) < SMOOTH_MS || o.speed === undefined) return null
  const index = STEPS.findIndex((s) => s.height === lowered)
  if (index < 0) return null
  if (index === 0) return o.fileBitrate !== undefined && fits(o.speed, o.fileBitrate) ? 'auto' : null
  const above = STEPS[index - 1]
  return fits(o.speed, above.bitrate) ? above.height : null
}

// ---------- Remembered away from home

const STORE = 'flexarr.quality'

/** Whether `hostname` is on the home network (or this machine), not reached from away. */
export function isPrivateHost(hostname: string): boolean {
  const h = hostname.toLowerCase().replace(/^\[|\]$/g, '')
  if (h === 'localhost' || h.endsWith('.local') || h === '::1' || /^f[cd][0-9a-f]{2}:/.test(h) || h.startsWith('fe80:')) return true
  const ip = h.match(/^(\d+)\.(\d+)\.\d+\.\d+$/)
  if (!ip) return false
  const [a, b] = [Number(ip[1]), Number(ip[2])]
  return a === 10 || a === 127 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31) || (a === 169 && b === 254)
}

/** Away from home: what the server says of this browser's connection, else the address it's reached at. */
export const isAway = (zone: string | undefined, hostname: string) => (zone ? zone !== 'Local' : !isPrivateHost(hostname))

function isQuality(value: unknown): value is Quality {
  return QUALITIES.includes(value as Quality)
}

/** The Quality a play starts on: the one last picked away from home, when away; Automatic at home. */
export function rememberedQuality(away: boolean, storage: Pick<Storage, 'getItem'> | undefined = safeStorage()): Quality {
  if (!away) return 'auto'
  try {
    const kept: unknown = JSON.parse(storage?.getItem(STORE) ?? 'null')
    return isQuality(kept) ? kept : 'auto'
  } catch {
    return 'auto'
  }
}

/** Keeps a pick for the next play away from home (only picks made away count). */
export function rememberQuality(q: Quality, away: boolean, storage: Pick<Storage, 'setItem' | 'removeItem'> | undefined = safeStorage()) {
  if (!away) return
  try {
    if (q === 'auto') storage?.removeItem(STORE)
    else storage?.setItem(STORE, JSON.stringify(q))
  } catch {
    // Private browsing: not kept.
  }
}

function safeStorage(): Storage | undefined {
  try {
    return typeof localStorage === 'undefined' ? undefined : localStorage
  } catch {
    return undefined
  }
}
