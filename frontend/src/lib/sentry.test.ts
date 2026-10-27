import type { ErrorEvent } from '@sentry/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Counts loads of the SDK, so "off" can be shown to never fetch it.
const sdk = vi.hoisted(() => ({
  loads: 0,
  init: vi.fn(),
  close: vi.fn(async () => true),
  setUser: vi.fn(),
  captureException: vi.fn(),
}))

vi.mock('@sentry/react', () => {
  sdk.loads++
  return {
    init: sdk.init,
    close: sdk.close,
    setUser: sdk.setUser,
    captureException: sdk.captureException,
    dedupeIntegration: () => ({ name: 'Dedupe' }),
    linkedErrorsIntegration: () => ({ name: 'LinkedErrors' }),
  }
})

afterEach(() => {
  vi.resetModules()
  vi.clearAllMocks()
  sdk.loads = 0
})

/** A fresh copy of the module, its switch off. */
const fresh = () => import('./sentry')
const scrubbing = () => import('./sentryScrub')

describe('scrubText', () => {
  it('takes out quotes, hosts, addresses, emails, paths and secrets', async () => {
    const { scrubText } = await scrubbing()
    expect(scrubText('Couldn’t play “The Bear” S02E03')).toBe('Couldn’t play "…" S02E03')
    expect(scrubText(`No artwork for "Dune: Part Two" or 'Arrival'`)).toBe('No artwork for "…" or "…"')
    // Apostrophes are not quotes.
    expect(scrubText("can't read 'x' and don't")).toBe('can\'t read "…" and don\'t')
    expect(scrubText('GET https://flexarr.example.com:8383/api/v1/items/42?api_key=abc failed')).toBe('GET <server>/api/v1/items/42 failed')
    expect(scrubText('refused by 192.168.1.20:8383 and fe80::1c2b:3aff:fe4d:12')).toBe('refused by <ip> and <ip>')
    expect(scrubText('mail dan@example.com')).toBe('mail <email>')
    expect(scrubText('ENOENT /Volumes/Media/Movies/Dune (2021)/Dune.mkv')).toBe('ENOENT <path>')
    expect(scrubText('at C:\\Users\\dan\\Videos\\a.mkv')).toBe('at <path>')
    expect(scrubText('open file:///Users/dan/x.mkv')).toBe('open <path>')
    expect(scrubText('token=abc123 and 0123456789abcdef0123456789abcdef')).toBe('token=[redacted] and [redacted]')
    // The app's own routes and assets, times and code stay readable.
    expect(scrubText('fetch /api/v1/logs at 12:30:45 in Foo::bar')).toBe('fetch /api/v1/logs at 12:30:45 in Foo::bar')
  })
})

describe('scrubFilename', () => {
  it('keeps only the asset path', async () => {
    const { scrubFilename } = await scrubbing()
    expect(scrubFilename('http://192.168.1.20:8383/assets/index-abc.js?v=1')).toBe('/assets/index-abc.js')
    expect(scrubFilename('https://flexarr.tail1234.ts.net/assets/SettingsPage-x.js')).toBe('/assets/SettingsPage-x.js')
    expect(scrubFilename('/Users/dan/Projects/flexarr/frontend/src/App.tsx')).toBe('<path>')
    expect(scrubFilename('<anonymous>')).toBe('<anonymous>')
  })
})

