import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { clearApiCache, FETCH_TIMEOUT_MS, getLibraryGenres, getLibraryItems, keepPage, keptPage, setWatched } from './client'

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
