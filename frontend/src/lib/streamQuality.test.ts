import { describe, expect, it } from 'vitest'
import { fits, heightClass, isAway, isPrivateHost, rememberedQuality, rememberQuality, qualitiesFor, requestFor, resolutionName, SMOOTH_MS, stallCounts, stepBelow, stepDownDue, stepUp } from './streamQuality'

function memory() {
  const kept = new Map<string, string>()
  return {
    getItem: (k: string) => kept.get(k) ?? null,
    setItem: (k: string, v: string) => void kept.set(k, v),
    removeItem: (k: string) => void kept.delete(k),
  }
}

describe('stream quality', () => {
  it('asks for each choice as the doc says', () => {
    expect(requestFor('auto')).toEqual({})
    expect(requestFor('original')).toEqual({ original: true })
    expect(requestFor(1080)).toEqual({ maxHeight: 1080, maxBitrate: 8_000_000, forceTranscode: true })
    expect(requestFor(480)).toEqual({ maxHeight: 480, maxBitrate: 1_500_000, forceTranscode: true })
  })

  it('ignores stalls in the first 3 seconds after a start or seek', () => {
    expect(stallCounts(2999, 0)).toBe(false)
    expect(stallCounts(3000, 0)).toBe(true)
  })

  it('steps down after two stalls within a minute, or one over 10 seconds', () => {
    expect(stepDownDue([10_000], 11_000)).toBe(false)
    expect(stepDownDue([10_000, 80_000], 80_000)).toBe(false)
    expect(stepDownDue([10_000, 70_000], 70_000)).toBe(true)
    expect(stepDownDue([], 20_000, 10_000)).toBe(false)
    expect(stepDownDue([], 20_001, 10_000)).toBe(true)
  })

  it('steps to the first quality below what plays', () => {
    expect(stepBelow({ height: 2160, bitrate: 40_000_000 })).toBe(1080)
    expect(stepBelow({ height: 1080, bitrate: 6_000_000 })).toBe(720)
    expect(stepBelow({ bitrate: 3_000_000 })).toBe(480)
    expect(stepBelow({ height: 720 })).toBe(480)
    expect(stepBelow({ height: 480, bitrate: 1_500_000 })).toBeNull()
    expect(stepBelow({})).toBe(720)
  })

  it('steps back up after 5 smooth minutes when the speed fits the next quality', () => {
    expect(fits(16_000_000, 8_000_000)).toBe(true)
    expect(fits(15_000_000, 8_000_000)).toBe(false)
    const calm = { now: SMOOTH_MS, lastStall: 0, lastChange: 0 }
    expect(stepUp(720, { ...calm, now: SMOOTH_MS - 1, speed: 100_000_000 })).toBeNull()
    expect(stepUp(720, { ...calm, lastChange: 1, speed: 100_000_000 })).toBeNull()
    expect(stepUp(720, calm)).toBeNull()
    expect(stepUp(720, { ...calm, speed: 16_000_000 })).toBe(1080)
    expect(stepUp(720, { ...calm, speed: 15_000_000 })).toBeNull()
    expect(stepUp(480, { ...calm, speed: 8_000_000 })).toBe(720)
    expect(stepUp(1080, { ...calm, speed: 50_000_000 })).toBeNull()
    expect(stepUp(1080, { ...calm, speed: 50_000_000, fileBitrate: 20_000_000 })).toBe('auto')
  })

  it('tells home from away', () => {
    for (const h of ['localhost', '127.0.0.1', '192.168.1.106', '10.0.0.5', '172.20.1.1', 'mini.local', '[::1]']) expect(isPrivateHost(h)).toBe(true)
    for (const h of ['100.101.102.103', 'mini.tail1234.ts.net', 'flexarr.example.com', '172.32.0.1']) expect(isPrivateHost(h)).toBe(false)
    expect(isAway('Local', 'flexarr.example.com')).toBe(false)
    expect(isAway('Tailnet', '192.168.1.106')).toBe(true)
    expect(isAway(undefined, 'flexarr.example.com')).toBe(true)
  })

  it('remembers a pick for plays away from home only', () => {
    const store = memory()
    rememberQuality(720, false, store)
    expect(rememberedQuality(true, store)).toBe('auto')
    rememberQuality(720, true, store)
    expect(rememberedQuality(true, store)).toBe(720)
    expect(rememberedQuality(false, store)).toBe('auto')
    rememberQuality('original', true, store)
    expect(rememberedQuality(true, store)).toBe('original')
    rememberQuality('auto', true, store)
    expect(rememberedQuality(true, store)).toBe('auto')
  })

  it('names a picture by its class, wide films included', () => {
    expect(resolutionName(3840, 2160)).toBe('4K')
    expect(resolutionName(3840, 1600)).toBe('4K')
    expect(resolutionName(1920, 800)).toBe('1080p')
    expect(resolutionName(1280, 534)).toBe('720p')
    expect(resolutionName(720, 480)).toBe('480p')
    expect(resolutionName(640, 360)).toBe('360p')
    expect(resolutionName(null, null)).toBeNull()
    expect(heightClass(1920, 1080)).toBe(1080)
  })

  it('offers only the caps below the file', () => {
    expect(qualitiesFor(720)).toEqual(['auto', 'original', 480])
    expect(qualitiesFor(1080)).toEqual(['auto', 'original', 720, 480])
    expect(qualitiesFor(2160)).toEqual(['auto', 'original', 1080, 720, 480])
    expect(qualitiesFor(undefined)).toEqual(['auto', 'original', 1080, 720, 480])
  })
})