describe('scrubEvent', () => {
  const event = (): ErrorEvent =>
    ({
      type: undefined,
      event_id: 'e1',
      timestamp: 1,
      platform: 'javascript',
      level: 'error',
      release: 'flexarr-web@0.44.0',
      environment: 'development', // vitest runs as the dev server
      message: 'Lost "Severance"',
      server_name: 'mini.local',
      request: { url: 'https://mini.local:8383/item/1', headers: { Cookie: 'flexarr_session=x' } },
      user: { id: 'install-1', email: 'dan@example.com', ip_address: '10.0.0.2', username: 'Dan' },
      breadcrumbs: [{ message: 'clicked "Severance"' }],
      extra: { title: 'Severance' },
      tags: { category: 'react', profile: 'Dan' },
      contexts: {
        os: { name: 'macOS', version: '15.1', kernel_version: '24.1' },
        browser: { name: 'Chrome', version: '130', locale: 'en-GB' },
        runtime: { name: 'browser' },
        culture: { locale: 'en-GB', timezone: 'Europe/London' },
        device: { timezone: 'Europe/London', model: 'Mac' },
        profile: { name: 'Dan' },
        react: { componentStack: 'x' },
      },
      exception: {
        values: [
          {
            type: 'TypeError',
            value: 'Cannot play "Severance" from http://10.0.0.5:8383/hls/abc.m3u8?api_key=k',
            mechanism: { type: 'generic', handled: true, data: { secret: 'x' } },
            stacktrace: {
              frames: [{ filename: 'http://10.0.0.5:8383/assets/index-abc.js', abs_path: 'http://10.0.0.5:8383/assets/index-abc.js', function: 'play', lineno: 1, colno: 2, in_app: true, vars: { title: 'Severance' }, context_line: 'play(title)' }],
            },
          },
        ],
      },
    }) as unknown as ErrorEvent

  it('sends only what’s listed, every piece of text scrubbed', async () => {
    const { scrubEvent } = await scrubbing()
    const out = scrubEvent(event(), 'install-1')!
    expect(out).not.toHaveProperty('request')
    expect(out).not.toHaveProperty('breadcrumbs')
    expect(out).not.toHaveProperty('extra')
    expect(out).not.toHaveProperty('server_name')
    expect(out.user).toEqual({ id: 'install-1' })
    expect(out.tags).toEqual({ category: 'react' })
    // No time zone, locale or culture: they say where someone is.
    expect(out.contexts).toEqual({ browser: { name: 'Chrome', version: '130' }, os: { name: 'macOS', version: '15.1' } })
    expect(out.message).toBe('Lost "…"')
    const exception = out.exception!.values![0]
    expect(exception.value).toBe('Cannot play "…" from <server>/hls/abc.m3u8')
    expect(exception.mechanism).toEqual({ type: 'generic', handled: true })
    expect(exception.stacktrace!.frames![0]).toEqual({ filename: '/assets/index-abc.js', abs_path: '/assets/index-abc.js', function: 'play', lineno: 1, colno: 2, in_app: true })
    expect(JSON.stringify(out)).not.toMatch(/Severance|10\.0\.0|mini\.local|Dan|api_key|example\.com|Europe|en-GB/)
  })

  it('drops the event if scrubbing fails', async () => {
    const { scrubEvent } = await scrubbing()
    const broken = event()
    Object.defineProperty(broken, 'exception', {
      get() {
        throw new Error('boom')
      },
    })
    expect(scrubEvent(broken, null)).toBeNull()
  })
})

describe('loading', () => {
  it('never loads the SDK while error reports are off', async () => {
    const { configureSentry, captureForSentry, sentryRequested } = await fresh()
    configureSentry({ enabled: false, installId: null }, '0.44.0')
    configureSentry(undefined, '0.44.0')
    captureForSentry(new Error('x'), 'window')
    await new Promise((r) => setTimeout(r, 0))
    expect(sentryRequested()).toBe(false)
    expect(sdk.loads).toBe(0)
    expect(sdk.init).not.toHaveBeenCalled()
  })

  it('loads and starts it when on, strictly, and stops at once when switched off', async () => {
    const { configureSentry, captureForSentry } = await fresh()
    configureSentry({ enabled: true, installId: 'install-1' }, '0.44.0')
    await vi.waitFor(() => expect(sdk.init).toHaveBeenCalledTimes(1))
    const options = sdk.init.mock.calls[0][0]
    expect(options).toMatchObject({
      release: 'flexarr-web@0.44.0',
      environment: 'development', // vitest runs as the dev server
      defaultIntegrations: false,
      tracesSampleRate: 0,
      sendClientReports: false,
      dataCollection: { userInfo: false, cookies: false, httpHeaders: false },
    })
    expect(options.integrations.map((i: { name: string }) => i.name)).toEqual(['Dedupe', 'LinkedErrors'])
    expect(sdk.setUser).toHaveBeenCalledWith({ id: 'install-1' })

    captureForSentry(new Error('one'), 'window')
    await vi.waitFor(() => expect(sdk.captureException).toHaveBeenCalledTimes(1))

    configureSentry({ enabled: false, installId: null }, '0.44.0')
    expect(sdk.close).toHaveBeenCalled()
    // Anything already on its way is stopped as it leaves.
    expect(options.beforeSend({ exception: { values: [] } })).toBeNull()
    captureForSentry(new Error('two'), 'window')
    await new Promise((r) => setTimeout(r, 0))
    expect(sdk.captureException).toHaveBeenCalledTimes(1)
  })
})

describe('deviceContexts', () => {
  it('names the browser and OS, no more', async () => {
    const { deviceContexts } = await scrubbing()
    const edge = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0'
    expect(deviceContexts(edge)).toEqual({ browser: { name: 'Edge', version: '130' }, os: { name: 'Windows' } })
    const iphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'
    expect(deviceContexts(iphone)).toEqual({ browser: { name: 'Safari', version: '18' }, os: { name: 'iOS' } })
  })
})
