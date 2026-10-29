import { describe, expect, it } from 'vitest'
import { chapterAt, clockSeconds, episodeDate, episodeLength, episodeProgress, looksLikeFeedUrl, notesParts, previousChapterStart } from './podcasts'

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

describe('episode notes', () => {
  it('make web addresses links and times jumps', () => {
    expect(notesParts('Chapters:\n00:00 Intro\n1:02:55 Outro\nMore at https://example.com/show.')).toEqual([
      { text: 'Chapters:\n' },
      { time: 0, text: '00:00' },
      { text: ' Intro\n' },
      { time: 3775, text: '1:02:55' },
      { text: ' Outro\nMore at ' },
      { url: 'https://example.com/show.' },
    ])
  })

  it('leave alone times past the end, and longer runs of digits', () => {
    expect(notesParts('At 50:00 and 12:30:45:10', 600)).toEqual([{ text: 'At 50:00 and 12:30:45:10' }])
    expect(clockSeconds('12:75')).toBeNull()
  })
})

describe('chapters', () => {
  const chapters = [
    { startSeconds: 0, title: 'Intro' },
    { startSeconds: 230, title: 'A gift' },
    { startSeconds: 312, title: 'Food' },
  ]

  it('know which is playing', () => {
    expect(chapterAt(chapters, 100)).toBe(0)
    expect(chapterAt(chapters, 229.8)).toBe(1)
    expect(chapterAt([{ startSeconds: 60, title: 'Late' }], 10)).toBeNull()
  })

  it('go back to the start of this one, or the one before when just started', () => {
    expect(previousChapterStart(chapters, 280)).toBe(230)
    expect(previousChapterStart(chapters, 231)).toBe(0)
    expect(previousChapterStart(chapters, 1)).toBe(0)
  })
})
