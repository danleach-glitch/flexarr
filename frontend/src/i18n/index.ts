// Flexarr in the profile's language (docs/languages.md). Every string people read goes through t(), with the English
// as the key: t('Mark as watched'), t('Resume from {time}', { time }), t('Play', { ctx: 'music' }). `make strings`
// collects them into languages/packs/en.json, so the first argument is always a plain literal. A language's pack comes
// from this app's server (GET /api/v1/languages/{code}?scope=web), kept in localStorage so the next visit starts in it
// straight away; a string the pack lacks, or can't format, shows the English.
import { Fragment, createElement, type ReactNode } from 'react'
import { format, formatParts, parse, type Part } from './messageFormat'

export { Words } from './words.generated'

export interface LanguageInfo {
  code: string
  name: string
  direction: 'ltr' | 'rtl'
  version: number
  coverage?: Record<string, number>
}

interface Pack {
  code: string
  name: string
  direction: 'ltr' | 'rtl'
  version: number
  messages: Record<string, string>
}

type Values = Record<string, unknown> & { ctx?: string }

const STORE = 'flexarr.language.'
const ENGLISH: Pack = { code: 'en', name: 'English', direction: 'ltr', version: 0, messages: {} }

let pack: Pack = ENGLISH
const parsed = new Map<string, Part[] | null>()
const listeners = new Set<() => void>()

function parsedFor(key: string, message: string): Part[] | null {
  const k = `${pack.code}\u0000${key}\u0000${message}`
  let parts = parsed.get(k)
  if (parts === undefined) {
    try {
      parts = parse(message)
    } catch {
      parts = null
    }
    parsed.set(k, parts)
  }
  return parts
}

const CONTEXT_KEY = /^([a-z][a-z-]*)::([\s\S]*)$/

/** A shared word can carry its context in its key (music::Play): split it off. */
function split(english: string, ctx?: string): [string, string | undefined] {
  const m = ctx ? null : CONTEXT_KEY.exec(english)
  return m ? [m[2], m[1]] : [english, ctx]
}

function lookup(english: string, ctx?: string): { key: string; translated?: string } {
  const key = ctx ? `${ctx}::${english}` : english
  return { key, translated: pack.messages[key] }
}

/** A string in the current language. */
export function t(message: string, values?: Values): string {
  const { ctx: given, ...rest } = values ?? {}
  const [english, ctx] = split(message, given)
  const { key, translated } = lookup(english, ctx)
  if (translated !== undefined) {
    const parts = parsedFor(key, translated)
    if (parts) {
      try {
        return format(parts, pack.code, rest)
      } catch {
        // Falls back to the English.
      }
    }
  }
  const source = parsedFor(`en\u0000${key}`, english)
  return source ? format(source, 'en', rest) : english
}

/** A string with React nodes dropped into it: t.rich('Resume {title} from {time}', { title: <b>{title}</b>, time }). */
t.rich = function rich(message: string, values?: Record<string, ReactNode> & { ctx?: string }): ReactNode {
  const { ctx: given, ...rest } = values ?? {}
  const [english, ctx] = split(message, typeof given === 'string' ? given : undefined)
  const { key, translated } = lookup(english, ctx)
  let out: (string | ReactNode)[] | undefined
  const parts = translated !== undefined ? parsedFor(key, translated) : null
  if (parts) {
    try {
      out = formatParts<ReactNode>(parts, pack.code, rest)
    } catch {
      out = undefined
    }
  }
  if (!out) {
    const source = parsedFor(`en\u0000${key}`, english)
    out = source ? formatParts<ReactNode>(source, 'en', rest) : [english]
  }
  return createElement(Fragment, null, ...out.map((p, i) => (typeof p === 'string' ? p : createElement(Fragment, { key: i }, p))))
}

/** Marks a string for `make strings` without translating it, for tables translated later with t(value). */
export function tk<T extends string>(english: T): T {
  return english
}

/** The language shown now: its code (for Intl), name and direction. */
export function currentLanguage(): { code: string; direction: 'ltr' | 'rtl' } {
  return { code: pack.code, direction: pack.direction }
}

/**
 * The locale to hand Intl's formatters: the language shown, in the browser's own variant of it where it has one (so
 * English in a British browser writes 3 Mar 2025, and German in an Austrian one Jänner). The pseudo-language formats
 * as English.
 */
