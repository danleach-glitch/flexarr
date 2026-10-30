import { describe, expect, it } from 'vitest'
import { defaultSoundSettings, isShaping, minutesOf, readSoundSettings, shapingFor, timeOfDay, writeSoundSettings } from './soundSettings'

const at = (h: number, m = 0) => new Date(2026, 9, 30, h, m)

describe('sound settings', () => {
  it('does nothing by default', () => {
    expect(isShaping(shapingFor(defaultSoundSettings, at(12)))).toBe(false)
  })

  it('folds to the speakers chosen, and lifts dialogue by the strength chosen', () => {
    const s = { ...defaultSoundSettings, speakers: 'Stereo' as const, dialogueBoost: 'Medium' as const }
    expect(shapingFor(s, at(12))).toEqual({ channels: 2, dialogueDb: 6, night: false })
    expect(shapingFor({ ...s, speakers: 'Surround' }, at(12)).channels).toBe(6)
  })

  it('runs a scheduled night mode across midnight', () => {
    const s = { ...defaultSoundSettings, nightMode: 'Scheduled' as const } // 22:00 to 07:00
    expect(shapingFor(s, at(21, 59)).night).toBe(false)
    expect(shapingFor(s, at(22)).night).toBe(true)
    expect(shapingFor(s, at(3)).night).toBe(true)
    expect(shapingFor(s, at(7)).night).toBe(false)
    expect(shapingFor({ ...s, nightStart: 13 * 60, nightEnd: 15 * 60 }, at(14)).night).toBe(true)
  })

  it('reads times as a time input gives them', () => {
    expect(timeOfDay(22 * 60 + 5)).toBe('22:05')
    expect(minutesOf('07:30')).toBe(450)
    expect(minutesOf('25:00')).toBeNull()
  })

  it('keeps what was chosen and ignores anything it does not know', () => {
    writeSoundSettings({ dialogueBoost: 'High' })
    expect(readSoundSettings().dialogueBoost).toBe('High')
    localStorage.setItem('flexarr.sound', JSON.stringify({ speakers: 'Cinema', nightStart: 99999 }))
    expect(readSoundSettings()).toEqual(defaultSoundSettings)
  })
})
