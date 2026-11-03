import { describe, expect, it } from 'vitest'
import { browserMoved, clearEnding, endingFrom, isWatched, moveOn, recordEnding, whenBrowserBack } from './playbackEnding'

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

describe('browser Back from the player', () => {
  it("runs the player's handler before the router's popstate listener", () => {
    const order: string[] = []
    const router = () => order.push('router')
    const player = () => order.push('player')
    window.addEventListener('popstate', router)
    whenBrowserBack(player)
    window.dispatchEvent(new PopStateEvent('popstate'))
    window.removeEventListener('popstate', router)
    whenBrowserBack(undefined, player)
    expect(order).toEqual(['player', 'router'])
  })

  it('records the ending as the history moves, only while that player is open', () => {
    clearEnding()
    const onBack = () => recordEnding({ from: 'k1', startedWith: 'e2', stoppedOn: 'e2', watched: true, next: 'e3' })
    whenBrowserBack(onBack)
    browserMoved()
    expect(endingFrom('k1')?.next).toBe('e3')
    clearEnding()
    // Another player's close doesn't clear this one's handler.
    whenBrowserBack(undefined, () => undefined)
    browserMoved()
    expect(endingFrom('k1')).toBeDefined()
    clearEnding()
    whenBrowserBack(undefined, onBack)
    browserMoved()
    expect(endingFrom('k1')).toBeUndefined()
  })
})
