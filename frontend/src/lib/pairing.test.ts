import { describe, expect, it } from 'vitest'
import { deviceCode } from './pairing'

describe('deviceCode', () => {
  it.each([
    ['as the server writes it', 'K7M-Q4X', 'K7M-Q4X'],
    ['without the dash', 'K7MQ4X', 'K7MQ4X'],
    ['in small letters', 'k7m-q4x', 'k7m-q4x'],
    ['with space around it', '  K7M-Q4X\n', 'K7M-Q4X'],
  ])('takes a code %s', (_what, text, code) => {
    expect(deviceCode(text)).toBe(code)
  })

  it.each([
    ['nothing', null],
    ['nothing at all', undefined],
    ['an empty value', ''],
    ['one character short', 'K7M-Q4'],
    ['one character over', 'K7M-Q4XZ'],
    ['a dash in the wrong place', 'K7-MQ4X'],
    ['two dashes', 'K7M--Q4X'],
    ['a space for the dash', 'K7M Q4X'],
    ['a code with more after it on another line', 'K7M-Q4X\nCall 0800 123 456'],
    ['a code with more before it', 'Call 0800 123 456\nK7M-Q4X'],
    ['letters and digits from other alphabets', 'КОД-١٢٣'],
    ['markup', '<b>K7M</b>'],
    ['a sentence', 'Call 0800 123 456 to finish setting up'],
  ])('takes no code from %s', (_what, text) => {
    expect(deviceCode(text)).toBeNull()
  })
})
