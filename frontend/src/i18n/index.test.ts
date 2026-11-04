import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createElement } from 'react'
import { currentLanguage, deviceLanguage, showLanguage, t } from '.'

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
