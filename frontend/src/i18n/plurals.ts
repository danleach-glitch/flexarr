// Which plural form a number takes in a language, from CLDR's rules (plurals.generated.ts, written by `make strings`).
// The operands are computed exactly as scripts/i18n/plurals.py does, so every platform picks the same form.
import { type Operands, type PluralCategory, pluralRules } from './plurals.generated'

export type { PluralCategory }

export function operands(x: number): Operands {
  const n = Math.abs(x)
  const s = String(n)
  if (Number.isInteger(n) || /e/i.test(s)) return { n, i: Math.trunc(n), v: 0, w: 0, f: 0, t: 0, e: 0 }
  const [whole, frac] = s.split('.')
  const stripped = frac.replace(/0+$/, '')
  return { n, i: Number(whole), v: frac.length, w: stripped.length, f: Number(frac), t: Number(stripped || '0'), e: 0 }
}

/** The CLDR locale a BCP 47 code uses: pt-PT, then pt, then und (everything is 'other'). */
export function pluralLocale(code: string): string {
  let c = code.replace('_', '-')
  for (;;) {
    if (c in pluralRules) return c
    const cut = c.lastIndexOf('-')
    if (cut < 0) return 'und'
    c = c.slice(0, cut)
  }
}

export function pluralCategory(code: string, x: number): PluralCategory {
  return pluralRules[pluralLocale(code)](operands(x))
}
