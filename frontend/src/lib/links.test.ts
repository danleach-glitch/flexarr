import { describe, expect, it } from 'vitest'
import { webLink } from './links'

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
