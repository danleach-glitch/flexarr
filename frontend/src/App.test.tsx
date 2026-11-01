import { waitFor, act, fireEvent, render, renderHook, screen, within } from '@testing-library/react'
import { MemoryRouter, useNavigate } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import App, { Shell } from './App'
import { TrailerDialog } from './components/TrailerDialog'
import { usePolling } from './hooks/usePolling'
import { deleteItem } from './api/client'
import { WATCHED_CHANGED } from './hooks/watched'
import { detail, mockApi, movies, status, summary } from './test/mockApi'

// The player's tests wait for streams to be asked for again, which takes a slow CI runner more than the default 5 s.
vi.setConfig({ testTimeout: 15_000 })

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

  it('says a library is empty when nothing in its folders was recognised, and keeps Coming soon', async () => {
    const upcoming = { id: 'u1', kind: 'Movie', title: 'Dune: Part Three', seriesTitle: null, seasonNumber: null, episodeNumber: null, date: '2026-12-18', when: 'Dec 18', overview: null, poster: null, still: null, libraryItemId: null, available: false, download: null }
    mockApi({
      '/system/status': status,
      '/libraries': [{ ...movies, itemCount: 0, scan: null }],
      '/home': { featured: [], overviews: {}, rows: [{ id: 'upcoming', title: 'Coming soon', style: 'upcoming', items: [], link: null }], upcoming: [upcoming], downloading: 0 },
    })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Your library is empty' })).toBeInTheDocument()
    expect(screen.getByText('Add files to your library folder, or connect Sonarr or Radarr.')).toBeInTheDocument()
    expect(screen.getByText('/media/movies')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Connect Sonarr or Radarr' })).toHaveAttribute('href', '/settings/connections')
    expect(screen.getByRole('heading', { name: 'Coming soon' })).toBeInTheDocument()
  })

  it('shows what Home found rather than an empty library when the library list is behind a scan', async () => {
    const film = summary({ id: 'm9', kind: 'Movie', title: 'Big Buck Bunny', poster: null, backdrop: null })
    mockApi({
      '/system/status': status,
      '/libraries': [{ ...movies, itemCount: 0, scan: null }],
      '/home': { featured: [], overviews: {}, rows: [{ id: 'recent-movies', title: 'Latest unwatched films', style: 'poster', items: [film], link: null }], upcoming: [], downloading: 0 },
    })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Latest unwatched films' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Your library is empty' })).not.toBeInTheDocument()
  })

  it('says an empty library is still being looked through during its first scan', async () => {
    mockApi({
      '/system/status': status,
      '/libraries': [{ ...movies, itemCount: 0 }],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Looking through your library' })).toBeInTheDocument()
    expect(screen.getByText(/reading the files in Movies \(4 of 10\)/)).toBeInTheDocument()
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

  it('marks a half-watched episode unwatched, and removes a show’s next episode, from Continue Watching', async () => {
    const userData = { positionSeconds: 0, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null }
    const started = summary({ id: 'e5', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 5, seriesTitle: 'Severance', seriesId: 's1', durationSeconds: 3000, userData: { ...userData, positionSeconds: 600 } })
    const next = summary({ id: 'f2', kind: 'Episode', title: 'Two', seasonNumber: 1, episodeNumber: 2, seriesTitle: 'Slow Horses', seriesId: 's2', durationSeconds: 3000, userData })
    let row = [started, next]
    const calls: string[] = []
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': () => ({ featured: [], overviews: {}, rows: [{ id: 'continue', title: 'Continue watching', style: 'wide', items: row, link: null }], upcoming: [], downloading: 0 }),
      'POST /items/e5/watched': (init?: RequestInit) => {
        calls.push(`watched ${JSON.parse(String(init?.body)).watched}`)
        row = row.filter((i) => i.id !== 'e5')
      },
      'DELETE /home/continue/f2': () => {
        calls.push('removed f2')
        row = row.filter((i) => i.id !== 'f2')
      },
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'More for Severance S01E05' }))
    let menu = screen.getByRole('menu')
    // Part way through: either way. Not next up, so no Remove.
    expect(within(menu).getByRole('menuitem', { name: 'Mark episode as watched' })).toBeInTheDocument()
    expect(within(menu).queryByRole('menuitem', { name: 'Remove from Continue Watching' })).not.toBeInTheDocument()
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Mark episode as unwatched' }))
    await vi.waitFor(() => expect(screen.queryByRole('button', { name: 'More for Severance S01E05' })).not.toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: 'More for Slow Horses S01E02' }))
    menu = screen.getByRole('menu')
    expect(within(menu).queryByRole('menuitem', { name: 'Mark episode as unwatched' })).not.toBeInTheDocument()
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Remove from Continue Watching' }))
    await vi.waitFor(() => expect(screen.queryByRole('button', { name: 'More for Slow Horses S01E02' })).not.toBeInTheDocument())
    expect(calls).toEqual(['watched false', 'removed f2'])
  })

  it('asks Home for books and podcasts, and removes them from Continue watching without marking them finished', async () => {
    const userData = { positionSeconds: 600, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null }
    const book = summary({ id: 'b1', kind: 'Audiobook', title: 'Project Hail Mary', durationSeconds: 7200, userData })
    const episode = summary({ id: 'p1', kind: 'PodcastEpisode', title: 'The long one', parentId: 'pod', seriesTitle: 'Hardcore History', durationSeconds: 3600, userData })
    let row = [book, episode]
    const asked: (string | null)[] = []
    const calls: string[] = []
    const remove = (id: string) => () => {
      calls.push(`removed ${id}`)
      row = row.filter((i) => i.id !== id)
    }
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': (_init: RequestInit | undefined, url: URL) => {
        asked.push(url.searchParams.get('listening'))
        return { featured: [], overviews: {}, rows: [{ id: 'continue', title: 'Continue watching or listening', style: 'wide', items: row, link: null }], upcoming: [], downloading: 0 }
      },
      'DELETE /home/continue/b1': remove('b1'),
      'DELETE /home/continue/p1': remove('p1'),
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'More for Project Hail Mary' }))
    let menu = screen.getByRole('menu')
    expect(within(menu).getByRole('menuitem', { name: 'Resume' })).toBeInTheDocument()
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Remove from Continue Watching' }))
    await vi.waitFor(() => expect(screen.queryByRole('button', { name: 'More for Project Hail Mary' })).not.toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: 'More for The long one' }))
    menu = screen.getByRole('menu')
    expect(within(menu).getByRole('menuitem', { name: 'Resume' })).toBeInTheDocument()
    fireEvent.click(within(menu).getByRole('menuitem', { name: 'Remove from Continue Watching' }))
    await vi.waitFor(() => expect(screen.queryByRole('button', { name: 'More for The long one' })).not.toBeInTheDocument())

    expect(calls).toEqual(['removed b1', 'removed p1'])
    expect(asked[0]).toBe('books,podcasts')
  })

  it('toggles watched from the tick on a poster', async () => {
    const posted: unknown[] = []
    mockApi({
      '/system/status': status,
      '/libraries': [movies],
      '/home': { featured: [], overviews: {}, rows: [{ id: 'recent', title: 'Latest unwatched films', style: 'poster', items: [niceGuys], link: null }], upcoming: [], downloading: 0 },
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

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
    expect(authorized).toBe(true)
    expect(localStorage.length).toBe(0)
  })

  it('sets up a new server from the first browser with its setup code, without a key', async () => {
    let claimed = false
    const sent: string[] = []
    mockApi({
      '/setup': () => ({ open: !claimed, codeRequired: true }),
      '/setup/claim': (init?: RequestInit) => {
        const { code, pin } = JSON.parse(String(init?.body)) as { code: string; pin: string }
        sent.push(`${code} ${pin}`)
        if (code !== 'ABCD-EFGH') return new Response(JSON.stringify({ title: 'Wrong code' }), { status: 400 })
        claimed = true
        return new Response(null, { status: 204 })
      },
      '/system/status': () => (claimed ? status : new Response('', { status: 401 })),
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)
    // The admin PIN comes with the code, so setup happens all at once.
    const fill = (code: string, pin: string, again = pin) => {
      fireEvent.change(screen.getByLabelText('Setup code'), { target: { value: code } })
      fireEvent.change(screen.getByLabelText('PIN (required)'), { target: { value: pin } })
      fireEvent.change(screen.getByLabelText('Confirm PIN'), { target: { value: again } })
      fireEvent.click(screen.getByRole('button', { name: 'Set up Flexarr' }))
    }
    await screen.findByLabelText('Setup code')
    fill('ABCD-EFGH', '2468', '2469')
    expect(await screen.findByText('The two PINs don’t match. Type the same one in both.')).toBeInTheDocument()
    fill('WRONG-ONE', '2468')
    expect(await screen.findByText("That isn't this server's setup code.")).toBeInTheDocument()

    // The whole link from the server's log, pasted in.
    fill('http://localhost:8383/#setup=ABCD-EFGH', '2468')

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
    expect(screen.queryByLabelText('API key')).not.toBeInTheDocument()
    expect(sent).toEqual(['WRONG-ONE 2468', 'ABCD-EFGH 2468'])
  })

  it('opened with the setup code in the link (from the Mac app), only asks for a PIN', async () => {
    const sent: string[] = []
    let claimed = false
    mockApi({
      '/setup': () => ({ open: !claimed, codeRequired: true, install: 'MacApp' }),
      '/setup/claim': (init?: RequestInit) => {
        const { code, pin } = JSON.parse(String(init?.body)) as { code: string; pin: string }
        sent.push(`${code} ${pin}`)
        claimed = true
        return new Response(null, { status: 204 })
      },
      '/system/status': () => (claimed ? status : new Response('', { status: 401 })),
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })
    window.history.replaceState(null, '', '/#setup=ABCD-EFGH')

    render(<App />)
    fireEvent.change(await screen.findByLabelText('PIN (required)'), { target: { value: '2468' } })
    fireEvent.change(screen.getByLabelText('Confirm PIN'), { target: { value: '2468' } })
    expect(screen.queryByLabelText('Setup code')).not.toBeInTheDocument()
    expect(window.location.hash).toBe('') // out of the address bar at once
    fireEvent.click(screen.getByRole('button', { name: 'Set up Flexarr' }))

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
    expect(sent).toEqual(['ABCD-EFGH 2468'])
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

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
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

  it('puts focus back on the card opened from Browse, in the row it was opened from', async () => {
    const heat = summary({ id: 'm2', title: 'Heat', year: 1995 })
    mockApi({
      '/libraries': [movies],
      '/libraries/lib-movies/browse': {
        featured: [niceGuys],
        overviews: {},
        rows: [
          { id: 'genre-Crime', title: 'Crime', style: 'poster', items: [niceGuys, heat], link: null },
          { id: 'recent', title: 'Recently added', style: 'poster', items: [heat], link: null },
        ],
        upcoming: [],
        downloading: 0,
      },
      '/items/m2': detail(heat),
      '/items/m2/similar': [],
    })
    function Back() {
      const navigate = useNavigate()
      return <button onClick={() => navigate(-1)}>Back</button>
    }
    render(
      <MemoryRouter initialEntries={['/library/lib-movies']}>
        <Shell />
        <Back />
      </MemoryRouter>,
    )
    const row = (title: string) => screen.getByRole('heading', { name: title }).closest('section') as HTMLElement

    await screen.findByRole('heading', { name: 'Recently added' })
    fireEvent.click(within(row('Recently added')).getByRole('link', { name: 'Heat' }))
    await screen.findByRole('heading', { level: 1, name: 'Heat' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))

    await screen.findByRole('heading', { name: 'Recently added' })
    await vi.waitFor(() => expect(within(row('Recently added')).getByRole('link', { name: 'Heat' })).toHaveFocus())
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
      '/search': { titles: [niceGuys], episodes: [], people: [{ id: 'p1', name: 'Ryan Gosling', image: null, credits: 4 }], genres: [], albums: [], tracks: [] },
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

  it('chooses library folders in the Mac folder window when the server runs in the Mac app', async () => {
    mockApi({
      '/libraries': [],
      '/filesystem': { path: '/Volumes', parent: '/', directories: [], chooseOnMac: true },
      'POST /filesystem/choose': { path: '/Volumes/Media/Films' },
    })

    renderAt('/settings/libraries')
    fireEvent.click(await screen.findByRole('button', { name: 'Add library' }))
    fireEvent.click(await screen.findByRole('button', { name: /Choose a folder on the Mac/ }))

    expect(await screen.findByText('/Volumes/Media/Films')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Use this folder' })).not.toBeInTheDocument()
    // The first library's form stands in for the list, with no empty list below it.
    expect(document.querySelector('.library-list')).toBeNull()
  })

  it('renames a library in place, keeping its folders', async () => {
    let library = { ...movies, scan: null }
    const sent: unknown[] = []
    mockApi({
      '/libraries': () => [library],
      '/library-health': { score: null },
      'PUT /libraries/lib-movies': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body))
        sent.push(body)
        library = { ...library, name: body.name }
        return library
      },
    })

    renderAt('/settings/libraries')
    fireEvent.click(await screen.findByRole('button', { name: 'Rename Movies' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Name for Movies' }), { target: { value: '  Films ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('button', { name: 'Rename Films' })).toBeInTheDocument()
    expect(sent).toEqual([{ name: 'Films', type: 'Movies', folders: ['/media/movies'] }])
  })

  it('sends a profile that isn’t the main one from a server page to its own', async () => {
    mockApi({
      '/libraries': [],
      '/me': { profileId: 'c1', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null },
      '/profiles': [{ id: 'c1', name: 'Claire', isMain: false, hasPin: false, isCurrent: true }],
      '/me/playback-preferences': { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Standard' },
    })

    renderAt('/settings/server')

    expect(await screen.findByRole('switch', { name: /Skip intros/ })).toBeInTheDocument()
    expect(screen.queryByText(/Server/)).not.toBeInTheDocument()
  })

  it('gives another profile its own pages, and nothing of the server’s', async () => {
    mockApi({
      '/libraries': [],
      '/me': { profileId: 'c1', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null },
      '/profiles': [{ id: 'c1', name: 'Claire', isMain: false, hasPin: false, isCurrent: true }],
      '/me/playback-preferences': { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Standard' },
    })

    renderAt('/settings')

    expect(await screen.findByRole('switch', { name: /Skip intros/ })).not.toBeChecked()
    expect(screen.getByRole('heading', { name: 'Claire’s profile' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Playback' })).toHaveClass('active')
    expect(screen.queryByRole('link', { name: 'Libraries' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Server' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Manage the server' })).not.toBeInTheDocument()
  })

  it('doesn’t call a new server’s main profile “Main profile’s profile”', async () => {
    mockApi({
      '/libraries': [],
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null },
      '/profiles': [{ id: 'owner', name: 'Main profile', isMain: true, hasPin: false, isCurrent: true }],
    })

    renderAt('/settings/playback')

    expect(await screen.findByRole('heading', { name: 'Main profile' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Main profile’s profile' })).not.toBeInTheDocument()
  })

  it('saves Home’s rows in the order they’re dragged into', async () => {
    let saved: { home: { id: string }[] } | undefined
    const row = (id: string) => ({ id, label: id, enabled: true })
    mockApi({
      '/libraries': [],
      '/settings/layout': { home: [row('Continue'), row('Latest'), row('Genres')], movies: [], shows: [], combineLibraries: true, combinable: false },
      'PUT /settings/layout': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return undefined
      },
    })

    renderAt('/settings/layout')
    const first = (await screen.findByText('Continue')).closest('li')!
    const last = screen.getByText('Genres').closest('li')!
    const dataTransfer = { setData: vi.fn(), effectAllowed: '', dropEffect: '' }
    fireEvent.dragStart(first, { dataTransfer })
    fireEvent.dragOver(last, { dataTransfer })
    // Moving down, it lands under the row it's dropped on.
    expect(last).toHaveClass('drop-below')
    fireEvent.drop(last, { dataTransfer })

    await vi.waitFor(() => expect(saved?.home.map((r) => r.id)).toEqual(['Latest', 'Genres', 'Continue']))
  })

  it('combines libraries on Home, then shows the rows the server answers with', async () => {
    let saved: { combineLibraries: boolean } | undefined
    const row = (id: string, label = id) => ({ id, label, enabled: true })
    mockApi({
      '/libraries': [],
      '/settings/layout': { home: [row('continue'), row('recent-shows', 'Latest unwatched shows, from every TV library')], movies: [], shows: [], combineLibraries: true, combinable: true },
      'PUT /settings/layout': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return { home: [row('continue'), row('recent-a', 'Latest unwatched in TV'), row('recent-b', 'Latest unwatched in Anime')], movies: [], shows: [], combineLibraries: false, combinable: true }
      },
    })

    renderAt('/settings/layout')
    const combine = await screen.findByRole('switch', { name: /Combine libraries of the same kind/ })
    expect(combine).toBeChecked()
    fireEvent.click(combine)

    expect(await screen.findByText('Latest unwatched in Anime')).toBeInTheDocument()
    expect(screen.queryByText('Latest unwatched shows, from every TV library')).not.toBeInTheDocument()
    expect(saved?.combineLibraries).toBe(false)
    expect(combine).not.toBeChecked()
  })

  it('sends old links to where their pages moved', async () => {
    mockApi({ '/libraries': [], '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null }, '/sign-ins': [] })

    renderAt('/settings/access')

    expect(await screen.findByRole('heading', { level: 2, name: 'Remote access' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Remote access' })).toHaveClass('active')
  })

  it('a server without an admin PIN asks for one before anything in its settings', async () => {
    let sent: unknown
    mockApi({
      '/libraries': [],
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null, canUnlock: true, adminPinMissing: true },
      'POST /admin/pin': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return { unlockedUntil: new Date(Date.now() + 900_000).toISOString() }
      },
    })
    const reload = vi.fn()
    vi.stubGlobal('location', { ...window.location, reload })

    renderAt('/settings/overview')
    expect(await screen.findByRole('heading', { name: 'Choose an admin PIN' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '2468' } })
    fireEvent.change(screen.getByLabelText('Confirm PIN'), { target: { value: '2468' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save PIN' }))

    await vi.waitFor(() => expect(reload).toHaveBeenCalled())
    expect(sent).toMatchObject({ pin: '2468' })
    vi.unstubAllGlobals()
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

    renderAt('/settings/security')

    // Hidden behind dots; the eye asks for the admin PIN first (assumed until /me says there's none).
    expect(await screen.findByLabelText('API key, hidden')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Show the API key' }))
    fireEvent.change(await screen.findByLabelText('Admin PIN'), { target: { value: '2580' } })
    fireEvent.click(screen.getByRole('button', { name: 'Show the key' }))
    expect(await screen.findByText('the-key')).toBeInTheDocument()
    const reveal = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/settings/api-key/reveal'))
    expect(JSON.parse(String(reveal?.[1]?.body))).toMatchObject({ pin: '2580', confirm: true })
    expect(screen.getByRole('button', { name: 'Copy the API key' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Hide the API key' }))
    expect(screen.queryByText('the-key')).not.toBeInTheDocument()
    expect(await screen.findByText('Living room')).toBeInTheDocument()
    // Each Sign out says which one it is, and asks first.
    fireEvent.click(screen.getByRole('button', { name: /^Sign out Living room, connected/ }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Sign out Living room?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Sign out' }))
    await vi.waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/sign-ins/s1') && init?.method === 'DELETE')).toBe(true))
  })

  it('in Docker, says Tailscale can’t be turned on rather than asking to install it', async () => {
    mockApi({
      '/libraries': [],
      '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null },
      '/sign-ins': [],
      '/remote': {
        yourConnection: 'Local',
        yourTailscaleLogin: null,
        tailscale: { installed: false, running: false, dnsName: null, ownerLogin: null, privateAccess: false, publicAccess: false, people: [], problem: 'Tailscale isn’t installed on the server.', url: null },
        links: [],
        cloudflareTunnelRunning: false,
        mainProfileHasPin: false,
        inContainer: true,
      },
    })

    renderAt('/settings/remote')

    expect(await screen.findByText('Not available in Docker')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Watch away from home' })).toHaveAttribute('href', 'https://flexarr.io/guide/away-from-home')
    expect(screen.queryByText(/Install Tailscale on the server/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Turn on' })).not.toBeInTheDocument()
  })

  it('in the Mac app, explains Tailscale and shows the Mac’s tailnet address instead of Turn on', async () => {
    mockApi({
      '/remote': {
        yourConnection: 'Local',
        yourTailscaleLogin: null,
        tailscale: { installed: true, running: false, dnsName: null, ownerLogin: null, privateAccess: false, publicAccess: false, people: [], problem: 'Operation not permitted', url: null },
        links: [],
        cloudflareTunnelRunning: false,
        mainProfileHasPin: false,
        inContainer: false,
        inMacApp: true,
        tailnetAddress: 'http://100.64.1.2:8383',
      },
    })

    renderAt('/settings/remote')

    expect(await screen.findByText('This Mac is on your tailnet')).toBeInTheDocument()
    expect(screen.getByText('http://100.64.1.2:8383')).toBeInTheDocument()
    expect(screen.queryByText('Operation not permitted')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Turn on' })).not.toBeInTheDocument()
  })

  it('says how many were signed out with a new key', async () => {
    const fetchMock = mockApi({
      '/libraries': [],
      '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null, dataDirectory: '/data', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', webhookSecret: 'hook-secret' },
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null, adminPinMissing: false },
      'POST /settings/api-key/rotate': { apiKey: 'new-key', signedOut: 3 },
      '/sign-ins': [],
    })

    renderAt('/settings/security')

    // With an admin PIN, the eye asks for it before showing the key.
    fireEvent.click(await screen.findByRole('button', { name: 'Show the API key' }))
    expect(await screen.findByRole('form', { name: 'Show the API key' })).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/api-key/reveal'))).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Make a new key' }))
    // With an admin PIN, the PIN field takes the focus.
    const pin = await screen.findByLabelText('Admin PIN')
    await vi.waitFor(() => expect(pin).toHaveFocus())
    fireEvent.change(pin, { target: { value: '1234' } })
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Replace the key' }))
    expect(await screen.findByText(/^New key made/)).toHaveTextContent('New key made. The old one stopped working. 3 browsers and TVs were signed out.')
    expect(screen.getByText('new-key')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy the API key' })).toBeInTheDocument()
  })

  it('lists the webhook addresses, and asks before a new secret in its own dialog', async () => {
    const fetchMock = mockApi({
      '/libraries': [],
      '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null, dataDirectory: '/data', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', webhookSecret: 'hook-secret' },
      '/arr/status': [],
      '/requests/status': { configured: false, reachable: false },
      'POST /settings/webhook-secret/rotate': { webhookSecret: 'fresh' },
    })

    renderAt('/settings/connections')

    expect(await screen.findByText(/webhooks\/sonarr\?secret=hook-secret/)).toBeInTheDocument()
    const opener = screen.getByRole('button', { name: 'Make a new secret' })
    opener.focus()
    fireEvent.click(opener)
    expect(await screen.findByRole('alertdialog', { name: 'Make a new webhook secret?' })).toBeInTheDocument()
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/webhook-secret/rotate'))).toBe(false)

    fireEvent.click(opener)
    fireEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Make a new secret' }))
    expect(await screen.findByText(/webhooks\/radarr\?secret=fresh/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy the Radarr address' })).toBeInTheDocument()
  })

  it('says where each app’s folders are, warns on the app’s line, and takes a suggested mapping', async () => {
    let saved: unknown
    const settings = { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], seerr: null, dataDirectory: '/data', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', webhookSecret: 'x' }
    mockApi({
      '/libraries': [],
      '/settings': { ...settings, arr: [{ name: 'Sonarr', kind: 'Sonarr', baseUrl: 'http://sonarr:8989', hasApiKey: true }, { name: 'Radarr', kind: 'Radarr', baseUrl: 'http://radarr:7878', hasApiKey: true }] },
      '/arr/status': [
        { name: 'Sonarr', kind: 'Sonarr', ok: true, version: '4.0', error: null },
        { name: 'Radarr', kind: 'Radarr', ok: true, version: '5.0', error: null },
      ],
      '/arr/folders': [
        { app: 'Sonarr', kind: 'Sonarr', path: '/tv', state: 'Suggested', local: null, suggested: { from: '/tv', to: '/Volumes/Media/TV' }, agreeing: 2, compared: 2, certain: false },
        { app: 'Radarr', kind: 'Radarr', path: '/Volumes/Media/Films', state: 'Found', local: '/Volumes/Media/Films', suggested: null, agreeing: 0, compared: 0, certain: false },
      ],
      '/requests/status': { configured: false, reachable: false },
      'PUT /settings': (init: RequestInit) => {
        saved = JSON.parse(String(init.body))
        return { ...settings, arr: [], pathMappings: [{ from: '/tv', to: '/Volumes/Media/TV' }] }
      },
    })

    renderAt('/settings/connections')

    expect(await screen.findByText(/folder isn’t at that path on this server/)).toBeInTheDocument()
    expect(screen.getByText(/Not at that path here, but the 2 shows in both are in/)).toBeInTheDocument()
    expect(screen.getByText('At the same path on this server')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Use this' }))
    await vi.waitFor(() => expect(saved).toEqual({ pathMappings: [{ from: '/tv', to: '/Volumes/Media/TV' }] }))
  })

  it('groups a browser that signed in again and again, marks only newer ones New, and renames one', async () => {
    const now = Date.now()
    const at = (hoursAgo: number) => new Date(now - hoursAgo * 3_600_000).toISOString()
    const chrome = (id: string, hoursAgo: number) => ({ id, kind: 'Browser', name: 'Chrome on Mac', createdAt: at(hoursAgo), lastUsedAt: at(hoursAgo), zone: 'Local', address: '127.0.0.1', profileId: 'owner', profileName: 'Dan', isThisOne: false })
    let renamed: unknown
    mockApi({
      '/libraries': [],
      '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null, dataDirectory: '/data', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', webhookSecret: 'x' },
      '/sign-ins': [
        { ...chrome('me', 1), name: 'Safari on iPhone', isThisOne: true },
        chrome('c1', 2),
        chrome('c2', 3),
        { id: 'tv', kind: 'Device', name: 'Apple TV', createdAt: at(0.5), lastUsedAt: at(0.5), zone: 'Local', address: '192.168.1.9', profileId: 'owner', profileName: 'Dan', isThisOne: false },
      ],
      'PATCH /sign-ins/tv': (init: RequestInit) => {
        renamed = JSON.parse(String(init.body))
        return undefined
      },
    })

    renderAt('/settings/security')

    expect(await screen.findByText('2 sign-ins')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign out all 2 sign-ins of Chrome on Mac' })).toBeInTheDocument()
    // Only the TV signed in after this browser did.
    expect(screen.getAllByText('New')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Sign out every other browser and TV' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /^Rename Apple TV/ }))
    const name = screen.getByLabelText('Name for Apple TV')
    fireEvent.change(name, { target: { value: 'Bedroom TV' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await vi.waitFor(() => expect(renamed).toEqual({ name: 'Bedroom TV' }))
  })

  it('declines a TV’s code with Don’t allow, so the TV shows a new one', async () => {
    const fetchMock = mockApi({
      '/libraries': [],
      '/pairing/code/ABC-DEF': { code: 'ABC-DEF', deviceName: 'Living room', expiresAt: new Date().toISOString(), requestedFrom: '192.168.1.70', zone: 'Local' },
      'POST /pairing/code/ABC-DEF/decline': undefined,
    })

    renderAt('/link?code=ABC-DEF')

    expect(await screen.findByText('ABC-DEF', { selector: '.connect-tv-big-code' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Allow' })).toHaveFocus()
    fireEvent.click(screen.getByRole('button', { name: 'Don’t allow' }))
    expect(await screen.findByRole('status')).toHaveTextContent('The device will show a new code')
    expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/pairing/code/ABC-DEF/decline') && init?.method === 'POST')).toBe(true)
  })

  it('offers the main profile when another profile opens a TV’s link', async () => {
    mockApi({
      '/libraries': [],
      '/pairing/code/ABC-DEF': new Response(JSON.stringify({ title: 'Forbidden' }), { status: 403 }),
    })

    renderAt('/link?code=ABC-DEF')

    expect(await screen.findByText('Only the main profile can connect a device.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Switch to the main profile' })).toBeInTheDocument()
  })

  it('names the library in a held folder’s removal, and closes on Escape', async () => {
    mockApi({
      '/libraries': [],
      '/library-health': {
        score: { score: 100, titles: 10, withProblems: 0, duplicates: 0, unreadable: 0, standardDefinition: 0, missingArtwork: 0, fileProblems: 0, artworkNeedsConnection: 4 },
        duplicates: [],
        reclaimableBytes: 0,
        unreadable: [],
        missingArtwork: [],
        matchedByTitle: [],
        standardDefinition: [],
        standardDefinitionTotal: 0,
        downloading: {},
        held: [{ folderId: 1, library: 'Films', libraryType: 'Movies', path: '/Volumes/Media/films', reason: 'MostFilesGone', missing: 4, known: 9, since: new Date().toISOString(), scans: 3, confirmed: false, canConfirm: true }],
      },
      '/library-health/check': { running: false, checked: 0, total: 0, finishedAt: null, problems: [] },
    })

    renderAt('/settings/health')

    expect(await screen.findByText(/folder on hold/)).toHaveTextContent('1 folder on hold')
    expect(screen.getByText(/without artwork/)).toHaveTextContent('4 without artwork: connect Radarr or Sonarr to fill it in')
    fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove 4 films from Films?' })
    expect(within(dialog).getByText('/Volumes/Media/films')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Not yet' })).toHaveFocus()
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
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
    // The title shows before the first stream is attached; an error before then isn't a stopped transcode yet.
    await vi.waitFor(() => expect(video.src).toContain('/playback/s1/'), { timeout: 5000 })
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 352 })

    // The server restarted, say: the stream ends, and the player asks for a fresh one at the same spot.
    act(() => {
      video.dispatchEvent(new Event('error'))
    })
    await vi.waitFor(() => expect(video.src).toContain('/playback/s2/'), { timeout: 5000 })
    expect(requests[1].startPositionSeconds).toBe(352)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

    act(() => {
      video.dispatchEvent(new Event('error'))
    })
    expect(await screen.findByRole('alertdialog')).toHaveTextContent('The stream stopped')
    expect(requests).toHaveLength(2)
  })

  const transcode = (sessionId: string) => ({
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
    item: summary({ id: 'e1', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 1 }),
    ancestors: [],
    next: null,
    transcodeSummary: null,
  })
  const problem = (status: number, detail: string) => new Response(JSON.stringify({ detail }), { status })
  const takenOver = 'Someone started watching on another screen, so this one had to stop.'

  it('says another screen took over, and Watch here instead starts afresh rather than being refused again', async () => {
    const requests: { replacesSessionId?: string }[] = []
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => {
        requests.push(JSON.parse(String(init?.body)))
        // The first stream was ended for another screen: asking with its id is refused for as long as the server remembers.
        if (requests.at(-1)!.replacesSessionId === 's1') return problem(410, takenOver)
        return transcode(`s${requests.length}`)
      },
      'DELETE /playback/s1': undefined,
      'DELETE /playback/s3': undefined,
    })

    renderAt('/play/e1')
    await screen.findByText('Pilot')
    const video = document.querySelector('video')!
    // The title shows before the first stream is attached; an error before then isn't a stopped transcode yet.
    await vi.waitFor(() => expect(video.src).toContain('/playback/s1/'), { timeout: 5000 })
    act(() => {
      video.dispatchEvent(new Event('error'))
    })

    const dialog = await screen.findByRole('alertdialog')
    expect(dialog).toHaveTextContent(takenOver)
    expect(dialog).toHaveTextContent('The other screen will stop')
    const watchHere = within(dialog).getByRole('button', { name: 'Watch here instead' })
    expect(watchHere).toHaveFocus()
    // Nothing behind it can be used meanwhile.
    expect(screen.queryByRole('button', { name: 'Play' })).not.toBeInTheDocument()

    fireEvent.click(watchHere)
    await vi.waitFor(() => expect(video.src).toContain('/playback/s3/'), { timeout: 5000 })
    expect(requests.map((r) => r.replacesSessionId)).toEqual([undefined, 's1', undefined])
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('says another screen took over when the stream itself says so, without asking for it back', async () => {
    const requests: unknown[] = []
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => {
        requests.push(JSON.parse(String(init?.body)))
        return transcode(`s${requests.length}`)
      },
      '/playback/s1/main.m3u8': problem(410, takenOver),
      'DELETE /playback/s2': undefined,
    })

    renderAt('/play/e1')
    await screen.findByText('Pilot')
    const video = document.querySelector('video')!
    await vi.waitFor(() => expect(video.src).toContain('/playback/s1/'), { timeout: 5000 })
    act(() => {
      video.dispatchEvent(new Event('error'))
    })

    // The dialog can open with the plain "stopped" message and take the server's reason a moment later (CI runners
    // are slow), so wait for the text rather than the dialog.
    await waitFor(() => expect(screen.getByRole('alertdialog')).toHaveTextContent(takenOver), { timeout: 5000 })
    expect(requests).toHaveLength(1)
  })

  it('tells the server when subtitles are switched in the browser, so the choice holds next time', async () => {
    const choices: unknown[] = []
    const english = { key: 'e2', label: 'English', codec: 'subrip', language: 'eng', isForced: false, isDefault: false, isText: true, url: '/api/v1/playback/s1/subtitles/e2.vtt' }
    mockApi({
      '/libraries': [],
      'POST /playback': () => ({ ...transcode('s1'), subtitles: [english], selectedSubtitleKey: 'e2' }),
      'PUT /playback/s1/subtitle': (init?: RequestInit) => {
        choices.push(JSON.parse(String(init?.body)))
      },
    })

    renderAt('/play/e1')
    await screen.findByText('Pilot')

    // The subtitles shortcut: off, since they're on. The title can show before the stream's details arrive, and a press
    // before then does nothing, so press until the choice goes out.
    await waitFor(() => {
      fireEvent.keyDown(window, { key: 'c' })
      expect(choices).toEqual([{ key: 'off' }])
    })
  })

  it('recovers once when the stream errors twice while asking why it stopped', async () => {
    const requests: unknown[] = []
    const answers: ((response: Response) => void)[] = []
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => {
        requests.push(JSON.parse(String(init?.body)))
        return transcode(`s${requests.length}`)
      },
      '/playback/s1/main.m3u8': () => new Promise<Response>((resolve) => answers.push(resolve)),
      'DELETE /playback/s1': undefined,
      'DELETE /playback/s2': undefined,
    })

    renderAt('/play/e1')
    await screen.findByText('Pilot')
    const video = document.querySelector('video')!
    act(() => {
      video.dispatchEvent(new Event('error'))
      video.dispatchEvent(new Event('error'))
    })
    await act(async () => answers.forEach((answer) => answer(new Response('', { status: 404 }))))

    await vi.waitFor(() => expect(video.src).toContain('/playback/s2/'), { timeout: 5000 })
    expect(requests).toHaveLength(2)
    expect(screen.queryByText(/The stream stopped/)).not.toBeInTheDocument()
  })

  it('says plainly when the server is busy, with Try again', async () => {
    mockApi({
      '/libraries': [],
      'POST /playback': problem(503, 'Flexarr is busy playing on other screens. Try again when one of them finishes.'),
    })

    renderAt('/play/e1')

    const dialog = await screen.findByRole('alertdialog')
    expect(dialog).toHaveTextContent('Flexarr is busy playing on other screens')
    expect(within(dialog).getByRole('button', { name: 'Try again' })).toHaveFocus()
  })
})

describe('Player watched announcement', () => {
  it('names the episode just played when moving on to the next, and nothing at the start', async () => {
    const pilot = summary({ id: 'e1', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 1, seriesId: 's' })
    const second = summary({ id: 'e2', kind: 'Episode', title: 'Second', seasonNumber: 1, episodeNumber: 2, seriesId: 's' })
    const playback = (item: typeof pilot, next: typeof pilot | null) => ({
      sessionId: item.id,
      method: 'DirectPlay',
      url: `/api/v1/playback/${item.id}/stream`,
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
      item,
      ancestors: [],
      next,
      transcodeSummary: null,
    })
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => (JSON.parse(String(init?.body)).itemId === 'e2' ? playback(second, null) : playback(pilot, second)),
      'DELETE /playback/e1': undefined,
      'DELETE /playback/e2': undefined,
    })
    const announced: string[][] = []
    const listener = (e: Event) => announced.push((e as CustomEvent<string[]>).detail)
    window.addEventListener(WATCHED_CHANGED, listener)
    try {
      renderAt('/play/e1')
      await screen.findByText('Pilot')
      expect(announced).toEqual([])

      fireEvent.click(screen.getByRole('button', { name: 'Next episode' }))
      await screen.findByText('Second')
      await vi.waitFor(() => expect(announced).toEqual([['e1', 's']]))
    } finally {
      window.removeEventListener(WATCHED_CHANGED, listener)
    }
  })

  it('goes back to the episode before with Previous at the start of an episode', async () => {
    const pilot = summary({ id: 'e1', kind: 'Episode', title: 'Pilot', seasonNumber: 1, episodeNumber: 1, seriesId: 's' })
    const second = summary({ id: 'e2', kind: 'Episode', title: 'Second', seasonNumber: 1, episodeNumber: 2, seriesId: 's' })
    const playback = (item: typeof pilot, previous: typeof pilot | null) => ({
      sessionId: item.id,
      method: 'DirectPlay',
      url: `/api/v1/playback/${item.id}/stream`,
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
      item,
      ancestors: [],
      next: null,
      previous,
      transcodeSummary: null,
    })
    mockApi({
      '/libraries': [],
      'POST /playback': (init?: RequestInit) => (JSON.parse(String(init?.body)).itemId === 'e1' ? playback(pilot, null) : playback(second, pilot)),
      'DELETE /playback/e1': undefined,
      'DELETE /playback/e2': undefined,
    })

    renderAt('/play/e2')
    await screen.findByText('Second')

    fireEvent.click(screen.getByRole('button', { name: 'Previous' }))
    await screen.findByText('Pilot')
    // Nothing before the first episode: Previous stays, and starts it again.
    expect(screen.getByRole('button', { name: 'Previous' })).toBeEnabled()
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

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
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

  const pinnedMain = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }
  const tailscaleOwner = { profileId: 'owner', method: 'tailscale', zone: 'Tailnet', isAdmin: false, tailscaleLogin: 'dan@example.com' }

  it('never keeps the main profile’s token, even with a PIN', async () => {
    const profiles = [pinnedMain, { id: 'c1', name: 'Sam', color: '#e8a0bf', hasPin: false, isMain: false, isCurrent: false, hiddenLibraryIds: [], image: null }]
    mockApi({
      '/system/status': status,
      '/profiles': profiles,
      'POST /profiles/owner/select': { token: 'owner.sig', profile: pinnedMain },
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: /Dan/ }))
    fireEvent.change(await screen.findByLabelText('PIN'), { target: { value: '24681357' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
    expect(localStorage.getItem('flexarr.profile')).toBeNull()
  })

  it('over Tailscale, drops a main-profile token kept by an earlier version and asks for the PIN, which can wait', async () => {
    localStorage.setItem('flexarr.profile', 'owner.kept-by-an-earlier-version')
    mockApi({
      '/system/status': status,
      '/profiles': [pinnedMain],
      '/me': tailscaleOwner,
      '/libraries': [],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
    })

    render(<App />)

    expect(await screen.findByRole('button', { name: /Dan/ })).toBeInTheDocument()
    expect(localStorage.getItem('flexarr.profile')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Not now' }))
    expect(await screen.findByRole('heading', { name: 'Nothing to watch yet' })).toBeInTheDocument()
  })

  it('over Tailscale, Switch profile opens the picker', async () => {
    sessionStorage.setItem('flexarr.pickProfile', '1') // what Switch profile leaves before reloading
    mockApi({ '/system/status': status, '/profiles': [pinnedMain], '/me': tailscaleOwner })

    render(<App />)

    expect(await screen.findByRole('button', { name: /Dan/ })).toBeInTheDocument()
    sessionStorage.clear()
  })

  it('locked, the server’s settings ask for the admin PIN, or the API key when it’s forgotten', async () => {
    const sent: unknown[] = []
    mockApi({
      '/libraries': [],
      '/me': { ...tailscaleOwner, canUnlock: true },
      '/profiles': [pinnedMain],
      'POST /admin/unlock': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)))
        return new Response(JSON.stringify({ title: 'That PIN isn’t right.' }), { status: 403 })
      },
      'POST /admin/pin': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)))
        return { unlockedUntil: new Date(Date.now() + 900_000).toISOString() }
      },
    })
    const reload = vi.fn()
    vi.stubGlobal('location', { ...window.location, reload })

    renderAt('/settings/libraries')
    expect(await screen.findByRole('heading', { name: 'Settings are locked' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Unlock settings' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Admin PIN'), { target: { value: '1111' } })
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Forgotten it?' }))
    fireEvent.change(screen.getByLabelText('API key'), { target: { value: 'the-key' } })
    fireEvent.change(screen.getByLabelText('New PIN'), { target: { value: '97531' } })
    fireEvent.change(screen.getByLabelText('Confirm new PIN'), { target: { value: '97531' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save PIN' }))

    await vi.waitFor(() => expect(reload).toHaveBeenCalled())
    expect(sent).toEqual([{ pin: '1111' }, { pin: '97531', apiKey: 'the-key' }])
    vi.unstubAllGlobals()
  })

  it('changing the admin PIN asks for the current one, in Security', async () => {
    let sent: Record<string, unknown> = {}
    mockApi({
      '/libraries': [],
      '/libraries/all': [],
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null },
      '/profiles': [pinnedMain],
      '/settings': { authenticationRequired: 'Enabled' },
      '/sign-ins': [],
      'POST /admin/pin': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return { unlockedUntil: null }
      },
    })

    renderAt('/settings/security')
    fireEvent.click(await screen.findByRole('button', { name: 'Change' }))
    const form = screen.getByRole('form', { name: 'Change the admin PIN' })
    fireEvent.change(within(form).getByLabelText('Current PIN'), { target: { value: '1234' } })
    fireEvent.change(within(form).getByLabelText('New PIN'), { target: { value: '97531' } })
    fireEvent.change(within(form).getByLabelText('Confirm new PIN'), { target: { value: '97531' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Change PIN' }))

    await vi.waitFor(() => expect(sent).toMatchObject({ pin: '97531', currentPin: '1234' }))
    expect(await screen.findByText(/PIN changed/)).toBeInTheDocument()
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

  it('dims letters with nothing to jump to, once the grid has loaded past them', async () => {
    grid()
    renderAt('/library/lib-movies?view=all')

    const rail = await screen.findByRole('navigation', { name: 'Jump to letter' })
    // Every title so far starts with F, and the grid is sorted A–Z: A to E are known to be empty, the rest aren't yet.
    expect(within(rail).getByRole('button', { name: 'E' })).toBeDisabled()
    expect(within(rail).getByRole('button', { name: 'F' })).toBeEnabled()
    expect(within(rail).getByRole('button', { name: 'G' })).toBeEnabled()
    expect(within(rail).getByRole('button', { name: 'Numbers and symbols' })).toBeEnabled()

    fireEvent.click(screen.getByRole('button', { name: 'Show 10 more' }))
    await screen.findByRole('link', { name: 'Film 209' })
    expect(within(rail).getByRole('button', { name: 'G' })).toBeDisabled()
    expect(within(rail).getByRole('button', { name: 'Numbers and symbols' })).toBeDisabled()
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
    // The page opens at the top, then the grid goes back to where it was, with focus on the title that was opened.
    await vi.waitFor(() => expect(scrolls).toHaveBeenCalledTimes(2))
    await vi.waitFor(() => expect(screen.getByRole('link', { name: 'Film 003' })).toHaveFocus())

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search Movies' }), { target: { value: 'Film 1' } })
    await vi.waitFor(() => expect(pages).toEqual([0, 200, 0]))
    await new Promise((resolve) => requestAnimationFrame(resolve))
    expect(scrolls).toHaveBeenCalledTimes(2)
  })
})

describe('Library grid going back', () => {
  function Nav() {
    const navigate = useNavigate()
    return (
      <>
        <button onClick={() => navigate(-1)}>Back</button>
        <button onClick={() => navigate('/item/f1')}>Elsewhere</button>
      </>
    )
  }
  function renderGrid(path = '/library/lib-movies?view=all') {
    render(
      <MemoryRouter initialEntries={[path]}>
        <Shell />
        <Nav />
      </MemoryRouter>,
    )
  }
  /** A small library whose answers the test can change and hold back. */
  function server(count: number) {
    const state = {
      films: Array.from({ length: count }, (_, i) => summary({ id: `f${i}`, title: `Film ${i}`, year: 2000 + i })),
      hold: undefined as Promise<void> | undefined,
      pages: [] as string[],
      lists: 0,
    }
    mockApi({
      '/libraries': () => {
        state.lists++
        return [{ ...movies, scan: null, itemCount: state.films.length }]
      },
      '/libraries/lib-movies/genres': [],
      '/libraries/lib-movies/items': async (_: RequestInit | undefined, url: URL) => {
        const sort = url.searchParams.get('sort') ?? ''
        state.pages.push(sort)
        await state.hold
        const items = sort === 'year' ? [...state.films].reverse() : state.films
        return { total: items.length, items }
      },
      '/items/f1': () => detail(state.films[1]),
      '/items/f1/similar': [],
      'DELETE /items/f1': () => {
        state.films = state.films.filter((f) => f.id !== 'f1')
      },
    })
    return state
  }
  function held(state: { hold: Promise<void> | undefined }) {
    let release = () => {}
    state.hold = new Promise<void>((resolve) => (release = resolve))
    return () => {
      state.hold = undefined
      release()
    }
  }
  const titles = () => screen.getAllByRole('link', { name: /^Film \d+$/ }).map((l) => l.textContent)

  it('loads afresh on Back when it was left before the first page arrived', async () => {
    const state = server(3)
    const release = held(state)
    renderGrid()
    await vi.waitFor(() => expect(state.pages).toHaveLength(1))

    fireEvent.click(screen.getByRole('button', { name: 'Elsewhere' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))
    release()

    expect(await screen.findByRole('link', { name: 'Film 2' })).toBeInTheDocument()
  })

  it('shows the new sort on Back when it was left before that sort loaded', async () => {
    const state = server(3)
    renderGrid()
    await screen.findByRole('link', { name: 'Film 2' })
    expect(titles()).toEqual(['Film 0', 'Film 1', 'Film 2'])

    const release = held(state)
    fireEvent.change(screen.getByRole('combobox', { name: 'Sort by' }), { target: { value: 'year' } })
    await vi.waitFor(() => expect(state.pages).toEqual(['', 'year']))
    fireEvent.click(screen.getByRole('button', { name: 'Elsewhere' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))
    release()

    await vi.waitFor(() => expect(titles()).toEqual(['Film 2', 'Film 1', 'Film 0']))
  })

  it('picks up titles that arrived while away, in a small library', async () => {
    const state = server(3)
    renderGrid()
    fireEvent.click(await screen.findByRole('link', { name: 'Film 1' }))
    await screen.findByRole('heading', { name: 'Film 1' })

    // Sonarr imported one meanwhile, and the library list hears of it.
    state.films = [...state.films, summary({ id: 'f3', title: 'Film 3' })]
    const lists = state.lists
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    await vi.waitFor(() => expect(state.lists).toBe(lists + 1))
    await new Promise((resolve) => setTimeout(resolve, 0))
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))

    expect(await screen.findByRole('link', { name: 'Film 3' })).toBeInTheDocument()
  })

  const frame = () => new Promise((resolve) => requestAnimationFrame(resolve))

  it('puts focus back on the title that had it when leaving', async () => {
    server(3)
    renderGrid()
    ;(await screen.findByRole('link', { name: 'Film 2' })).focus()
    fireEvent.click(screen.getByRole('button', { name: 'Elsewhere' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))

    await vi.waitFor(() => expect(screen.getByRole('link', { name: 'Film 2' })).toHaveFocus())
  })

  it("doesn't put focus back on a title opened on an earlier visit", async () => {
    server(3)
    renderGrid()
    fireEvent.click(await screen.findByRole('link', { name: 'Film 1' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))
    await vi.waitFor(() => expect(screen.getByRole('link', { name: 'Film 1' })).toHaveFocus())

    // This time it's left by something outside the grid.
    const elsewhere = screen.getByRole('button', { name: 'Elsewhere' })
    elsewhere.focus()
    fireEvent.click(elsewhere)
    await screen.findByRole('heading', { name: 'Film 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))
    await screen.findByRole('link', { name: 'Film 2' })
    await frame()
    await frame()

    expect(screen.getByRole('link', { name: 'Film 1' })).not.toHaveFocus()
  })

  it("doesn't put focus on the opened title when the place restored is far from it", async () => {
    server(3)
    renderGrid()
    fireEvent.click(await screen.findByRole('link', { name: 'Film 1' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    // Film 1 is now two screens below where the grid goes back to.
    const rect = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const top = this.closest('[data-id="f1"]') ? 3 * window.innerHeight : 0
      return { top, bottom: top + 300, left: 0, right: 0, width: 0, height: 300, x: 0, y: top, toJSON: () => ({}) } as DOMRect
    })
    try {
      fireEvent.click(screen.getByRole('button', { name: 'Back' }))
      await screen.findByRole('link', { name: 'Film 2' })
      await frame()
      await frame()

      expect(screen.getByRole('link', { name: 'Film 1' })).not.toHaveFocus()
    } finally {
      rect.mockRestore()
    }
  })

  it("doesn't bring back a title deleted from its own page", async () => {
    server(3)
    renderGrid()
    fireEvent.click(await screen.findByRole('link', { name: 'Film 1' }))
    await screen.findByRole('heading', { name: 'Film 1' })
    await deleteItem('f1')

    fireEvent.click(screen.getByRole('button', { name: 'Back' }))

    await vi.waitFor(() => expect(titles()).toEqual(['Film 0', 'Film 2']))
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
