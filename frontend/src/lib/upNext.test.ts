import { describe, expect, it } from 'vitest'
import shared from '../../../docs/next-episode-timing.json'
import { countdownLeft, nextEpisodeTiming } from './upNext'

// The shared cases every player tests against (docs/next-episode-timing.md), so the platforms can't drift.
describe('nextEpisodeTiming', () => {
  for (const c of shared.cases) {
    it(c.name, () => {
      const timing = nextEpisodeTiming({ credits: c.credits ?? undefined, duration: c.duration, skipCredits: c.skipCredits, autoPlayNext: c.autoPlayNext })
      expect(timing).toEqual(c.expect)
    })
  }
})

describe('countdownLeft', () => {
  it('follows the position, 10 down to 0', () => {
    expect(countdownLeft(1402, 1392)).toBe(10)
    expect(countdownLeft(1402, 1396.5)).toBe(6)
    expect(countdownLeft(1402, 1402)).toBe(0)
    expect(countdownLeft(1402, 1410)).toBe(0)
    expect(countdownLeft(1402, 1380)).toBe(10)
  })
})
