import { useMemo, useSyncExternalStore } from 'react'

/**
 * How films and episodes sound in this browser: the speakers, a lift for the dialogue and night mode. A device
 * preference (it's about the room), so it lives in localStorage and never reaches the server. The Apple and Android
 * apps keep the same three settings with the same values.
 */
export interface SoundSettings {
  speakers: Speakers
  dialogueBoost: DialogueBoost
  nightMode: NightMode
  /** Minutes after midnight that a scheduled night mode starts and stops; it runs past midnight when start > end. */
  nightStart: number
  nightEnd: number
}

export type Speakers = 'Auto' | 'Stereo' | 'Surround' | 'Headphones'
export type DialogueBoost = 'Off' | 'Low' | 'Medium' | 'High'
export type NightMode = 'Off' | 'On' | 'Scheduled'

export const speakerChoices: { value: Speakers; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Stereo', label: 'Stereo' },
  { value: 'Surround', label: '5.1' },
  { value: 'Headphones', label: 'Headphones' },
]
export const dialogueBoostChoices: DialogueBoost[] = ['Off', 'Low', 'Medium', 'High']
export const nightModeChoices: NightMode[] = ['Off', 'On', 'Scheduled']

/** The lift each strength gives the dialogue, in dB (the same in every app). */
export const dialogueBoostDb: Record<DialogueBoost, number> = { Off: 0, Low: 3, Medium: 6, High: 9 }

export const defaultSoundSettings: SoundSettings = { speakers: 'Auto', dialogueBoost: 'Off', nightMode: 'Off', nightStart: 22 * 60, nightEnd: 7 * 60 }

/** What the sound chain does right now: the settings with the schedule worked out. */
export interface SoundShaping {
  /** Channels to fold to; null leaves the browser's own. */
  channels: number | null
  dialogueDb: number
  night: boolean
}

export function shapingFor(settings: SoundSettings, now: Date): SoundShaping {
  return {
    channels: settings.speakers === 'Surround' ? 6 : settings.speakers === 'Auto' ? null : 2,
    dialogueDb: dialogueBoostDb[settings.dialogueBoost],
    night: settings.nightMode === 'On' || (settings.nightMode === 'Scheduled' && inWindow(now, settings.nightStart, settings.nightEnd)),
  }
}

/** Nothing shaped: the sound as it comes. */
export const unshaped: SoundShaping = { channels: null, dialogueDb: 0, night: false }

export function isShaping(s: SoundShaping) {
  return s.channels !== null || s.dialogueDb > 0 || s.night
}

function inWindow(now: Date, start: number, end: number) {
  const minute = now.getHours() * 60 + now.getMinutes()
  return start <= end ? minute >= start && minute < end : minute >= start || minute < end
}

/** "22:00" for 1320, as a time input wants it. */
export function timeOfDay(minutes: number) {
  return `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`
}

export function minutesOf(time: string): number | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(time)
  if (!m || Number(m[1]) > 23 || Number(m[2]) > 59) return null
  return Number(m[1]) * 60 + Number(m[2])
}

const key = 'flexarr.sound'
const changed = 'flexarr:sound-settings'

export function readSoundSettings(): SoundSettings {
  try {
    const saved = JSON.parse(localStorage.getItem(key) ?? '{}') as Partial<SoundSettings>
    const minutes = (v: unknown, fallback: number) => (typeof v === 'number' && Number.isInteger(v) && v >= 0 && v < 24 * 60 ? v : fallback)
    return {
      speakers: speakerChoices.some((c) => c.value === saved.speakers) ? saved.speakers! : defaultSoundSettings.speakers,
      dialogueBoost: dialogueBoostChoices.includes(saved.dialogueBoost!) ? saved.dialogueBoost! : defaultSoundSettings.dialogueBoost,
      nightMode: nightModeChoices.includes(saved.nightMode!) ? saved.nightMode! : defaultSoundSettings.nightMode,
      nightStart: minutes(saved.nightStart, defaultSoundSettings.nightStart),
      nightEnd: minutes(saved.nightEnd, defaultSoundSettings.nightEnd),
    }
  } catch {
    return defaultSoundSettings
  }
}

export function writeSoundSettings(next: Partial<SoundSettings>) {
  try {
    localStorage.setItem(key, JSON.stringify({ ...readSoundSettings(), ...next }))
  } catch {
    // Storage blocked: nothing to keep it in, so it doesn't hold.
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
// The same object until a value changes, so useSyncExternalStore sees a stable snapshot.
let snapshot = defaultSoundSettings
const getSnapshot = () => {
  const now = readSoundSettings()
  if ((Object.keys(now) as (keyof SoundSettings)[]).some((k) => now[k] !== snapshot[k])) snapshot = now
  return snapshot
}

/** The browser's sound settings, live: a change in Settings or the player's menu reaches the player straight away. */
export function useSoundSettings(): SoundSettings {
  return useSyncExternalStore(subscribe, getSnapshot, () => defaultSoundSettings)
}

// The minute, as a store: read fresh whenever the player renders, and a render each minute while a schedule runs.
const subscribeMinute = (notify: () => void) => {
  const timer = window.setInterval(notify, 60_000)
  return () => window.clearInterval(timer)
}
const currentMinute = () => Math.floor(Date.now() / 60_000)
const noMinute = () => () => {}

/** What the settings mean right now; a scheduled night mode is looked at again every minute so it starts and stops mid-film. */
export function useSoundShaping(settings: SoundSettings): SoundShaping {
  const minute = useSyncExternalStore(settings.nightMode === 'Scheduled' ? subscribeMinute : noMinute, currentMinute, currentMinute)
  const { channels, dialogueDb, night } = shapingFor(settings, new Date(minute * 60_000))
  return useMemo(() => ({ channels, dialogueDb, night }), [channels, dialogueDb, night])
}