export function locale(): string {
  // Worked out once for the language shown and the browser's languages: a page asks this for every date it writes,
  // and asking Intl for the browser's own locale builds a formatter each time.
  const languages = typeof navigator === 'undefined' ? [] : (navigator.languages ?? [navigator.language])
  const key = `${pack.code} ${languages.join(' ')}`
  if (localeKept?.key === key) return localeKept.value
  const code = pack.code === 'en-XA' ? 'en' : pack.code
  const base = code.split('-')[0]
  const preferred = [new Intl.DateTimeFormat().resolvedOptions().locale, ...languages]
  const value = preferred.find((l) => l.split('-')[0].toLowerCase() === base && (code === base || l.toLowerCase() === code.toLowerCase())) ?? code
  localeKept = { key, value }
  return value
}

let localeKept: { key: string; value: string } | undefined

/** Most formatters kept at once: pages ask for a handful of shapes, so reaching this means something asks for ever-new ones. */
const MAX_DATE_FORMATS = 50
const dateFormats = new Map<string, Intl.DateTimeFormat>()

/**
 * A date and time formatter for the language shown, kept for the next time it's asked for: building one is slow, and
 * a TV guide writes thousands of times at once. Given options that name the parts wanted, its `format(date)` writes
 * what `date.toLocaleTimeString(locale(), options)` or `toLocaleDateString` does. A new one is built when the
 * language or the computer's time zone changes.
 */
export function dateFormat(options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `${locale()} ${new Date().getTimezoneOffset()} ${JSON.stringify(options)}`
  let format = dateFormats.get(key)
  if (!format) {
    if (dateFormats.size >= MAX_DATE_FORMATS) dateFormats.clear()
    format = new Intl.DateTimeFormat(locale(), options)
    dateFormats.set(key, format)
  }
  return format
}

export function onLanguageChange(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

function apply(next: Pack) {
  if (next.code === pack.code && next.version === pack.version) return
  pack = next
  parsed.clear()
  document.documentElement.lang = next.code
  document.documentElement.dir = next.direction
  for (const l of listeners) l()
}

function kept(code: string): (Pack & { etag?: string }) | null {
  try {
    const raw = localStorage.getItem(STORE + code)
    const p = raw ? (JSON.parse(raw) as Pack & { etag?: string }) : null
    // A damaged entry would break every word on every load: fetched afresh instead.
    return p && typeof p.messages === 'object' && p.messages !== null ? p : null
  } catch {
    return null
  }
}

function keep(p: Pack, etag: string | null) {
  try {
    // One kept pack is enough: the language this browser shows.
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const k = localStorage.key(i)
      if (k?.startsWith(STORE) && k !== STORE + p.code && k !== STORE + 'choice') localStorage.removeItem(k)
    }
    localStorage.setItem(STORE + p.code, JSON.stringify({ ...p, etag }))
  } catch {
    // Storage full or unavailable: it's fetched again next time.
  }
}

/** The languages this server offers (English first). Empty from servers before 0.61, which have none. */
export async function availableLanguages(signal?: AbortSignal): Promise<LanguageInfo[]> {
  return (await languagesOffered(signal)) ?? []
}

/** The languages this server offers, or null when it couldn't be asked (offline, or it didn't answer). */
async function languagesOffered(signal?: AbortSignal): Promise<LanguageInfo[] | null> {
  try {
    const response = await fetch('/api/v1/languages', { signal, headers: { 'X-Requested-With': 'flexarr' } })
    if (!response.ok) return []
    return ((await response.json()) as { languages: LanguageInfo[] }).languages
  } catch {
    return null
  }
}

/** The best of the browser's own languages among those offered: pt-BR, then pt; null for English or none. */
export function deviceLanguage(offered: string[], preferred: readonly string[] = navigator.languages ?? [navigator.language]): string | null {
  for (const tag of preferred) {
    const parts = tag.split(/[-_]/)
    for (let n = parts.length; n > 0; n--) {
      const code = normalise(parts.slice(0, n).join('-'))
      if (code === 'en') return null
      if (offered.includes(code)) return code
    }
  }
  return null
}

function normalise(tag: string): string {
  const [first, ...rest] = tag.split('-')
  return [first.toLowerCase(), ...rest.map((p) => (p.length === 4 ? p[0].toUpperCase() + p.slice(1).toLowerCase() : p.toUpperCase()))].join('-')
}

/**
 * Shows a language: English for null or 'en', else its pack from this server (the kept copy at once, then a fresh one
 * if the server has newer). Resolves once the newest pack it can get is showing; offline, the kept one stays.
 */
export function showLanguage(code: string | null, signal?: AbortSignal): Promise<void> {
  return load(code, signal, () => true)
}

/**
 * showLanguage, for someone who may have stopped waiting: once `wanted` says no, a pack that arrives is kept for the
 * next visit but nothing on screen changes.
 */
