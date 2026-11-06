import { afterEach, describe, expect, it } from 'vitest'
import { forgetPlayed, profileKey, storedKeys } from './profileScope'

afterEach(() => {
  localStorage.clear()
  sessionStorage.clear()
})

describe('what this browser keeps for a profile', () => {
  it('is kept under the profile chosen here, and under the plain name for the main profile', () => {
    expect(profileKey('flexarr.musicQueue')).toBe('flexarr.musicQueue')
    localStorage.setItem('flexarr.profile', 'kid-1.signature')
    expect(profileKey('flexarr.musicQueue')).toBe('flexarr.musicQueue.p-kid-1')
  })

  it('lists every key under a name, however many are removed along the way', () => {
    for (const key of ['a.1', 'b.1', 'a.2', 'a.3']) localStorage.setItem(key, '1')
    const found = storedKeys(localStorage, 'a.')
    for (const key of found) localStorage.removeItem(key)
    expect(found.sort()).toEqual(['a.1', 'a.2', 'a.3'])
    expect(Object.keys(localStorage)).toEqual(['b.1'])
  })

  it('forgets every profile’s book places and queue at sign-out, and keeps this browser’s settings', () => {
    localStorage.setItem('flexarr.bookPlace.book-1', '{"position":600,"savedAt":1}')
    localStorage.setItem('flexarr.bookPlace.book-2.p-kid-1', '{"position":30,"savedAt":2}')
    localStorage.setItem('flexarr.language.choice', 'de')
    localStorage.setItem('flexarr.sound', '{}')
    localStorage.setItem('flexarr.crossfade', '4')
    sessionStorage.setItem('flexarr.musicQueue', '{"ids":["t1"]}')
    sessionStorage.setItem('flexarr.musicQueueTracks', '[]')
    sessionStorage.setItem('flexarr.musicQueue.p-kid-1', '{"ids":["t2"]}')
    sessionStorage.setItem('flexarr.musicQueueTracks.p-kid-1', '[]')
    sessionStorage.setItem('flexarr.pickProfile', '1')

    forgetPlayed()

    expect(Object.keys(localStorage).sort()).toEqual(['flexarr.crossfade', 'flexarr.language.choice', 'flexarr.sound'])
    expect(Object.keys(sessionStorage)).toEqual(['flexarr.pickProfile'])
  })
})
