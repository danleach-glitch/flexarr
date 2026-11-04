import { describe, expect, it } from 'vitest'
import { format, parse } from './messageFormat'

interface Cases {
  format: { locale: string; message: string; args: Record<string, unknown>; expect: string }[]
  errors: string[]
}

// Outside frontend/ (shared with every platform's tests); a glob, so a build that copies only frontend/ still compiles.
const found = import.meta.glob('../../../languages/tests/format-cases.json', { eager: true, import: 'default' })
const cases = Object.values(found)[0] as Cases

describe('the shared formatter cases', () => {
  it('are there', () => expect(cases.format.length).toBeGreaterThan(20))
  it.each(cases.format.map((c) => [c.message, c] as const))('formats %s', (_, c) => {
    expect(format(parse(c.message), c.locale, c.args)).toBe(c.expect)
  })
  it.each(cases.errors)('refuses %s', (m) => {
    expect(() => parse(m)).toThrow()
  })
})