async function load(code: string | null, signal: AbortSignal | undefined, wanted: () => boolean): Promise<void> {
  if (!code || code === 'en') {
    apply(ENGLISH)
    return
  }
  const have = kept(code)
  if (have) apply(have)
  try {
    const response = await fetch(`/api/v1/languages/${encodeURIComponent(code)}?scope=web`, {
      signal,
      headers: { 'X-Requested-With': 'flexarr', ...(have?.etag ? { 'If-None-Match': have.etag } : {}) },
    })
    if (response.status === 304) return
    if (!response.ok) {
      if (response.status === 404 && wanted()) apply(ENGLISH)
      return
    }
    const fresh = (await response.json()) as Pack
    keep(fresh, response.headers.get('ETag'))
    if (wanted()) apply(fresh)
  } catch {
    // Offline or aborted: whatever's showing stays.
  }
}

/** The language this browser showed last time, when its pack is still kept here. */
function lastShown(): (Pack & { etag?: string }) | null {
  try {
    const last = localStorage.getItem(STORE + 'choice')
    return last ? kept(last) : null
  } catch {
    return null
  }
}

/** The language a browser starts in before it knows the profile: the one it showed last time, if any. */
export function startWithKeptLanguage() {
  const have = lastShown()
  if (have) apply(have)
}

/** Remembers which language this browser showed, for startWithKeptLanguage next time. */
export function rememberLanguage(code: string | null) {
  try {
    if (code && code !== 'en') localStorage.setItem(STORE + 'choice', code)
    else localStorage.removeItem(STORE + 'choice')
  } catch {
    // Storage unavailable.
  }
}

/**
 * Settles the language for this browser: the profile's choice, else the browser's best match among the server's
 * languages, else English. Used at start-up (before the first real screen) and when the choice changes.
 */
export async function settleLanguage(profileChoice: string | null | undefined, signal?: AbortSignal): Promise<LanguageInfo[]> {
  const offered = await availableLanguages(signal)
  const code = profileChoice ?? deviceLanguage(offered.map((l) => l.code))
  await showLanguage(code, signal)
  rememberLanguage(currentLanguage().code)
  return offered
}

/** How long start-up holds the first screen for a language request before going on with what it has. */
export const LANGUAGE_WAIT_MS = 5_000

/**
 * `load`, for a caller who waits `waitMs` at most: `shown` resolves when the language is showing or the wait is up,
 * whichever is first. The request carries on past the wait, so the server finishes fetching a pack and it's here next
 * time; `drop` ends it, for a language that turned out not to be wanted.
 */
function loadFor(waitMs: number, code: string | null, signal?: AbortSignal) {
  const own = new AbortController()
  const end = () => own.abort()
  if (signal?.aborted) end()
  else signal?.addEventListener('abort', end, { once: true })
  let waiting = true
  const shown = new Promise<void>((resolve) => {
    const giveUp = () => {
      waiting = false
      resolve()
    }
    const timer = setTimeout(giveUp, waitMs)
    void load(code, own.signal, () => waiting).then(() => {
      clearTimeout(timer)
      signal?.removeEventListener('abort', end)
      resolve()
    })
  })
  return {
    shown,
    drop() {
      waiting = false
      end()
    },
  }
}

/**
 * Start-up's language, asked for at once so it doesn't wait its turn behind who's signed in: the server's list, and
 * the newest pack of the language this browser showed last (nearly always the one it's about to show). Call what it
 * returns with the profile's choice once that's known; it resolves when the language is settled as settleLanguage
 * would, or after LANGUAGE_WAIT_MS of the server not answering, when the first screen goes ahead in what's showing.
 */
export function beginLanguage(signal?: AbortSignal, waitMs = LANGUAGE_WAIT_MS): (profileChoice: string | null | undefined) => Promise<void> {
  // Null when the server couldn't be asked.
  const offered = languagesOffered(signal)
  const waitUp = new Promise<undefined>((resolve) => {
    const timer = setTimeout(resolve, waitMs)
    void offered.then(() => clearTimeout(timer))
  })
  const last = lastShown()?.code ?? null
  const early = last ? loadFor(waitMs, last, signal) : null

  return async (profileChoice) => {
    let code = profileChoice ?? null
    if (!code) {
      // Undefined when the wait is up and there's still no list. One that has come by now is used, however long it
      // took: a server slow to answer anything (just started) has often answered this by the time it says who's here.
      const list = await Promise.race([offered, waitUp])
      if (!list) {
        // No telling what the browser's language would be here: what's showing stays, and stays remembered.
        await early?.shown
        return
      }
      code = deviceLanguage(list.map((l) => l.code))
    }
    if (early && code === last) await early.shown
    else {
      early?.drop()
      await loadFor(waitMs, code, signal).shown
    }
    rememberLanguage(currentLanguage().code)
  }
}
