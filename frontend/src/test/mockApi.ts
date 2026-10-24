import { vi } from 'vitest'

type Handler = unknown | ((init: RequestInit | undefined) => Response | unknown)

/** Stubs fetch, answering `/api/v1<path>` from `routes` (matched by path without query string, longest first). */
export function mockApi(routes: Record<string, Handler>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = init?.method ?? 'GET'
    const key = Object.keys(routes)
      .sort((a, b) => b.length - a.length)
      .find((route) => route === `${method} ${path}` || route === path)
    if (!key) return new Response('', { status: 404 })

    const handler = routes[key]
    const result = typeof handler === 'function' ? (handler as (i?: RequestInit) => unknown)(init) : handler
    return result instanceof Response ? result : new Response(JSON.stringify(result))
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

export const status = {
  appName: 'Flexarr',
  version: '0.1.0+abc',
  osDescription: 'macOS',
  runtimeVersion: '.NET 10',
  startTime: new Date().toISOString(),
}

export const movies = {
  id: 'lib-movies',
  name: 'Movies',
  type: 'Movies',
  folders: ['/media/movies'],
  itemCount: 2,
  lastScannedAt: null,
  scan: { path: '/media/movies', total: 10, processed: 4 },
}

export function summary(overrides: Record<string, unknown>) {
  return {
    id: 'item',
    libraryId: 'lib-movies',
    kind: 'Movie',
    title: 'Title',
    year: null,
    seasonNumber: null,
    episodeNumber: null,
    episodeNumberEnd: null,
    airDate: null,
    childCount: 0,
    durationSeconds: null,
    resolution: null,
    addedAt: new Date().toISOString(),
    ...overrides,
  }
}
