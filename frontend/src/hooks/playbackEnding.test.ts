import { describe, expect, it } from 'vitest'
import { moveOn, recordEnding, takeEnding } from './playbackEnding'

describe('playback ending', () => {
  it('moves on past a finished episode, to where it stopped, or nowhere', () => {
    expect(moveOn({ startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' })).toBe('e3')
    expect(moveOn({ startedWith: 'e2', stoppedOn: 'e4', watched: false, next: 'e5' })).toBe('e4')
    expect(moveOn({ startedWith: 'e2', stoppedOn: 'e2', watched: false, next: 'e3' })).toBeUndefined()
    expect(moveOn({ startedWith: 'e9', stoppedOn: 'e9', watched: true, next: null })).toBeUndefined()
  })

  it('is taken once, only by the page it was started from', () => {
    recordEnding({ startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' })
    expect(takeEnding('series')).toBeUndefined()
    expect(takeEnding('e2')?.next).toBe('e3')
    expect(takeEnding('e2')).toBeUndefined()
  })
})
