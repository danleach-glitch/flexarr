import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { errorText } from './errorText'

describe('errorText', () => {
  it('keeps the server’s own words', () => {
    expect(errorText(new ApiError(400, 'Bad Request', ['That name is taken.', 'Pick another.']))).toBe('That name is taken. Pick another.')
    expect(errorText(new ApiError(500, 'Internal Server Error', ['The database is busy.']))).toBe('The database is busy.')
  })

  it('turns the browser’s network failures into words people can read', () => {
    for (const message of ['Failed to fetch', 'Load failed', 'NetworkError when attempting to fetch resource.']) {
      expect(errorText(new TypeError(message))).toBe('Flexarr didn’t answer. Try again.')
    }
  })

  it('says the server had a problem for a 5xx with no problem body', () => {
    expect(errorText(new ApiError(500, 'Internal Server Error', []))).toBe('Flexarr had a problem. Try again.')
    expect(errorText(new ApiError(502, 'Bad Gateway', []))).toBe('Flexarr didn’t answer. Try again.')
  })

  it('falls back to the error’s message', () => {
    expect(errorText(new Error('Something specific'))).toBe('Something specific')
    expect(errorText('plain')).toBe('plain')
  })
})
