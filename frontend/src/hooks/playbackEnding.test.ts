import { describe, expect, it } from 'vitest'
import { clearEnding, endingFrom, isWatched, moveOn, recordEnding } from './playbackEnding'

describe('playback ending', () => {
  it('moves on past a finished episode, to where it stopped, or nowhere', () => {
    expect(moveOn({ from: 'k', startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' }, 'e2')).toBe('e3')
    expect(moveOn({ from: 'k', startedWith: 'e2', stoppedOn: 'e4', watched: false, next: 'e5' }, 'e2')).toBe('e4')
    expect(moveOn({ from: 'k', startedWith: 'e2', stoppedOn: 'e2', watched: false, next: 'e3' }, 'e2')).toBeUndefined()
    expect(moveOn({ from: 'k', startedWith: 'e9', stoppedOn: 'e9', watched: true, next: null }, 'e9')).toBeUndefined()
  })

  it('only moves the page showing what was started', () => {
    expect(moveOn({ from: 'k', startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' }, 'series')).toBeUndefined()
  })

  it('belongs to the history entry Play was pressed on', () => {
    recordEnding({ from: 'k1', startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' })
    expect(endingFrom('k2')).toBeUndefined()
    expect(endingFrom('k1')?.next).toBe('e3')
    clearEnding()
    expect(endingFrom('k1')).toBeUndefined()
  })

  it('counts watched as the server does: credits or 92%, whichever first', () => {
    expect(isWatched(94, 100, 97)).toBe(true)
    expect(isWatched(85, 100, 84)).toBe(true)
    expect(isWatched(90, 100, undefined)).toBe(false)
    expect(isWatched(0, 0, undefined)).toBe(false)
  })
})
