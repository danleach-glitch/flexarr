import { describe, expect, it } from 'vitest'
import type { BookChapter, BookProgress } from '../api/types'
import {
  chapterEnd,
  chapterIndexAt,
  clampSpeed,
  deviceName,
  ELSEWHERE_GRACE_MS,
  fadeLevel,
  fileAt,
  formatSpeed,
  nextChapterStart,
  parseClock,
  previousChapterStart,
  sleepReached,
  smartRewindSeconds,
  startDecision,
  stepSpeed,
  timelineAt,
} from './books'

// Three files: 0–100, 100–250, 250–300.
const files = [
  { offsetSeconds: 0, durationSeconds: 100 },
  { offsetSeconds: 100, durationSeconds: 150 },
  { offsetSeconds: 250, durationSeconds: 50 },
]

const chapters: BookChapter[] = [
  { index: 0, title: 'One', startSeconds: 0, endSeconds: 60 },
  { index: 1, title: 'Two', startSeconds: 60, endSeconds: 180 },
  { index: 2, title: 'Three', startSeconds: 180, endSeconds: 300 },
]

describe('fileAt', () => {
  it('finds the file and the place in it', () => {
    expect(fileAt(files, 0)).toEqual({ index: 0, offset: 0 })
    expect(fileAt(files, 42.5)).toEqual({ index: 0, offset: 42.5 })
    expect(fileAt(files, 180)).toEqual({ index: 1, offset: 80 })
  })

  it('puts a place exactly on a boundary at the start of the next file', () => {
    expect(fileAt(files, 100)).toEqual({ index: 1, offset: 0 })
    expect(fileAt(files, 250)).toEqual({ index: 2, offset: 0 })
    expect(fileAt(files, 99.999)).toEqual({ index: 0, offset: 99.999 })
  })

  it('keeps places before the start and past the end inside the book', () => {
    expect(fileAt(files, -5)).toEqual({ index: 0, offset: 0 })
    expect(fileAt(files, 400)).toEqual({ index: 2, offset: 50 })
  })

  it('copes with no files and with one', () => {
    expect(fileAt([], 10)).toEqual({ index: 0, offset: 0 })
    expect(fileAt([{ offsetSeconds: 0, durationSeconds: 30 }], 12)).toEqual({ index: 0, offset: 12 })
  })

  it('round-trips with timelineAt', () => {
    for (const at of [0, 1, 99.5, 100, 101, 249.9, 250, 299]) {
      const { index, offset } = fileAt(files, at)
      expect(timelineAt(files, index, offset)).toBeCloseTo(at)
    }
  })
})

describe('chapters', () => {
  it('finds the chapter playing', () => {
    expect(chapterIndexAt(chapters, 0)).toBe(0)
    expect(chapterIndexAt(chapters, 59.9)).toBe(0)
    expect(chapterIndexAt(chapters, 60)).toBe(1)
    expect(chapterIndexAt(chapters, 299)).toBe(2)
    expect(chapterIndexAt([], 10)).toBe(-1)
    expect(chapterIndexAt([{ startSeconds: 5 }], 2)).toBe(-1)
  })

  it('knows where a chapter ends, even without an end of its own', () => {
    expect(chapterEnd(chapters, 1, 300)).toBe(180)
    const open = chapters.map((c) => ({ ...c, endSeconds: 0 }))
    expect(chapterEnd(open, 0, 300)).toBe(60)
    expect(chapterEnd(open, 2, 300)).toBe(300)
  })

  it('goes back to the start of the chapter, or to the one before when it has only just begun', () => {
    expect(previousChapterStart(chapters, 100)).toBe(60)
    expect(previousChapterStart(chapters, 61)).toBe(0)
    expect(previousChapterStart(chapters, 2)).toBe(0)
  })

  it('goes on to the next chapter, and nowhere from the last', () => {
    expect(nextChapterStart(chapters, 10)).toBe(60)
    expect(nextChapterStart(chapters, 200)).toBeNull()
  })
})

describe('smartRewindSeconds', () => {
  it('does nothing for a short pause', () => {
    expect(smartRewindSeconds(0)).toBe(0)
    expect(smartRewindSeconds(9.9)).toBe(0)
  })

  it('grows with the pause, to 30 seconds after an hour', () => {
    expect(smartRewindSeconds(10)).toBe(3)
    const minute = smartRewindSeconds(60)
    const tenMinutes = smartRewindSeconds(600)
    expect(minute).toBeGreaterThan(3)
    expect(tenMinutes).toBeGreaterThan(minute)
    expect(smartRewindSeconds(3600)).toBe(30)
    expect(smartRewindSeconds(86_400)).toBe(30)
  })
})

