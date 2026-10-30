import { describe, expect, it } from 'vitest'
import { unmarkedCreditsSeconds } from './upNext'

describe('unmarkedCreditsSeconds', () => {
  it('is 3% of the episode, between 30 seconds and 2 minutes', () => {
    expect(unmarkedCreditsSeconds(2831)).toBeCloseTo(84.93)
    expect(unmarkedCreditsSeconds(600)).toBe(30)
    expect(unmarkedCreditsSeconds(7200)).toBe(120)
  })
})
