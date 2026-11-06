import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi, movies } from '../test/mockApi'
import { apiGet, parseAnswer, beaconBookProgress, clearApiCache, FETCH_TIMEOUT_MS, getLibraries, liveHeartbeat, reportProgress, getLibraryGenres, getLibraryItems, keepPage, keptPage, onAdminLapsed, onLockedChange, savePlayerPreferences, setWatched, stopPlayback, watchLiveChannel } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

const sent = (fetchMock: ReturnType<typeof mockApi>, path: string) =>
  fetchMock.mock.calls.filter(([input]) => new URL(String(input), 'http://localhost').pathname === `/api/v1${path}`).length

describe('API cache', () => {
  it('keeps cached answers through progress reports while playing, but not a pause or a change', async () => {
    const fetchMock = mockApi({ '/libraries/l/genres': [], 'POST /playback/s/progress': undefined, 'POST /livetv/watch/v/heartbeat': undefined })
    clearApiCache()
    await getLibraryGenres('l')
    await reportProgress('s', 10, false)
    await liveHeartbeat('v')
    await getLibraryGenres('l')
    expect(sent(fetchMock, '/libraries/l/genres')).toBe(1)

    await reportProgress('s', 20, true)
    await getLibraryGenres('l')
    expect(sent(fetchMock, '/libraries/l/genres')).toBe(2)
    await reportProgress('s', 30, false, true)
    await getLibraryGenres('l')
    expect(sent(fetchMock, '/libraries/l/genres')).toBe(3)
  })

  it('keeps the 100 most recently used answers', async () => {
    const fetchMock = mockApi(Object.fromEntries(Array.from({ length: 101 }, (_, i) => [`/libraries/l${i}/genres`, []])))
    await getLibraryGenres('l0')
    for (let i = 1; i <= 100; i++) {
      await getLibraryGenres(`l${i}`)
      // l1 stays in use, so it outlives l0.
      if (i === 50) await getLibraryGenres('l1')
    }

    await getLibraryGenres('l1')
    await getLibraryGenres('l100')
    expect(sent(fetchMock, '/libraries/l1/genres')).toBe(1)
    expect(sent(fetchMock, '/libraries/l100/genres')).toBe(1)
    await getLibraryGenres('l0')
    expect(sent(fetchMock, '/libraries/l0/genres')).toBe(2)
  })

  it("doesn't keep a letter jump's big pages", async () => {
    const fetchMock = mockApi({ '/libraries/l/items': { total: 0, items: [] } })
    await getLibraryItems('l', { skip: 500, take: 500 })
    await getLibraryItems('l', { skip: 500, take: 500 })
    await getLibraryItems('l', { take: 200 })
    await getLibraryItems('l', { take: 200 })
    expect(sent(fetchMock, '/libraries/l/items')).toBe(3)
  })

  it('gives up on a request the server never answers, for everyone waiting, and asks afresh next time', async () => {
    vi.useFakeTimers()
    let answer = false
    const fetchMock = vi.fn(
      (_: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((resolve, reject) => {
          if (answer) resolve(new Response('[]'))
          init?.signal?.addEventListener('abort', () => reject(init.signal?.reason))
        }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const first = getLibraryGenres('slow')
    const second = getLibraryGenres('slow')
    const failures = Promise.allSettled([first, second])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(FETCH_TIMEOUT_MS)
    const [a, b] = await failures
    expect(a).toMatchObject({ status: 'rejected', reason: { name: 'TimeoutError' } })
    expect(b).toMatchObject({ status: 'rejected', reason: { name: 'TimeoutError' } })

    answer = true
    await expect(getLibraryGenres('slow')).resolves.toEqual([])
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it("gives up on a letter jump's big page the server never answers, or when its caller aborts", async () => {
    vi.useFakeTimers()
    const fetchMock = vi.fn(
      (_: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(init.signal?.reason))),
    )
    vi.stubGlobal('fetch', fetchMock)

    const stalled = getLibraryItems('l', { skip: 500, take: 500 })
    const failure = expect(stalled).rejects.toMatchObject({ name: 'TimeoutError' })
    await vi.advanceTimersByTimeAsync(FETCH_TIMEOUT_MS)
    await failure

    const controller = new AbortController()
    const left = getLibraryItems('l', { skip: 1000, take: 500 }, controller.signal)
    controller.abort()
    await expect(left).rejects.toMatchObject({ name: 'AbortError' })
  })

  it('forgets cached answers on a change, but not a page kept for going back', async () => {
    const fetchMock = mockApi({ '/libraries/l/genres': [], 'POST /items/i/watched': undefined })
    keepPage('grid', { scrollY: 900 })
    await getLibraryGenres('l')

    await setWatched('i', true)

    expect(keptPage('grid')).toEqual({ scrollY: 900 })
    await getLibraryGenres('l')
    expect(sent(fetchMock, '/libraries/l/genres')).toBe(2)
  })

  it('can forget just some answers', async () => {
    const fetchMock = mockApi({ '/libraries/a/genres': [], '/libraries/b/genres': [] })
    await getLibraryGenres('a')
    await getLibraryGenres('b')

    clearApiCache((path) => path.startsWith('/libraries/a/'))
    await getLibraryGenres('a')
    await getLibraryGenres('b')
    expect(sent(fetchMock, '/libraries/a/genres')).toBe(2)
    expect(sent(fetchMock, '/libraries/b/genres')).toBe(1)
  })
})

describe('Playback preferences', () => {
  it('leaves books and podcasts in Continue out of a save from the player', async () => {
    const sent: unknown[] = []
    mockApi({
      'PUT /me/playback-preferences': (init?: RequestInit) => {
        const body: unknown = JSON.parse(String(init?.body))
        sent.push(body)
        return body
      },
    })

    await savePlayerPreferences({ autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Large', listeningInContinue: true })

    expect(sent).toEqual([{ autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Large' }])
  })
})

describe('Times', () => {
  it('reads times sent without a zone as UTC, and leaves dates and zoned times alone', async () => {
    mockApi({ '/libraries': [{ ...movies, lastScannedAt: '2026-10-27T01:53:49.215901', created: '2026-10-27T01:53:49Z', aired: '2026-10-27', name: '2026-10-27T01:53:49 notes' }] })

    const [library] = (await getLibraries()) as unknown as Record<string, string>[]

    expect(library.lastScannedAt).toBe('2026-10-27T01:53:49.215901Z')
    expect(library.created).toBe('2026-10-27T01:53:49Z')
    expect(library.aired).toBe('2026-10-27')
    expect(library.name).toBe('2026-10-27T01:53:49 notes')
  })
})

describe('Parsing an answer', () => {
  /** What every answer went through before: each value checked as it's parsed. */
  const checkingEveryValue = (text: string) =>
    JSON.parse(text, (_key, value: unknown) => (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$/.test(value) ? `${value}Z` : value)) as unknown

  it('only checks each value when the text could hold a time without a zone', async () => {
    const parse = vi.spyOn(JSON, 'parse')
    try {
      mockApi({ '/libraries': [{ ...movies, lastScannedAt: '2026-10-27T01:53:49.215901Z', aired: '2026-10-27' }] })
      await apiGet('/libraries')
      expect(parse).toHaveBeenCalledTimes(1)
      expect(parse.mock.calls[0]).toHaveLength(1)

      mockApi({ '/libraries': [{ ...movies, lastScannedAt: '2026-10-27T01:53:49.215901' }] })
      const [library] = await apiGet<{ lastScannedAt: string }[]>('/libraries')
      expect(parse.mock.calls[1][1]).toBeTypeOf('function')
      expect(library.lastScannedAt).toBe('2026-10-27T01:53:49.215901Z')
    } finally {
      parse.mockRestore()
    }
  })

  it.each([
    ['a time with no zone', '{"at":"2026-10-27T01:53:49"}'],
    ['one with fractions of a second', '{"at":"2026-10-27T01:53:49.215901"}'],
    ['one deep inside lists', '{"a":[{"b":[null,1,"2026-10-27T01:53:49",{"c":"2026-10-27T01:53:50"}]}]}'],
    ['one that is the whole answer', '"2026-10-27T01:53:49"'],
    ['one written with an escaped digit', '{"at":"\\u0032026-10-27T01:53:49"}'],
    ['one written with an escaped colon, dash, dot and T', '{"at":"2026\\u002d10-27\\u005401\\u003A53:49\\u002E5"}'],
    ['a zoned time', '{"at":"2026-10-27T01:53:49Z","other":"2026-10-27T01:53:49+01:00"}'],
    ['a date', '{"aired":"2026-10-27"}'],
    ['a time inside a sentence', '{"note":"2026-10-27T01:53:49 notes","more":"at 2026-10-27T01:53:49"}'],
    ['a time in quotes inside a sentence', '{"note":"she said \\"2026-10-27T01:53:49\\" twice","end":"x\\"2026-10-27T01:53:49"}'],
    ['a time as a name, not a value', '{"2026-10-27T01:53:49":1}'],
    ['other escapes', '{"title":"Tom \\u0026 Jerry\\u0027s \\u003Cb\\u003E caf\\u00E9","n":12.5,"ok":true,"none":null}'],
    ['nothing at all', '[]'],
  ])('reads %s exactly as checking every value would', (_what, text) => {
    expect(parseAnswer(text)).toEqual(checkingEveryValue(text))
  })
})

describe('an admin read after the PIN ran out here', () => {
  it('says settings are locked, and tells the session to look again', async () => {
    mockApi({
      'GET /people/p1/candidates': () => new Response('', { status: 403, statusText: 'Forbidden' }),
      'GET /me': () => ({ isAdmin: false, canUnlock: true }),
    })
    const stopAsking = onLockedChange(() => Promise.resolve(false))
    const lapsed = vi.fn()
    const stopListening = onAdminLapsed(lapsed)
    try {
      await expect(apiGet('/people/p1/candidates')).rejects.toThrow('Settings are locked here. Unlock them to see this.')
      expect(lapsed).toHaveBeenCalledOnce()
    } finally {
      stopAsking()
      stopListening()
    }
  })
})

describe('ids in paths', () => {
  // What the browser would make of each address asked for.
  const asked = (fetchMock: ReturnType<typeof mockApi>) => fetchMock.mock.calls.map(([input]) => new URL(String(input), 'http://localhost').pathname)

  it('keeps an id from a crafted address inside its own request', async () => {
    const fetchMock = mockApi({ 'POST /pairing/code/ABC123/approve': undefined, 'PUT /settings': undefined, 'DELETE /settings': undefined })
    const crafted = '%2E%2E/%2E%2E/pairing/code/ABC123/approve?'
    await expect(watchLiveChannel(crafted)).rejects.toMatchObject({ status: 404 })
    beaconBookProgress('../../settings?', 10, 'web')
    await stopPlayback('../settings#')

    const paths = asked(fetchMock)
    expect(paths).toHaveLength(3)
    expect(paths[0]).toBe(`/api/v1/livetv/channels/${encodeURIComponent(crafted)}/watch`)
    expect(paths[1]).toMatch(/^\/api\/v1\/items\/[^/]+\/progress$/)
    expect(paths[2]).toMatch(/^\/api\/v1\/playback\/[^/]+$/)
  })

  it('leaves an ordinary id as it is', async () => {
    const fetchMock = mockApi({ 'POST /items/3f2b8c1e-5d4a-4c6b-9e7f-0a1b2c3d4e5f/watched': undefined })
    await setWatched('3f2b8c1e-5d4a-4c6b-9e7f-0a1b2c3d4e5f', true)
    expect(fetchMock.mock.calls.map(([input]) => String(input))).toEqual(['/api/v1/items/3f2b8c1e-5d4a-4c6b-9e7f-0a1b2c3d4e5f/watched'])
  })

  it('sends nothing for a path with a step up, a second ?, or a character browsers drop or read as a slash', async () => {
    const fetchMock = mockApi({})
    const strays = ['/items/../settings', '/items/%2e%2E/settings', '/items/.%2e/settings', '/items/./x', '/items/a?b=1?c=2', '/items/a#b', '/items/..\\settings', '/items/.\t./settings']
    for (const path of strays) await expect(apiGet(path)).rejects.toMatchObject({ status: 404 })
    await expect(setWatched('..', true)).rejects.toMatchObject({ status: 404 })
    expect(fetchMock).not.toHaveBeenCalled()

    // A dot in a name, or one question mark, is an ordinary path.
    await expect(apiGet('/items/a.b/x..y?q=..')).rejects.toMatchObject({ status: 404 })
    expect(asked(fetchMock)).toEqual(['/api/v1/items/a.b/x..y'])
  })
})
