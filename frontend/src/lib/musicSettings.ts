import { useSyncExternalStore } from 'react'

/**
 * How music plays in this browser: a device preference rather than a profile one (speakers differ, and a crossfade is a
 * matter of taste per room), so it lives in localStorage and never reaches the server.
 */
export interface MusicSettings {
  /** Seconds the next track overlaps the end of the current one; 0 is a clean cut. */
  crossfade: number
  /** Whether tracks are levelled with their ReplayGain tags. */
  normalise: boolean
  /** Whether light moves with the music behind Now Playing. */
  visualiser: boolean
}

export const crossfadeChoices = [0, 2, 4, 6, 8, 12] as const

const crossfadeKey = 'flexarr.crossfade'
const normaliseKey = 'flexarr.normaliseVolume'
const visualiserKey = 'flexarr.visualiser'
const changed = 'flexarr:music-settings'

export const defaultMusicSettings: MusicSettings = { crossfade: 0, normalise: true, visualiser: true }

export function readMusicSettings(): MusicSettings {
  try {
    const crossfade = Number(localStorage.getItem(crossfadeKey))
    return {
      crossfade: (crossfadeChoices as readonly number[]).includes(crossfade) ? crossfade : defaultMusicSettings.crossfade,
      normalise: localStorage.getItem(normaliseKey) !== '0',
      visualiser: localStorage.getItem(visualiserKey) !== '0',
    }
  } catch {
    return defaultMusicSettings
  }
}

export function writeMusicSettings(next: Partial<MusicSettings>) {
  try {
    if (next.crossfade !== undefined) localStorage.setItem(crossfadeKey, String(next.crossfade))
    if (next.normalise !== undefined) localStorage.setItem(normaliseKey, next.normalise ? '1' : '0')
    if (next.visualiser !== undefined) localStorage.setItem(visualiserKey, next.visualiser ? '1' : '0')
  } catch {
    // Storage blocked: the change holds for this page only.
  }
  // Same-tab listeners don't get a storage event, so they're told directly; other tabs hear the storage event.
  window.dispatchEvent(new Event(changed))
}

const subscribe = (notify: () => void) => {
  window.addEventListener(changed, notify)
  window.addEventListener('storage', notify)
  return () => {
    window.removeEventListener(changed, notify)
    window.removeEventListener('storage', notify)
  }
}
// The same object until a value changes, so useSyncExternalStore sees a stable snapshot; reading storage is cheap.
let snapshot = defaultMusicSettings
const getSnapshot = () => {
  const now = readMusicSettings()
  if (now.crossfade !== snapshot.crossfade || now.normalise !== snapshot.normalise || now.visualiser !== snapshot.visualiser) snapshot = now
  return snapshot
}

/** The device's music settings, live: a change on the Settings page reaches the player straight away. */
export function useMusicSettings(): MusicSettings {
  return useSyncExternalStore(subscribe, getSnapshot, () => defaultMusicSettings)
}
