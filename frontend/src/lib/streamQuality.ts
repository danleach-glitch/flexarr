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
