import { describe, expect, it } from 'vitest'
import { ageLimitText, ratingLabel, ratingSteps } from './ratings'

describe('age limits', () => {
  it('names ages as the UK does', () => {
    expect([0, 8, 12, 15, 18].map((age) => ratingLabel(age))).toEqual(['U', 'PG', '12', '15', '18'])
  })

  it('describes a profile’s limit, and says nothing without one', () => {
    expect(ageLimitText({ maxRatingAge: 8, isKids: true })).toBe('Kids profile, up to PG')
    expect(ageLimitText({ maxRatingAge: 15 })).toBe('Up to 15')
    expect(ageLimitText({ maxRatingAge: null })).toBeNull()
    expect(ageLimitText({})).toBeNull()
  })

  it('goes by the US’s names on a US server, for the same ages', () => {
    expect([0, 8, 12, 15, 18].map((age) => ratingLabel(age, 'us'))).toEqual(['G', 'PG', 'PG-13', 'TV-14', 'R'])
    expect(ratingSteps('us').map((s) => s.age)).toEqual(ratingSteps('uk').map((s) => s.age))
    expect(ageLimitText({ maxRatingAge: 12, isKids: true }, 'us')).toBe('Kids profile, up to PG-13')
    expect(ageLimitText({ maxRatingAge: 15 }, 'us')).toBe('Up to TV-14')
  })
})
