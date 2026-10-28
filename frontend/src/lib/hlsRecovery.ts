/** What a fatal media error needs from hls.js: its own two ways of recovering. */
export interface MediaRecoverable {
  recoverMediaError(): void
  swapAudioCodec(): void
}

/** Errors further apart than this are separate problems, each with its own tries. */
const SEPARATE_AFTER_MS = 10_000

/**
 * hls.js's advice for a fatal media error, as a handler: recover; if the error comes straight back, swap the audio
 * codec and recover; if it comes back again, `giveUp`. Without this, a file that keeps failing to decode loops through
 * recoveries for ever and never says anything.
 */
export function mediaErrorRecovery(player: MediaRecoverable, giveUp: () => void, now: () => number = () => performance.now()) {
  let last = -Infinity
  let attempts = 0
  return () => {
    const at = now()
    if (at - last > SEPARATE_AFTER_MS) attempts = 0
    last = at
    attempts += 1
    if (attempts === 1) player.recoverMediaError()
    else if (attempts === 2) {
      player.swapAudioCodec()
      player.recoverMediaError()
    } else giveUp()
  }
}
