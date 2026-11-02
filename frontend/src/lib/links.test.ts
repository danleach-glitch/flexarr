import { describe, expect, it } from 'vitest'
import { libraryGenreLink, webLink } from './links'

describe('webLink', () => {
  it('passes web addresses, and nothing that would run or go elsewhere', () => {
    expect(webLink('http://sonarr.lan:8989/series/foo')).toBe('http://sonarr.lan:8989/series/foo')
    expect(webLink('https://github.com/x/y/releases/tag/v1')).toBe('https://github.com/x/y/releases/tag/v1')
    expect(webLink('javascript:alert(1)')).toBeUndefined()
    expect(webLink('data:text/html,hi')).toBeUndefined()
    expect(webLink('not a url')).toBeUndefined()
    expect(webLink(null)).toBeUndefined()
  })
})

describe('libraryGenreLink', () => {
  it("opens each kind of library's own genre page", () => {
    expect(libraryGenreLink('m', 'Movies', 'Sci-Fi & Fantasy')).toBe('/library/m?view=genre&genre=Sci-Fi%20%26%20Fantasy')
    expect(libraryGenreLink('t', 'TvShows', 'Drama')).toBe('/library/t?view=genre&genre=Drama')
    expect(libraryGenreLink('a', 'Music', 'Rock')).toBe('/library/a?view=genre&genre=Rock')
    expect(libraryGenreLink('b', 'Audiobooks', 'Fantasy')).toBe('/library/b?view=genre&genre=Fantasy')
  })
})
