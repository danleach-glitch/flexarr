import { beforeEach, describe, expect, it } from 'vitest'
import { linkSpeed, MIN_SAMPLE_BYTES, resetLinkSpeed, sample } from './linkSpeed'

describe('link speed', () => {
  beforeEach(() => resetLinkSpeed())

  it('is the upper quartile of recent big segments, in bits per second, once there are a few', () => {
    expect(linkSpeed()).toBeUndefined()
    sample(1_000_000, 2000, 0) // 4 Mbps
    sample(1_000_000, 1000, 1) // 8 Mbps
    expect(linkSpeed(2)).toBeUndefined() // two say nothing
    sample(1_000_000, 500, 2) // 16 Mbps
    sample(1_000_000, 250, 3) // 32 Mbps
    expect(linkSpeed(4)).toBe(32_000_000)
  })

  it('ignores small answers and forgets old ones', () => {
    sample(MIN_SAMPLE_BYTES - 1, 10, 0)
    expect(linkSpeed(1)).toBeUndefined()
    for (let i = 0; i < 3; i++) sample(1_000_000, 1000, 0)
    expect(linkSpeed(31 * 60_000)).toBeUndefined()
  })
})
