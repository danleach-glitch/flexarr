import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi, movies } from '../test/mockApi'
import { apiGet, clearApiCache, FETCH_TIMEOUT_MS, getLibraries, getLibraryGenres, getLibraryItems, keepPage, keptPage, onAdminLapsed, onLockedChange, savePlayerPreferences, setWatched } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

const sent = (fetchMock: ReturnType<typeof mockApi>, path: string) =>
  fetchMock.mock.calls.filter(([input]) => new URL(String(input), 'http://localhost').pathname === `/api/v1${path}`).length

describe('API cache', () => {
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
