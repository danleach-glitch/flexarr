import { afterEach, describe, expect, it } from 'vitest'
import { forgetPlayed, keepsPlayed, profileKey, startKeepingPlayed, storedKeys } from './profileScope'

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
    // Found not signed in, this page may be signed in again and carry on: it still keeps what's played.
    expect(keepsPlayed()).toBe(true)
  })

  it('keeps nothing more on a page that signed out, and the next page in the tab forgets what that one left as it went', () => {
    forgetPlayed({ signedOut: true })
    expect(keepsPlayed()).toBe(false)

    // Written all the same on the way out (by something that didn't ask), before the next page starts.
    localStorage.setItem('flexarr.bookPlace.book-1', '{"position":600,"savedAt":1}')
    sessionStorage.setItem('flexarr.musicQueue', '{"ids":["t1"]}')
    startKeepingPlayed()

    expect(Object.keys(localStorage)).toEqual([])
    expect(Object.keys(sessionStorage)).toEqual([])
    expect(keepsPlayed()).toBe(true)

    // A page that follows no sign-out leaves everything as it is.
    localStorage.setItem('flexarr.bookPlace.book-1', '{"position":600,"savedAt":1}')
    startKeepingPlayed()
    expect(Object.keys(localStorage)).toEqual(['flexarr.bookPlace.book-1'])
  })
})
