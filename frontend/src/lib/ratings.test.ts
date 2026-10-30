import { describe, expect, it } from 'vitest'
import { ageLimitText, ratingLabel } from './ratings'

describe('age limits', () => {
  it('names ages as the UK does', () => {
    expect([0, 8, 12, 15, 18].map(ratingLabel)).toEqual(['U', 'PG', '12', '15', '18'])
  })

  it('describes a profile’s limit, and says nothing without one', () => {
    expect(ageLimitText({ maxRatingAge: 8, isKids: true })).toBe('Kids profile · up to PG')
    expect(ageLimitText({ maxRatingAge: 15 })).toBe('Up to 15')
    expect(ageLimitText({ maxRatingAge: null })).toBeNull()
    expect(ageLimitText({})).toBeNull()
  })
})
