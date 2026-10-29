import { describe, expect, it } from 'vitest'
import { reorderedLibraries } from './libraryOrder'

describe('reorderedLibraries', () => {
  it('keeps entries this app does not show in their places', () => {
    expect(reorderedLibraries(['a', 'live-tv', 'b', 'c'], ['c', 'a', 'b'])).toEqual(['c', 'live-tv', 'a', 'b'])
  })

  it('adds libraries the saved order has not met yet at the end', () => {
    expect(reorderedLibraries([], ['b', 'a'])).toEqual(['b', 'a'])
    expect(reorderedLibraries(['live-tv', 'a'], ['new', 'a'])).toEqual(['live-tv', 'new', 'a'])
  })
})
