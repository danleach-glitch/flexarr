import { act, fireEvent, render, renderHook, screen, within } from '@testing-library/react'
import { MemoryRouter, useNavigate } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import App, { Shell } from './App'
import { TrailerDialog } from './components/TrailerDialog'
import { usePolling } from './hooks/usePolling'
import { detail, mockApi, movies, status, summary } from './test/mockApi'

beforeAll(() => {
  // jsdom has no media playback.
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined)
  window.HTMLMediaElement.prototype.pause = vi.fn()
  window.HTMLElement.prototype.scrollBy = vi.fn()
  window.HTMLElement.prototype.scrollIntoView = vi.fn()
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Shell />
    </MemoryRouter>,
  )
}

const niceGuys = summary({ id: 'm1', title: 'The Nice Guys', year: 2016, resolution: '1080p', communityRating: 7.4, contentRating: 'R' })

describe('App', () => {
  it('shows the featured hero and rows on the home page', async () => {
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': {
        featured: [niceGuys],
        overviews: { m1: 'A mismatched pair of private eyes investigate a missing girl.' },
        rows: [{ id: 'top-movies', title: 'Top 10 films in your library', style: 'top10', items: [niceGuys], link: null }],
        upcoming: [],
        downloading: 0,
      },
    })

    render(<App />)

    expect(await screen.findByRole('heading', { level: 1, name: 'The Nice Guys' })).toBeInTheDocument()
    expect(screen.getByText(/mismatched pair/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Top 10 films in your library' })).toBeInTheDocument()
    expect(screen.getByText('Number 1: The Nice Guys')).toBeInTheDocument()
  })

  it('marks an episode watched from its card menu, and Continue Watching drops it', async () => {
    const episode = summary({ id: 'e5', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 5, seriesTitle: 'Severance', seriesId: 's1', durationSeconds: 3000, userData: { positionSeconds: 600, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null } })
    let watched = false
    const marked: unknown[] = []
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': () => ({
        featured: [],
        overviews: {},
        rows: watched ? [] : [{ id: 'continue', title: 'Continue watching', style: 'wide', items: [episode], link: null }],
        upcoming: [],
        downloading: 0,
      }),
      'POST /items/e5/watched': (init?: RequestInit) => {
        marked.push(JSON.parse(String(init?.body)))
        watched = true
      },
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'More for Severance S01E05' }))
    const menu = screen.getByRole('menu')
    expect(within(menu).getByRole('menuitem', { name: 'Resume S01E05' })).toBeInTheDocument()
    // The card's text links to the show, the season and the episode.
    expect(screen.getByRole('link', { name: 'Severance' })).toHaveAttribute('href', '/item/s1')
    expect(screen.getByRole('link', { name: 'Season 01 | Episode 05' })).toHaveAttribute('href', '/item/s1?season=1')
    expect(screen.getByRole('link', { name: 'Pilot' })).toHaveAttribute('href', '/item/e5')
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Mark episode as watched' }))

    await vi.waitFor(() => expect(screen.queryByRole('heading', { name: 'Continue watching' })).not.toBeInTheDocument())
    expect(marked).toEqual([{ watched: true }])
  })

  it('toggles watched from the tick on a poster', async () => {
    const posted: unknown[] = []
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': { featured: [], overviews: {}, rows: [{ id: 'recent', title: 'Latest unwatched movies', style: 'poster', items: [niceGuys], link: null }], upcoming: [], downloading: 0 },
      'POST /items/m1/watched': (init?: RequestInit) => {
        posted.push(JSON.parse(String(init?.body)))
      },
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'Mark The Nice Guys as watched' }))
    const tick = await screen.findByRole('button', { name: 'Watched: mark The Nice Guys as unwatched' })
    expect(tick).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(tick)

    await vi.waitFor(() => expect(posted).toEqual([{ watched: true }, { watched: false }]))
  })

  it('asks for an API key when the server requires one, then retries with it', async () => {
    // The key is traded for a session cookie (which jsdom's fetch mock stands in for here) and never stored.
    let authorized = false
    mockApi({
      '/session': (init?: RequestInit) => {
        authorized = (init?.headers as Record<string, string> | undefined)?.['X-Api-Key'] === 'secret'
        return new Response(null, { status: authorized ? 204 : 401 })
      },
      '/system/status': () => (authorized ? status : new Response('', { status: 401 })),
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)
    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))

    expect(await screen.findByRole('heading', { name: 'Your cinema is empty' })).toBeInTheDocument()
    expect(authorized).toBe(true)
    expect(localStorage.length).toBe(0)
  })

  it('says so when the API key is wrong', async () => {
    mockApi({ '/session': new Response(null, { status: 401 }), '/system/status': new Response('', { status: 401 }) })

    render(<App />)
    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'nope' } })
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))

    expect(await screen.findByText("That isn't the right key.")).toBeInTheDocument()
  })

  it('trades a key stored by an earlier version for a session, then forgets it', async () => {
    localStorage.setItem('flexarr.apiKey', 'secret')
    let sentKey: string | undefined
    mockApi({
      '/session': (init?: RequestInit) => {
        sentKey = (init?.headers as Record<string, string> | undefined)?.['X-Api-Key']
        return new Response(null, { status: 204 })
      },
      '/system/status': status,
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Your cinema is empty' })).toBeInTheDocument()
    expect(sentKey).toBe('secret')
    expect(localStorage.getItem('flexarr.apiKey')).toBeNull()
  })

  it('explains when the server is unreachable', async () => {
    mockApi({ '/system/status': new Response('', { status: 503, statusText: 'Unavailable' }) })

    render(<App />)

    expect(await screen.findByText(/Can't reach the server/)).toBeInTheDocument()
  })
})

describe('Library page', () => {
  it('lists titles with genre filters and scan progress', async () => {
    mockApi({
      '/libraries': [movies],
      '/libraries/lib-movies/genres': [{ name: 'Crime', count: 1 }],
      '/libraries/lib-movies/items': { total: 1, items: [niceGuys] },
    })

    renderAt('/library/lib-movies?view=all')

    expect(await screen.findByRole('link', { name: /The Nice Guys/ })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Crime' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('4 of 10')
    expect(screen.getByRole('link', { name: 'All films' })).toHaveAttribute('aria-current', 'page')
  })

  it('opens on Browse: rows for just this library, with a way to every title', async () => {
    mockApi({
      '/libraries': [movies],
      '/libraries/lib-movies/browse': {
        featured: [niceGuys],
        overviews: {},
        rows: [{ id: 'genre-Crime', title: 'Crime', style: 'poster', items: [niceGuys], link: '/library/lib-movies?view=all&genre=Crime' }],
        upcoming: [],
        downloading: 0,
      },
    })

    renderAt('/library/lib-movies')

    expect(await screen.findByRole('heading', { name: 'Crime' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Browse' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'All films' })).toHaveAttribute('href', '/library/lib-movies?view=all')
  })
})

describe('Series page', () => {
  it('shows seasons as tabs and the selected season’s episodes with overviews', async () => {
    const series = summary({ id: 's', kind: 'Series', title: '30 Rock', childCount: 1 })
    const season = summary({ id: 'season-1', kind: 'Season', parentId: 's', title: 'Season 1', seasonNumber: 1, childCount: 1 })
    const pilot = summary({ id: 'e1', kind: 'Episode', parentId: 'season-1', title: 'Pilot', seasonNumber: 1, episodeNumber: 1, runtimeMinutes: 22 })
    const seasonDetail = detail(season, { children: [pilot], ancestors: [series], childOverviews: { e1: 'Liz meets Jack.' } })
    const fetchMock = mockApi({
      '/libraries': [],
      // The series brings its Up Next and the season it opens on, in one request.
      '/items/s': detail(series, { children: [season], overview: 'Liz Lemon runs a sketch show.', nextUp: pilot, season: seasonDetail }),
      '/items/s/similar': [],
    })

    renderAt('/item/s')

    expect(await screen.findByRole('tab', { name: 'Season 1' })).toHaveAttribute('aria-selected', 'true')
    expect(await screen.findByRole('link', { name: 'Pilot' })).toBeInTheDocument()
    expect(screen.getByText('Liz meets Jack.')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /Play Season 01 \| Episode 01/ })).toBeInTheDocument()
    const paths = fetchMock.mock.calls.map(([input]) => new URL(String(input), 'http://localhost'))
    expect(paths.filter((u) => u.pathname.startsWith('/api/v1/items/')).map((u) => u.pathname + u.search)).toEqual([
      '/api/v1/items/s?includeSeason=true',
      '/api/v1/items/s/similar',
    ])
  })
})

describe('Search', () => {
  it('opens with ⌘K and goes to the highlighted result on Enter', async () => {
    mockApi({
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
      '/search': { titles: [niceGuys], episodes: [], people: [{ id: 'p1', name: 'Ryan Gosling', image: null, credits: 4 }], genres: [] },
      '/items/m1': detail(niceGuys),
      '/items/m1/similar': [],
    })

    renderAt('/')
    fireEvent.keyDown(window, { key: 'k', metaKey: true })
    fireEvent.change(await screen.findByRole('searchbox', { name: 'Search your library' }), { target: { value: 'nice' } })

    const option = await screen.findByRole('option', { name: /The Nice Guys/ })
    expect(option).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('option', { name: /Ryan Gosling/ })).toBeInTheDocument()

    fireEvent.keyDown(option, { key: 'Enter' })
    expect(await screen.findByRole('heading', { level: 1, name: 'The Nice Guys' })).toBeInTheDocument()
  })
})

describe('Settings', () => {
  it('shows validation errors from the server when adding a library', async () => {
    mockApi({
      '/libraries': [],
      '/filesystem': { path: '/Volumes', parent: '/', directories: [{ name: 'Media', path: '/Volumes/Media' }] },
      'POST /libraries': new Response(JSON.stringify({ errors: { folders: ['/Volumes overlaps a folder in another library.'] } }), { status: 400 }),
    })

    renderAt('/settings/libraries')
    fireEvent.click(await screen.findByRole('button', { name: 'Add library' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Use this folder' }))
    fireEvent.click(within(screen.getByRole('form', { name: 'Add library' })).getByRole('button', { name: 'Add library' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('overlaps a folder in another library')
  })

  it('stays closed to a profile that isn’t the main one', async () => {
    mockApi({ '/libraries': [], '/me': { profileId: 'c1', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null } })

    renderAt('/settings/server')

    expect(await screen.findByText(/Settings are for the main profile/)).toBeInTheDocument()
  })

  it('over Tailscale, explains how the owner gets to settings', async () => {
    mockApi({ '/libraries': [], '/me': { profileId: 'owner', method: 'tailscale', zone: 'Tailnet', isAdmin: false, tailscaleLogin: 'dan@example.com' } })

    renderAt('/settings/server')

    expect(await screen.findByText(/Choose the main profile with its PIN/)).toBeInTheDocument()
    expect(screen.getByLabelText('API key')).toBeInTheDocument()
  })

  it('reveals the API key with a POST, lists sign-ins and signs one out', async () => {
    const fetchMock = mockApi({
      '/libraries': [],
      '/settings': {
        authenticationRequired: 'Enabled',
        transcoding: {},
        availableHardwareAcceleration: [],
        pathMappings: [],
        arr: [],
        seerr: null,
        dataDirectory: '/data',
        ffmpegPath: 'ffmpeg',
        ffprobePath: 'ffprobe',
        webhookSecret: 'hook-secret',
      },
      'POST /settings/api-key/reveal': { apiKey: 'the-key' },
      '/sign-ins': [
        { id: 's1', kind: 'Device', name: 'Living room', createdAt: '2026-01-01T00:00:00Z', lastUsedAt: new Date().toISOString(), zone: 'Local', address: '192.168.1.9', profileId: 'owner', profileName: 'Dan', isThisOne: false },
      ],
      'DELETE /sign-ins/s1': undefined,
    })
    vi.stubGlobal('confirm', () => true)

    renderAt('/settings/server')

    expect(await screen.findByText(/webhooks\/sonarr\?secret=hook-secret/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Show API key' }))
    // It asks again first (the main profile has no PIN here, so just a yes).
    fireEvent.click(await screen.findByRole('button', { name: 'Show the key' }))
    expect(await screen.findByText('the-key')).toBeInTheDocument()
    const reveal = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/settings/api-key/reveal'))
    expect(JSON.parse(String(reveal?.[1]?.body))).toMatchObject({ confirm: true })
    expect(await screen.findByText('Living room')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))
    await vi.waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/sign-ins/s1') && init?.method === 'DELETE')).toBe(true))
  })

  it('warns before approving a TV that didn’t ask from the home network', async () => {
    mockApi({
      '/libraries': [],
      '/pairing/code/ABC-DEF': { code: 'ABC-DEF', deviceName: 'Living room', expiresAt: new Date().toISOString(), requestedFrom: '100.70.1.2', zone: 'Tailnet' },
    })

    renderAt('/link?code=ABC-DEF')

    expect(await screen.findByRole('heading', { name: 'Connect Living room?' })).toBeInTheDocument()
    expect(screen.getByText(/didn’t come from your home network/)).toBeInTheDocument()
  })
})

describe('Player', () => {
  it('requests playback with this browser’s capabilities and offers to skip the intro', async () => {
    const episode = summary({ id: 'e1', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 1 })
    let requested: { profile?: { videoCodecs: string[] } } = {}
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => {
        requested = JSON.parse(String(init?.body))
        return {
          sessionId: 'abc',
          method: 'DirectPlay',
          url: '/api/v1/playback/abc/stream',
          reasons: [],
          durationSeconds: 1320,
          startPositionSeconds: 0,
          fileId: 'f1',
          audioTracks: [],
          selectedAudioIndex: 1,
          subtitles: [],
          selectedSubtitleKey: null,
          subtitleBurnedIn: false,
          markers: [{ type: 'Intro', start: 30, end: 60 }],
          item: episode,
          ancestors: [summary({ id: 's', kind: 'Series', title: '30 Rock' })],
          next: null,
          transcodeSummary: null,
        }
      },
      'DELETE /playback/abc': undefined,
    })

    renderAt('/play/e1')

    expect(await screen.findByText('30 Rock')).toBeInTheDocument()
    expect(requested.profile?.videoCodecs).toContain('h264')

    const video = document.querySelector('video')!
    Object.defineProperty(video, 'currentTime', { configurable: true, value: 42 })
    act(() => {
      video.dispatchEvent(new Event('timeupdate'))
    })

    expect(await screen.findByRole('button', { name: 'Skip intro' })).toBeInTheDocument()
  })

  it('picks a stopped stream back up where it was, and says so if it stops again straight away', async () => {
    const episode = summary({ id: 'e1', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 1 })
    const requests: { startPositionSeconds?: number }[] = []
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => {
        requests.push(JSON.parse(String(init?.body)))
        const sessionId = `s${requests.length}`
        return {
          sessionId,
          method: 'Transcode',
          url: `/api/v1/playback/${sessionId}/master.m3u8`,
          reasons: [],
          durationSeconds: 1320,
          startPositionSeconds: 0,
          fileId: 'f1',
          audioTracks: [],
          selectedAudioIndex: 1,
          subtitles: [],
          selectedSubtitleKey: null,
          subtitleBurnedIn: false,
          markers: [],
          item: episode,
          ancestors: [],
          next: null,
          transcodeSummary: null,
        }
      },
      'DELETE /playback/s1': undefined,
      'DELETE /playback/s2': undefined,
    })

    renderAt('/play/e1')
    await screen.findByText('Pilot')
    const video = document.querySelector('video')!
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 352 })

    // The server restarted, say: the stream ends, and the player asks for a fresh one at the same spot.
    act(() => {
      video.dispatchEvent(new Event('error'))
    })
    await vi.waitFor(() => expect(video.src).toContain('/playback/s2/'))
    expect(requests[1].startPositionSeconds).toBe(352)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    act(() => {
      video.dispatchEvent(new Event('error'))
    })
    expect(await screen.findByRole('alert')).toHaveTextContent('The stream stopped')
    expect(requests).toHaveLength(2)
  })
})

describe('Profiles', () => {
  it('asks who is watching when there are several profiles, and checks the PIN', async () => {
    const profiles = [
      { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: false, isMain: true, isCurrent: true, hiddenLibraryIds: [] },
      { id: 'c1', name: 'Sam', color: '#e8a0bf', hasPin: true, isMain: false, isCurrent: false, hiddenLibraryIds: [] },
    ]
    let sentPin: string | undefined
    mockApi({
      '/system/status': status,
      '/profiles': profiles,
      'POST /profiles/c1/select': (init?: RequestInit) => {
        sentPin = JSON.parse(String(init?.body)).pin
        return { token: 'c1.sig', profile: { ...profiles[1], isCurrent: true, hiddenLibraryIds: [] } }
      },
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: /Sam/ }))
    fireEvent.change(await screen.findByLabelText('PIN'), { target: { value: '2468' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    expect(await screen.findByRole('heading', { name: 'Your cinema is empty' })).toBeInTheDocument()
    expect(sentPin).toBe('2468')
    expect(localStorage.getItem('flexarr.profile')).toBe('c1.sig')
  })

  it('asks again when the server no longer honours the profile chosen here', async () => {
    // Chosen before the API key changed: the server now treats this browser as the main profile.
    localStorage.setItem('flexarr.profile', 'c1.old-signature')
    mockApi({
      '/system/status': status,
      '/profiles': [
        { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: false, isMain: true, isCurrent: true, hiddenLibraryIds: [] },
        { id: 'c1', name: 'Sam', color: '#e8a0bf', hasPin: false, isMain: false, isCurrent: false, hiddenLibraryIds: [] },
      ],
      '/me': { profileId: 'owner', method: 'local', zone: 'Local', isAdmin: true, tailscaleLogin: null },
    })

    render(<App />)

    expect(await screen.findByRole('button', { name: /Sam/ })).toBeInTheDocument()
    expect(localStorage.getItem('flexarr.profile')).toBeNull()
  })
})

describe('Library grid', () => {
  // 210 films: a first page of 200, then "Show 10 more".
  const films = Array.from({ length: 210 }, (_, i) => summary({ id: `f${i}`, title: `Film ${String(i).padStart(3, '0')}` }))
  function grid(onItem?: (id: string) => void) {
    const pages: number[] = []
    let watched = false
    mockApi({
      '/libraries': [{ ...movies, scan: null, itemCount: films.length }],
      '/libraries/lib-movies/genres': [],
      '/libraries/lib-movies/items': (_: RequestInit | undefined, url: URL) => {
        const skip = Number(url.searchParams.get('skip') ?? 0)
        pages.push(skip)
        return { total: films.length, items: films.slice(skip, skip + Number(url.searchParams.get('take'))) }
      },
      'POST /items/f3/watched': () => {
        watched = true
      },
      '/items/f3': () => {
        onItem?.('f3')
        return detail({ ...films[3], userData: { ...films[3].userData, watched } })
      },
      '/items/f3/similar': [],
    })
    return pages
  }

  it('loads "Show more" once however often it is clicked', async () => {
    const pages = grid()
    renderAt('/library/lib-movies?view=all')

    const more = await screen.findByRole('button', { name: 'Show 10 more' })
    fireEvent.click(more)
    fireEvent.click(more)

    expect(await screen.findByRole('link', { name: 'Film 209' })).toBeInTheDocument()
    expect(pages).toEqual([0, 200])
    expect(screen.getAllByRole('link', { name: /^Film \d+$/ })).toHaveLength(210)
  })

  it('marks a title watched in place, keeping the pages already loaded', async () => {
    const fetched: string[] = []
    const pages = grid((id) => fetched.push(id))
    renderAt('/library/lib-movies?view=all')

    fireEvent.click(await screen.findByRole('button', { name: 'Show 10 more' }))
    await screen.findByRole('link', { name: 'Film 209' })
    fireEvent.click(screen.getByRole('button', { name: 'Mark Film 003 as watched' }))

    expect(await screen.findByRole('button', { name: 'Watched: mark Film 003 as unwatched' })).toHaveAttribute('aria-pressed', 'true')
    await vi.waitFor(() => expect(fetched).toEqual(['f3']))
    expect(pages).toEqual([0, 200])
    expect(screen.getAllByRole('link', { name: /^Film \d+$/ })).toHaveLength(210)
  })

  it('comes back from a title marked watched with the pages it had, and stays put when you search', async () => {
    const pages = grid()
    function Back() {
      const navigate = useNavigate()
      return <button onClick={() => navigate(-1)}>Back</button>
    }
    render(
      <MemoryRouter initialEntries={['/library/lib-movies?view=all']}>
        <Shell />
        <Back />
      </MemoryRouter>,
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Show 10 more' }))
    await screen.findByRole('link', { name: 'Film 209' })
    fireEvent.click(screen.getByRole('link', { name: 'Film 003' }))
    // Marking it watched is a change, which clears cached answers; the grid's own state is kept apart.
    fireEvent.click(await screen.findByRole('button', { name: /Mark as watched/ }))
    expect(await screen.findByRole('button', { name: /Watched/ })).toHaveAttribute('aria-pressed', 'true')

    const scrolls = vi.mocked(window.scrollTo)
    scrolls.mockClear()
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))

    expect(await screen.findByRole('link', { name: 'Film 209' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Watched: mark Film 003 as unwatched' })).toHaveAttribute('aria-pressed', 'true')
    expect(pages).toEqual([0, 200])
    // The page opens at the top, then the grid goes back to where it was.
    await vi.waitFor(() => expect(scrolls).toHaveBeenCalledTimes(2))

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Movies' }), { target: { value: 'Film 1' } })
    await vi.waitFor(() => expect(pages).toEqual([0, 200, 0]))
    await new Promise((resolve) => requestAnimationFrame(resolve))
    expect(scrolls).toHaveBeenCalledTimes(2)
  })
})

describe('Requests', () => {
  it('asks the server who you are once, however many parts of the page want to know', async () => {
    const film = summary({ id: 'm1', title: 'The Nice Guys' })
    const fetchMock = mockApi({
      '/libraries': [],
      '/me': { profileId: 'owner', method: 'local', zone: 'Local', isAdmin: true, tailscaleLogin: null },
      '/profiles': [],
      '/items/m1': detail(film),
      '/items/m1/similar': [],
    })

    renderAt('/item/m1')

    expect(await screen.findByRole('heading', { level: 1, name: 'The Nice Guys' })).toBeInTheDocument()
    const calls = (path: string) => fetchMock.mock.calls.filter(([input]) => new URL(String(input), 'http://localhost').pathname === `/api/v1${path}`).length
    await vi.waitFor(() => expect(calls('/me')).toBe(1))
    expect(calls('/profiles')).toBe(1)
    expect(calls('/items/m1')).toBe(1)
  })

  it("doesn't poll from a background tab, and catches up when it's shown", () => {
    vi.useFakeTimers()
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
    const tick = vi.fn()
    try {
      renderHook(() => usePolling(tick, 1000))
      vi.advanceTimersByTime(5000)
      expect(tick).not.toHaveBeenCalled()

      hidden.mockReturnValue(false)
      act(() => {
        document.dispatchEvent(new Event('visibilitychange'))
      })
      expect(tick).toHaveBeenCalledTimes(1)
      vi.advanceTimersByTime(2000)
      expect(tick).toHaveBeenCalledTimes(3)
    } finally {
      hidden.mockRestore()
      vi.useRealTimers()
    }
  })
})

describe('Trailer', () => {
  it('only embeds what looks like a YouTube video id', () => {
    const { rerender } = render(<TrailerDialog videoId="dQw4w9WgXcQ" title="Film" onClose={() => {}} />)
    expect(screen.getByTitle('Film trailer')).toHaveAttribute('src', 'https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0')

    rerender(<TrailerDialog videoId="../../evil?x=" title="Film" onClose={() => {}} />)
    expect(screen.queryByTitle('Film trailer')).not.toBeInTheDocument()
    expect(screen.getByText('This trailer can’t be played.')).toBeInTheDocument()
  })
})
