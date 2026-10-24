import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import App, { Shell } from './App'
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
    expect(screen.getByRole('link', { name: 'S01E05' })).toHaveAttribute('href', '/item/s1?season=1')
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
    let authorized = false
    mockApi({
      '/system/status': (init?: RequestInit) => {
        authorized = (init?.headers as Record<string, string> | undefined)?.['X-Api-Key'] === 'secret'
        return authorized ? status : new Response('', { status: 401 })
      },
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)
    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))

    expect(await screen.findByRole('heading', { name: 'Your cinema is empty' })).toBeInTheDocument()
    expect(authorized).toBe(true)
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
    mockApi({
      '/libraries': [],
      '/items/s': detail(series, { children: [season], overview: 'Liz Lemon runs a sketch show.' }),
      '/items/season-1': detail(season, { children: [pilot], ancestors: [series], childOverviews: { e1: 'Liz meets Jack.' } }),
      '/items/s/next-up': pilot,
      '/items/s/similar': [],
    })

    renderAt('/item/s')

    expect(await screen.findByRole('tab', { name: 'Season 1' })).toHaveAttribute('aria-selected', 'true')
    expect(await screen.findByRole('link', { name: 'Pilot' })).toBeInTheDocument()
    expect(screen.getByText('Liz meets Jack.')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /Play Season 01 \| Episode 01/ })).toBeInTheDocument()
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
})
