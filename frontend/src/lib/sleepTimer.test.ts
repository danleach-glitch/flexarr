import { describe, expect, it } from 'vitest'
import { extended, minutesLeft, secondsLeft, sleepChoices, sleepLabel } from './sleepTimer'

describe('sleep timer', () => {
  const now = 1_000_000

  it('counts down by the clock, or by what is left of the episode', () => {
    expect(secondsLeft({ until: now + 90_000 }, now, 600)).toBe(90)
    expect(secondsLeft({ endOfEpisode: true }, now, 42)).toBe(42)
    expect(secondsLeft(null, now, 42)).toBeNull()
  })

  it('shows whole minutes, never less than one while it runs', () => {
    expect(minutesLeft({ until: now + 90_000 }, now)).toBe(2)
    expect(minutesLeft({ until: now + 5_000 }, now)).toBe(1)
    expect(minutesLeft({ endOfEpisode: true }, now)).toBeNull()
    expect(sleepLabel({ until: now + 600_000 }, now)).toBe('Sleep timer, 10 min left')
    expect(sleepLabel({ endOfEpisode: true }, now, 'film')).toBe('Sleep timer, at the end of this film')
    expect(sleepLabel(null, now)).toBe('Sleep timer')
  })

  it('adds 15 minutes to what is left, or from now once it has run out or was waiting for the end', () => {
    expect(extended({ until: now + 30_000 }, now)).toEqual({ until: now + 30_000 + 15 * 60_000 })
    expect(extended({ until: now - 1_000 }, now)).toEqual({ until: now + 15 * 60_000 })
    expect(extended({ endOfEpisode: true }, now)).toEqual({ until: now + 15 * 60_000 })
  })

  it('starts a chosen timer when it is chosen', () => {
    const choices = sleepChoices('episode')
    expect(choices.map((c) => c.label)).toEqual(['In 15 minutes', 'In 30 minutes', 'In 45 minutes', 'In 60 minutes', 'At the end of this episode'])
    const timer = choices[0].start()
    expect('until' in timer && timer.until - Date.now()).toBeGreaterThan(14 * 60_000)
    expect(choices[4].start()).toEqual({ endOfEpisode: true })
  })
})
