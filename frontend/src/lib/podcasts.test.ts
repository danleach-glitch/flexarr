import { describe, expect, it } from 'vitest'
import { episodeDate, episodeLength, episodeProgress, looksLikeFeedUrl } from './podcasts'

const user = (positionSeconds: number, watched = false) => ({ positionSeconds, watched, playCount: 0, lastPlayedAt: null, unwatchedCount: null })

describe('podcast episodes', () => {
  it('say how long they are, or how much is left', () => {
    expect(episodeLength({ durationSeconds: 3900, userData: user(0) })).toBe('1h 5m')
    expect(episodeLength({ durationSeconds: 3900, userData: user(2100) })).toBe('30m left')
    expect(episodeLength({ durationSeconds: 3900, userData: user(3890) })).toBe('1m left')
    expect(episodeLength({ durationSeconds: 3900, userData: user(0, true) })).toBe('Played')
    expect(episodeLength({ durationSeconds: null, userData: user(0) })).toBe('')
  })

  it('show progress only part way through', () => {
    expect(episodeProgress({ durationSeconds: 100, userData: user(25) })).toBe(0.25)
    expect(episodeProgress({ durationSeconds: 100, userData: user(0) })).toBeNull()
    expect(episodeProgress({ durationSeconds: 100, userData: user(25, true) })).toBeNull()
  })

  it('are dated as podcast apps date them', () => {
    const now = new Date(2026, 9, 25, 12)
    expect(episodeDate(new Date(2026, 9, 25, 6).toISOString(), now)).toBe('Today')
    expect(episodeDate(new Date(2026, 9, 24, 23).toISOString(), now)).toBe('Yesterday')
    expect(episodeDate(new Date(2026, 9, 21).toISOString(), now)).toBe(new Date(2026, 9, 21).toLocaleDateString(undefined, { weekday: 'long' }))
    expect(episodeDate(new Date(2025, 2, 3).toISOString(), now)).toBe(new Date(2025, 2, 3).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }))
    expect(episodeDate(null, now)).toBe('')
  })

  it('tell a feed address from words to search for', () => {
    expect(looksLikeFeedUrl('https://feeds.megaphone.fm/GLT4787413333')).toBe(true)
    expect(looksLikeFeedUrl(' http://example.com/rss ')).toBe(true)
    expect(looksLikeFeedUrl('the rest is history')).toBe(false)
    expect(looksLikeFeedUrl('https://')).toBe(false)
  })
})
