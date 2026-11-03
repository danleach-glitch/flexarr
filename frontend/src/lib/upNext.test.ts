import { describe, expect, it } from 'vitest'
import { countdownLeft, nextEpisodeTiming } from './upNext'

const duration = 1500
const at = (credits: number | undefined, skipCredits: boolean, autoPlayNext: boolean, length = duration) =>
  nextEpisodeTiming({ credits, duration: length, skipCredits, autoPlayNext })

describe('nextEpisodeTiming', () => {
  it('credits, Skip credits on, auto-play on: shows 8s before the credits and plays 2s into them', () => {
    expect(at(1400, true, true)).toEqual({ showAt: 1392, countdownFrom: 1392, nextAt: 1402 })
  })

  it('credits, Skip credits off, auto-play on: shows at the credits and counts down the last 10s', () => {
    expect(at(1400, false, true)).toEqual({ showAt: 1400, countdownFrom: 1490, nextAt: 1500 })
  })

  it('credits, Skip credits on, auto-play off: shows at the credits and closes there', () => {
    expect(at(1400, true, false)).toEqual({ showAt: 1400, closeAt: 1400 })
  })

  it('credits, Skip credits off, auto-play off: shows at the credits and closes at the end', () => {
    expect(at(1400, false, false)).toEqual({ showAt: 1400, closeAt: 1500 })
  })

  it('no credits, auto-play on: shows a minute before the end and counts down the last 10s, either way', () => {
    for (const skip of [true, false]) expect(at(undefined, skip, true)).toEqual({ showAt: 1440, countdownFrom: 1490, nextAt: 1500 })
  })

  it('no credits, auto-play off: shows a minute before the end and closes at the end, either way', () => {
    for (const skip of [true, false]) expect(at(undefined, skip, false)).toEqual({ showAt: 1440, closeAt: 1500 })
  })

  it('credits under 8s in still end the countdown 2s into them', () => {
    expect(at(5, true, true)).toEqual({ showAt: 0, countdownFrom: 0, nextAt: 7 })
  })

  it('credits in the last seconds count down no further than the end', () => {
    expect(at(1499, true, true)).toEqual({ showAt: 1491, countdownFrom: 1491, nextAt: 1500 })
    expect(at(1495, false, true)).toEqual({ showAt: 1490, countdownFrom: 1490, nextAt: 1500 })
  })

  it('an episode of 2 minutes or less with no credits shows no button before the end', () => {
    expect(at(undefined, false, true, 120)).toBeNull()
    expect(at(undefined, true, false, 90)).toBeNull()
    expect(at(undefined, false, true, 121)).toEqual({ showAt: 61, countdownFrom: 111, nextAt: 121 })
  })

  it('a short episode with credits follows them', () => {
    expect(at(100, true, true, 110)).toEqual({ showAt: 92, countdownFrom: 92, nextAt: 102 })
  })

  it('has nothing while the length is unknown', () => {
    expect(at(undefined, false, true, 0)).toBeNull()
  })
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
