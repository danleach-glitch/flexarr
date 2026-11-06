import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createElement } from 'react'
import { beginLanguage, currentLanguage, deviceLanguage, LANGUAGE_WAIT_MS, showLanguage, startWithKeptLanguage, t } from '.'

const pack = {
  code: 'de',
  name: 'Deutsch',
  direction: 'ltr',
  version: 3,
  messages: {
    Play: 'Abspielen',
    'music::Play': 'Wiedergeben',
    'Resume from {time}': 'Fortsetzen ab {time}',
    '{n, plural, one {# season} other {# seasons}}': '{n, plural, one {# Staffel} other {# Staffeln}}',
    'Broken {x}': 'Kaputt {',
    'Hello {name}': 'Hallo {name}',
  },
}

function serve(body: unknown, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { ETag: '"de-3-web"' } })),
  )
}

afterEach(async () => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  await showLanguage(null)
  localStorage.clear()
})

describe('t', () => {
  it('shows English with no pack', () => {
    expect(t('Resume from {time}', { time: '32:10' })).toBe('Resume from 32:10')
    expect(t('{n, plural, one {# season} other {# seasons}}', { n: 1 })).toBe('1 season')
  })

  it('shows a language once its pack is in, string by string', async () => {
    serve(pack)
    await showLanguage('de')
    expect(currentLanguage().code).toBe('de')
    expect(document.documentElement.lang).toBe('de')
    expect(t('Play')).toBe('Abspielen')
    expect(t('Play', { ctx: 'music' })).toBe('Wiedergeben')
    expect(t('{n, plural, one {# season} other {# seasons}}', { n: 2 })).toBe('2 Staffeln')
    expect(t('Not translated')).toBe('Not translated')
    expect(t('Broken {x}', { x: 1 })).toBe('Broken 1')
  })

  it('drops React nodes into a sentence', async () => {
    serve(pack)
    await showLanguage('de')
    const html = renderToStaticMarkup(createElement('p', null, t.rich('Hello {name}', { name: createElement('b', null, 'Sam') })))
    expect(html).toBe('<p>Hallo <b>Sam</b></p>')
  })

  it('stays in English when the server has no such language', async () => {
    serve({}, 404)
    await showLanguage('xx')
    expect(currentLanguage().code).toBe('en')
  })
})

describe('deviceLanguage', () => {
  it('picks the best of the browser’s languages that the server offers', () => {
    expect(deviceLanguage(['de', 'pt'], ['pt-BR', 'de'])).toBe('pt')
    expect(deviceLanguage(['pt-BR', 'pt'], ['pt-br'])).toBe('pt-BR')
    expect(deviceLanguage(['de'], ['en-GB', 'de'])).toBeNull()
    expect(deviceLanguage(['de'], ['fr'])).toBeNull()
  })
})

describe('beginLanguage', () => {
  const list = { languages: [{ code: 'en', name: 'English', direction: 'ltr', version: 0 }, { code: 'de', name: 'Deutsch', direction: 'ltr', version: 3 }] }

  /** A server whose answers can be held back: `answer(path, body)` lets one go. */
  function server(ready: Record<string, unknown> = {}) {
    const waiting = new Map<string, (response: Response) => void>()
    const asked: { path: string; signal?: AbortSignal | null }[] = []
    const respond = (body: unknown) => (body instanceof Response ? body : new Response(JSON.stringify(body), { headers: { ETag: '"tag"' } }))
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
        const path = new URL(String(input), 'http://localhost').pathname.replace('/api/v1', '')
        asked.push({ path, signal: init?.signal })
        if (path in ready) return Promise.resolve(respond(ready[path]))
        return new Promise<Response>((resolve, reject) => {
          waiting.set(path, resolve)
          init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
        })
      }),
    )
    return { asked, answer: (path: string, body: unknown) => waiting.get(path)?.(respond(body)) }
  }

  /** As a browser that showed German last time, with that pack kept. */
  function lastShowedGerman() {
    localStorage.setItem('flexarr.language.de', JSON.stringify({ ...pack, etag: '"de-3-web"' }))
    localStorage.setItem('flexarr.language.choice', 'de')
    startWithKeptLanguage()
  }

  it('asks for the list, and the newest pack of the language shown last, before it knows who is watching', async () => {
    lastShowedGerman()
    const { asked } = server({ '/languages': list, '/languages/de': { ...pack, version: 4, messages: { Play: 'Los' } } })

    const settle = beginLanguage()
    expect(asked.map((a) => a.path)).toEqual(['/languages', '/languages/de'])

    await settle('de')
    expect(asked).toHaveLength(2)
    expect(t('Play')).toBe('Los')
    expect(localStorage.getItem('flexarr.language.choice')).toBe('de')
  })

  it('follows the browser’s language when the profile has none, and English when the server has no match', async () => {
    vi.stubGlobal('navigator', { languages: ['de-AT', 'en'], language: 'de-AT' })
    server({ '/languages': list, '/languages/de': pack })
    await beginLanguage()(null)
    expect(currentLanguage().code).toBe('de')
    expect(localStorage.getItem('flexarr.language.choice')).toBe('de')

    server({ '/languages': { languages: [list.languages[0]] } })
    await beginLanguage()(null)
    expect(currentLanguage().code).toBe('en')
    expect(localStorage.getItem('flexarr.language.choice')).toBeNull()
  })

  it('shows the profile’s language when it isn’t the one shown last, and lets the other request go', async () => {
    lastShowedGerman()
    const { asked } = server({ '/languages': list, '/languages/fr': { ...pack, code: 'fr', name: 'Français', messages: { Play: 'Lire' } } })

    await beginLanguage()('fr')

    expect(t('Play')).toBe('Lire')
    expect(asked.find((a) => a.path === '/languages/de')?.signal?.aborted).toBe(true)
    expect(localStorage.getItem('flexarr.language.choice')).toBe('fr')
  })

  it('goes on in the language showing when the server is slow to list its languages, and keeps it remembered', async () => {
    vi.useFakeTimers()
    lastShowedGerman()
    server({ '/languages/de': new Response(null, { status: 304 }) })

    let settled = false
    void beginLanguage()(null).then(() => (settled = true))
    await vi.advanceTimersByTimeAsync(LANGUAGE_WAIT_MS - 1)
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)

    expect(settled).toBe(true)
    expect(t('Play')).toBe('Abspielen')
    expect(localStorage.getItem('flexarr.language.choice')).toBe('de')
  })

  it('stops waiting for a pack the server is slow to send, and keeps it for next time without changing the screen', async () => {
    vi.useFakeTimers()
    const { answer } = server({ '/languages': list })

    let settled = false
    void beginLanguage()('de').then(() => (settled = true))
    await vi.advanceTimersByTimeAsync(LANGUAGE_WAIT_MS - 1)
    expect(settled).toBe(false)
    await vi.advanceTimersByTimeAsync(1)
    expect(settled).toBe(true)
    expect(currentLanguage().code).toBe('en')

    // It arrives once the first screen is up: nothing changes under whoever is using it.
    answer('/languages/de', pack)
    await vi.advanceTimersByTimeAsync(0)
    expect(currentLanguage().code).toBe('en')
    expect(JSON.parse(localStorage.getItem('flexarr.language.de')!).messages.Play).toBe('Abspielen')
  })
})
