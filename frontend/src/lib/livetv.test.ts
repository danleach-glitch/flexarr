import { afterEach, describe, expect, it, vi } from 'vitest'
import { dateFormat, locale, showLanguage } from '../i18n'
import { clock, programmeTime } from './livetv'

afterEach(async () => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  await showLanguage(null)
  localStorage.clear()
})

const serve = (code: string) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ code, name: code, direction: 'ltr', version: 1, messages: {} }))),
  )

describe('programme times', () => {
  it('read as the browser writes times in the language shown', () => {
    const start = new Date(2026, 10, 6, 9, 5)
    const end = new Date(2026, 10, 6, 21, 30)
    const written = (d: Date) => d.toLocaleTimeString(locale(), { hour: 'numeric', minute: '2-digit' })
    expect(clock.format(start)).toBe(written(start))
    expect(programmeTime({ start: start.toISOString(), end: end.toISOString() })).toBe(`${written(start)} – ${written(end)}`)
  })

  it('build no formatter per time: a guide of thousands shares one', () => {
    const built = vi.spyOn(Intl, 'DateTimeFormat')
    const start = Date.UTC(2026, 10, 6, 9)
    for (let i = 0; i < 3600; i++) programmeTime({ start: new Date(start + i * 60_000).toISOString(), end: new Date(start + (i + 30) * 60_000).toISOString() })
    // At most the one that writes the times and the one that asks the browser its locale.
    expect(built.mock.calls.length).toBeLessThanOrEqual(2)
    expect(dateFormat({ hour: 'numeric', minute: '2-digit' })).toBe(dateFormat({ hour: 'numeric', minute: '2-digit' }))
  })

  it('follow a change of language, and of the browser’s own languages', async () => {
    const monday = new Date(2026, 10, 2, 12)
    expect(dateFormat({ weekday: 'long' }).format(monday)).toBe('Monday')

    // A language this machine isn't set to, so the browser's languages decide its variant.
    const own = new Intl.DateTimeFormat().resolvedOptions().locale.toLowerCase()
    const [code, weekday] = own.startsWith('de') ? ['fr', 'lundi'] : ['de', 'Montag']
    serve(code)
    await showLanguage(code)
    expect(locale()).toBe(code)
    expect(dateFormat({ weekday: 'long' }).format(monday)).toBe(weekday)
    expect(clock.format(monday)).toBe(monday.toLocaleTimeString(code, { hour: 'numeric', minute: '2-digit' }))

    const languages = vi.spyOn(navigator, 'languages', 'get').mockReturnValue([`${code}-CH`, code])
    expect(locale()).toBe(`${code}-CH`)
    languages.mockReturnValue([`${code}-BE`, code])
    expect(locale()).toBe(`${code}-BE`)
    const date = { day: 'numeric', month: 'short', year: 'numeric' } as const
    expect(dateFormat(date).format(monday)).toBe(monday.toLocaleDateString(`${code}-BE`, date))
  })
})
