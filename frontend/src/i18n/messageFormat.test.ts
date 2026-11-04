import { describe, expect, it } from 'vitest'
import cases from '../../../languages/tests/format-cases.json'
import { format, parse } from './messageFormat'

describe('the shared formatter cases', () => {
  it.each(cases.format.map((c) => [c.message, c] as const))('formats %s', (_, c) => {
    expect(format(parse(c.message), c.locale, c.args)).toBe(c.expect)
  })
  it.each(cases.errors)('refuses %s', (m) => {
    expect(() => parse(m)).toThrow()
  })
})
