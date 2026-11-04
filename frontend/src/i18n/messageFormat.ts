// The ICU MessageFormat subset Flexarr's messages use (docs/languages.md): text, {value}, {value, number},
// {value, plural, …} with # and =N, {value, select, …}, and ICU's apostrophe quoting. scripts/i18n/icu.py is the
// reference; languages/tests/format-cases.json keeps the two in step.
import { pluralCategory } from './plurals'

export type Part = string | { pound: true } | Arg
export interface Arg {
  name: string
  kind: 'simple' | 'number' | 'plural' | 'select'
  options: Record<string, Part[]>
}

export class MessageError extends Error {}

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/
const SELECTOR = /^(=\d+|[A-Za-z_][A-Za-z0-9_-]*)$/

export function parse(message: string): Part[] {
  const at = { pos: 0 }
  const parts = parseParts(message, at, false, false)
  if (at.pos !== message.length) throw new MessageError(`unexpected '}' at ${at.pos}`)
  return parts
}

function parseParts(s: string, at: { pos: number }, inPlural: boolean, nested: boolean): Part[] {
  const parts: Part[] = []
  let text = ''
  const flush = () => {
    if (text) parts.push(text)
    text = ''
  }
  while (at.pos < s.length) {
    const c = s[at.pos]
    if (c === "'") {
      const next = s[at.pos + 1]
      if (next === "'") {
        text += "'"
        at.pos += 2
        continue
      }
      if (next === '{' || next === '}' || (inPlural && next === '#')) {
        let end = at.pos + 1
        for (;;) {
          if (end >= s.length) throw new MessageError('unterminated quote')
          if (s[end] === "'") {
            if (s[end + 1] === "'") {
              text += "'"
              end += 2
              continue
            }
            break
          }
          text += s[end]
          end++
        }
        at.pos = end + 1
        continue
      }
      text += "'"
      at.pos++
    } else if (c === '{') {
      flush()
      at.pos++
      parts.push(parseArg(s, at))
    } else if (c === '}') {
      if (!nested) throw new MessageError(`unexpected '}' at ${at.pos}`)
      break
    } else if (c === '#' && inPlural) {
      flush()
      parts.push({ pound: true })
      at.pos++
    } else {
      text += c
      at.pos++
    }
  }
  flush()
  return parts
}

function skipSpace(s: string, at: { pos: number }) {
  while (at.pos < s.length && /\s/.test(s[at.pos])) at.pos++
}

function word(s: string, at: { pos: number }): string {
  const start = at.pos
  while (at.pos < s.length && /[\p{L}\p{N}_=-]/u.test(s[at.pos])) at.pos++
  return s.slice(start, at.pos)
}

function parseArg(s: string, at: { pos: number }): Arg {
  skipSpace(s, at)
  const name = word(s, at)
  if (!NAME.test(name)) throw new MessageError(`bad value name '${name}'`)
  skipSpace(s, at)
  if (s[at.pos] === '}') {
    at.pos++
    return { name, kind: 'simple', options: {} }
  }
  if (s[at.pos] !== ',') throw new MessageError(`expected ',' or '}' after {${name}`)
  at.pos++
  skipSpace(s, at)
  const kind = word(s, at)
  skipSpace(s, at)
  if (kind === 'number') {
    if (s[at.pos] === '}') {
      at.pos++
      return { name, kind: 'number', options: {} }
    }
    throw new MessageError(`{${name}, number} takes no style`)
  }
  if (kind !== 'plural' && kind !== 'select') throw new MessageError(`unsupported type '${kind}' in {${name}}`)
  if (s[at.pos] !== ',') throw new MessageError(`expected ',' after {${name}, ${kind}`)
  at.pos++
  const options: Record<string, Part[]> = {}
  for (;;) {
    skipSpace(s, at)
    if (s[at.pos] === '}') {
      at.pos++
      break
    }
    const sel = word(s, at)
    if (!SELECTOR.test(sel) || (kind === 'select' && sel.startsWith('='))) throw new MessageError(`bad selector '${sel}' in {${name}}`)
    if (sel in options) throw new MessageError(`selector '${sel}' twice in {${name}}`)
    skipSpace(s, at)
    if (s[at.pos] !== '{') throw new MessageError(`expected '{' after '${sel}' in {${name}}`)
    at.pos++
    const sub = parseParts(s, at, kind === 'plural', true)
    if (s[at.pos] !== '}') throw new MessageError(`unclosed option '${sel}' in {${name}}`)
    at.pos++
    options[sel] = sub
  }
  if (!('other' in options)) throw new MessageError(`{${name}, ${kind}} needs an 'other'`)
  return { name, kind, options }
}

const numberFormats = new Map<string, Intl.NumberFormat>()

function formatNumber(n: number, locale: string): string {
  const intlLocale = locale === 'en-XA' ? 'en' : locale
  let f = numberFormats.get(intlLocale)
  if (!f) {
    try {
      f = new Intl.NumberFormat(intlLocale, { maximumFractionDigits: 3 })
    } catch {
      f = new Intl.NumberFormat('en', { maximumFractionDigits: 3 })
    }
    numberFormats.set(intlLocale, f)
  }
  return f.format(n)
}

/** Each part with its values filled in: text, a formatted value, or (with rich values) whatever was passed. */
export function formatParts<T>(parts: Part[], locale: string, values: Record<string, unknown>, out: (string | T)[] = [], pound?: number): (string | T)[] {
  for (const p of parts) {
    if (typeof p === 'string') out.push(p)
    else if ('pound' in p) out.push(pound === undefined ? '#' : formatNumber(pound, locale))
    else if (!(p.name in values)) out.push(`{${p.name}}`)
    else {
      const v = values[p.name]
      if (p.kind === 'plural') {
        const n = Number(v)
        const exact = Number.isInteger(n) ? `=${n}` : undefined
        const chosen = (exact !== undefined && p.options[exact]) || p.options[pluralCategory(locale, n)] || p.options.other
        formatParts(chosen, locale, values, out, n)
      } else if (p.kind === 'select') {
        formatParts(p.options[String(v)] ?? p.options.other, locale, values, out, pound)
      } else if (typeof v === 'number') out.push(formatNumber(v, locale))
      else if (typeof v === 'string' || v === null || v === undefined || typeof v === 'boolean') out.push(String(v ?? ''))
      else out.push(v as T)
    }
  }
  return out
}

export function format(parts: Part[], locale: string, values: Record<string, unknown> = {}): string {
  return formatParts<string>(parts, locale, values).join('')
}