describe('speed', () => {
  it('keeps to 0.5× to 3.5× in 0.05 steps', () => {
    expect(clampSpeed(1.23)).toBe(1.25)
    expect(clampSpeed(0.1)).toBe(0.5)
    expect(clampSpeed(9)).toBe(3.5)
    expect(clampSpeed(Number.NaN)).toBe(1)
  })

  it('steps by 0.05 without drifting, and stops at the ends', () => {
    let speed = 1
    for (let i = 0; i < 10; i++) speed = stepSpeed(speed, 1)
    expect(speed).toBe(1.5)
    expect(stepSpeed(3.5, 1)).toBe(3.5)
    expect(stepSpeed(0.5, -1)).toBe(0.5)
    expect(stepSpeed(1.05, -1)).toBe(1)
  })

  it('says the speed', () => {
    expect(formatSpeed(1)).toBe('1×')
    expect(formatSpeed(1.25)).toBe('1.25×')
    expect(formatSpeed(1.5)).toBe('1.5×')
  })
})

describe('the sleep timer at the end of the chapter', () => {
  it('stops once the chapter ends', () => {
    const end = chapterEnd(chapters, chapterIndexAt(chapters, 100), 300)
    expect(sleepReached(end, 170)).toBe(false)
    expect(sleepReached(end, 179.9)).toBe(true)
    expect(sleepReached(end, 185)).toBe(true)
  })

  it('fades over the last seconds', () => {
    expect(fadeLevel(30, 10)).toBe(1)
    expect(fadeLevel(5, 10)).toBe(0.5)
    expect(fadeLevel(0, 10)).toBe(0)
    expect(fadeLevel(-1, 10)).toBe(0)
  })
})

describe('startDecision', () => {
  const now = Date.parse('2026-10-31T20:00:00Z')
  const progress = (position: number, updatedAt: number | null, device = 'Dan’s iPhone', finished = false): BookProgress => ({
    positionSeconds: position,
    finished,
    finishedAt: null,
    updatedAt: updatedAt === null ? null : new Date(updatedAt).toISOString(),
    device,
  })

  it('starts where the server says when this browser has never played it', () => {
    expect(startDecision(progress(500, now), null)).toEqual({ start: 500, offer: null })
  })

  it('offers another device’s newer place, starting from this browser’s', () => {
    const decision = startDecision(progress(900, now), { position: 500, savedAt: now - 10 * 60_000 })
    expect(decision).toEqual({ start: 500, offer: { position: 900, device: 'Dan’s iPhone' } })
  })

  it('doesn’t offer what this browser recorded itself, or a place only a little newer', () => {
    // The server's place is this browser's own last report (within the grace for clocks).
    expect(startDecision(progress(500, now), { position: 500, savedAt: now - ELSEWHERE_GRACE_MS / 2 })).toEqual({ start: 500, offer: null })
    // Newer, but close enough to where this browser was.
    expect(startDecision(progress(510, now), { position: 500, savedAt: now - 10 * 60_000 })).toEqual({ start: 510, offer: null })
  })

  it('keeps this browser’s place when the server’s is older', () => {
    expect(startDecision(progress(300, now - 60 * 60_000), { position: 500, savedAt: now })).toEqual({ start: 500, offer: null })
  })

  it('starts a finished book over, and a place asked for wins', () => {
    expect(startDecision(progress(0, now, 'x', true), { position: 500, savedAt: now - 10 * 60_000 })).toEqual({ start: 0, offer: null })
    expect(startDecision(progress(900, now), { position: 500, savedAt: now - 10 * 60_000 }, 60)).toEqual({ start: 60, offer: null })
  })
})

describe('parseClock', () => {
  it('reads times as typed', () => {
    expect(parseClock('1:02:03')).toBe(3723)
    expect(parseClock('2:03')).toBe(123)
    expect(parseClock('90')).toBe(90)
    expect(parseClock(' 12.5 ')).toBe(12.5)
    expect(parseClock('1:0x')).toBeNull()
    expect(parseClock('')).toBeNull()
  })
})

describe('deviceName', () => {
  it('names the browser and the computer', () => {
    expect(deviceName('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15')).toBe('Safari on Mac')
    expect(deviceName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36')).toBe('Chrome on Windows')
    expect(deviceName('Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0')).toBe('Edge on Windows')
    expect(deviceName('Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0')).toBe('Firefox on Linux')
  })
})
