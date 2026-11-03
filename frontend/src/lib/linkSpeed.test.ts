import { beforeEach, describe, expect, it } from 'vitest'
import { linkSpeed, MIN_SAMPLE_BYTES, resetLinkSpeed, sample } from './linkSpeed'

describe('link speed', () => {
  beforeEach(() => resetLinkSpeed())

  it('is the median of recent big answers, in bits per second', () => {
    expect(linkSpeed()).toBeUndefined()
    sample(1_000_000, 1000, 0) // 8 Mbps
    sample(1_000_000, 500, 1) // 16 Mbps
    sample(1_000_000, 2000, 2) // 4 Mbps
    expect(linkSpeed(3)).toBe(8_000_000)
  })

  it('ignores small answers and forgets old ones', () => {
    sample(MIN_SAMPLE_BYTES - 1, 10, 0)
    expect(linkSpeed(1)).toBeUndefined()
    sample(1_000_000, 1000, 0)
    expect(linkSpeed(31 * 60_000)).toBeUndefined()
  })
})
