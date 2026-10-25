import { vi } from 'vitest'

type Handler = unknown | ((init: RequestInit | undefined, url: URL) => Response | unknown)

/**
 * Stubs fetch, answering `/api/v1<path>` from `routes` (matched by "METHOD /path" or "/path", query string ignored). A
 * handler may return a promise, to hold an answer back.
 */
export function mockApi(routes: Record<string, Handler>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost')
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = init?.method ?? 'GET'
    const key = [`${method} ${path}`, path].find((k) => k in routes)
    if (!key) return new Response('', { status: 404 })

    const handler = routes[key]
    const result: unknown = await (typeof handler === 'function' ? (handler as (i?: RequestInit, u?: URL) => unknown)(init, url) : handler)
    if (result === undefined) return new Response(null, { status: 204 })
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
    parentId: null,
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
    artwork: { poster: null, backdrop: null, logo: null, thumb: null },
    accentColor: null,
    communityRating: null,
    criticRating: null,
    contentRating: null,
    runtimeMinutes: null,
    releaseDate: null,
    userData: { positionSeconds: 0, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null },
    seriesTitle: null,
    discNumber: null,
    trackNumber: null,
    albumType: null,
    trackArtist: null,
    ...overrides,
  }
}

export function detail(item: ReturnType<typeof summary>, overrides: Record<string, unknown> = {}) {
  return {
    item,
    path: null,
    originalTitle: null,
    overview: null,
    studio: null,
    status: null,
    trailerYouTubeId: null,
    genres: [],
    tvdbId: null,
    tmdbId: null,
    imdbId: null,
    ancestors: [],
    children: [],
    credits: [],
    files: [],
    childOverviews: {},
    ...overrides,
  }
}
