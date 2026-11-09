import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import * as livetv from '../lib/livetv'
import { SessionContext } from '../hooks/sessionContext'
import type { Me } from '../api/client'
import LiveTvPage from './LiveTvPage'

vi.mock('../lib/livetv', async (original) => {
  const actual = await original<typeof import('../lib/livetv')>()
  return { ...actual, programmeTime: vi.fn(actual.programmeTime) }
})

afterEach(() => vi.unstubAllGlobals())

const hour = 60 * 60_000
const start = Date.now() - (Date.now() % (30 * 60_000))
const programme = (id: number, at: number, title: string) => ({
  id,
  start: new Date(start + at * hour).toISOString(),
  end: new Date(start + (at + 1) * hour).toISOString(),
  title,
  subtitle: null,
  description: null,
  category: null,
  season: null,
  episode: null,
  isNew: false,
  recording: null,
  series: false,
})
const channels = Array.from({ length: 20 }, (_, c) => ({
  id: `c${c}`,
  number: c + 1,
  name: `Channel ${c + 1}`,
  group: null,
  logoUrl: null,
  isFavourite: false,
  programmes: Array.from({ length: 6 }, (_, p) => programme(c * 100 + p, p, `Show ${c + 1}.${p + 1}`)),
}))

describe('refreshing the Live TV guide', () => {
  const openPage = (isAdmin: boolean) =>
    render(
      <SessionContext.Provider value={{ me: { profileId: 'p', method: 'api-key', isAdmin } as Me, profiles: undefined }}>
        <MemoryRouter>
          <LiveTvPage />
        </MemoryRouter>
      </SessionContext.Provider>,
    )

  it('finishes a refresh that fails just as the last did, and says when one worked', async () => {
    let finished = 4
    let error: string | null = 'Not there'
    mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing: false, refreshedAt: null, error, rewindMinutes: 0, channelsWithoutListings: 0, refreshesFinished: finished }),
      '/livetv/channels': () => [],
      '/livetv/refresh': () => {
        finished++
        return {}
      },
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    await screen.findByText('Not there')
    fireEvent.click(button)
    // Same error, nothing else differs: the count is what says it finished.
    await waitFor(() => expect(button.getAttribute('aria-busy')).not.toBe('true'), { timeout: 5000 })
    expect(screen.queryByText('Guide refreshed')).toBeNull()

    error = null
    fireEvent.click(button)
    await screen.findByText('Guide refreshed', undefined, { timeout: 5000 })
  }, 15000)

  it('tells the server, says how many channels have no listings, and fetches the channels again when it’s done', async () => {
    let refreshing = false
    let refreshedAt = '2026-11-08T06:00:00Z'
    let channelFetches = 0
    const fetchMock = mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing, refreshedAt, error: null, rewindMinutes: 0, channelsWithoutListings: refreshedAt.startsWith('2026-11-08T06') ? 3 : 1 }),
      '/livetv/channels': () => {
        channelFetches++
        return []
      },
      '/livetv/refresh': () => {
        refreshing = true
        setTimeout(() => {
          refreshing = false
          refreshedAt = '2026-11-08T12:00:00Z'
        }, 100)
        return undefined
      },
    })
    openPage(true)

    expect(await screen.findByText('3 channels have no listings')).toBeInTheDocument()
    await waitFor(() => expect(channelFetches).toBeGreaterThan(0))
    const before = channelFetches
    fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))
    await waitFor(() => expect(screen.getByText('1 channel has no listings')).toBeInTheDocument(), { timeout: 8000 })
    await waitFor(() => expect(channelFetches).toBeGreaterThan(before))
    expect(fetchMock.mock.calls.some(([input, init]) => String(input).includes('/livetv/refresh') && (init as RequestInit)?.method === 'POST')).toBe(true)
  }, 15000)

  it('says Refreshing… while it runs, keeps keyboard focus, and takes no second press', async () => {
    let refreshing = false
    let finished = 0
    const fetchMock = mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing, refreshedAt: null, error: null, rewindMinutes: 0, refreshesFinished: finished }),
      '/livetv/channels': [],
      '/livetv/refresh': () => {
        refreshing = true
        return {}
      },
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    button.focus()
    fireEvent.click(button)

    await waitFor(() => expect(button).toHaveAccessibleName('Refreshing…'))
    expect(button).toHaveAttribute('data-tip', 'Refreshing…')
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(button).toBeEnabled()
    expect(button).toHaveFocus()
    expect(screen.getByRole('status')).toHaveTextContent('Refreshing…')

    fireEvent.click(button)
    const posts = () => fetchMock.mock.calls.filter(([input, init]) => String(input).includes('/livetv/refresh') && (init as RequestInit)?.method === 'POST')
    expect(posts()).toHaveLength(1)

    refreshing = false
    finished = 1
    await waitFor(() => expect(button).toHaveAccessibleName('Refresh guide'), { timeout: 5000 })
    expect(button).not.toHaveAttribute('aria-disabled')
  }, 15000)

  it('isn’t offered to other profiles', async () => {
    mockApi({
      '/livetv/status': { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0, channelsWithoutListings: 3 },
      '/livetv/channels': [],
    })
    openPage(false)

    await screen.findByText('On now')
    expect(screen.queryByRole('button', { name: 'Refresh guide' })).not.toBeInTheDocument()
    expect(screen.queryByText(/have no listings/)).not.toBeInTheDocument()
  })
})

describe('the Live TV guide', () => {
  it('opens a programme’s details by redrawing its own channel’s line, not every channel’s', async () => {
    mockApi({
      '/livetv/status': { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0 },
      // No favourites, as a server answers: the rows come with the last guide request, so none lands mid-test and redraws them.
      '/livetv/guide': (_: unknown, url: URL) => ({
        from: new Date(start).toISOString(),
        until: new Date(start + 6 * hour).toISOString(),
        channels: url.searchParams.get('favourites') === 'true' ? [] : channels,
      }),
      '/livetv/channels': [],
    })
    render(
      <MemoryRouter initialEntries={['/livetv?view=guide']}>
        <LiveTvPage />
      </MemoryRouter>,
    )
    const show = await screen.findByRole('button', { name: /Show 7\.3/ })
    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))

    const times = vi.mocked(livetv.programmeTime)
    times.mockClear()
    fireEvent.click(show)
    expect(screen.getByRole('heading', { name: 'Show 7.3' })).toBeInTheDocument()
    expect(show).toHaveClass('selected')
    // The chosen line's six programmes, and the details' own time: not the other 114.
    expect(times.mock.calls.length).toBeLessThanOrEqual(6 + 2)

    times.mockClear()
    const other = screen.getByRole('button', { name: /Show 2\.1/ })
    fireEvent.click(other)
    expect(other).toHaveClass('selected')
    expect(show).not.toHaveClass('selected')
    expect(times.mock.calls.length).toBeLessThanOrEqual(12 + 2)
  })

  const status = { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0 }
  const answer = (list: typeof channels) => ({ from: new Date(start).toISOString(), until: new Date(start + 6 * hour).toISOString(), channels: list })
  const starred = channels.map((c, i) => ({ ...c, isFavourite: i < 2 }))
  const open = () =>
    render(
      <MemoryRouter initialEntries={['/livetv?view=guide']}>
        <LiveTvPage />
      </MemoryRouter>,
    )
  const guideAsks = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([input]) => new URL(String(input), 'http://localhost')).filter((u) => u.pathname === '/api/v1/livetv/guide')

  it('asks for the favourite channels alone, without descriptions, when they’re what’s shown', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': (_: unknown, url: URL) => answer(url.searchParams.get('favourites') === 'true' ? starred.filter((c) => c.isFavourite) : starred),
    })
    open()

    expect(await screen.findByRole('button', { name: /Show 2\.1/ })).toBeInTheDocument()
    expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(2)
    const asks = guideAsks(fetchMock)
    expect(asks).toHaveLength(1)
    expect(asks[0].searchParams.get('favourites')).toBe('true')
    expect(asks[0].searchParams.get('descriptions')).toBe('false')

    fireEvent.click(screen.getByRole('button', { name: 'All channels' }))
    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))
    expect(guideAsks(fetchMock).at(-1)?.searchParams.get('favourites')).toBeNull()
  })

  it('with no favourites, shows every channel', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': (_: unknown, url: URL) => answer(url.searchParams.get('favourites') === 'true' ? [] : channels),
    })
    open()

    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))
    expect(screen.getByRole('button', { name: 'All channels' })).toHaveClass('active')
    expect(screen.queryByText(/No favourites yet/)).not.toBeInTheDocument()
    expect(guideAsks(fetchMock)).toHaveLength(2)
  })

  it('on a server that sends every channel whatever it’s asked, still shows the favourites alone', async () => {
    mockApi({ '/livetv/status': status, '/livetv/channels': [], '/livetv/guide': answer(starred) })
    open()

    expect(await screen.findByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
    expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(2)
    expect(screen.getByRole('button', { name: /Favourites/ })).toHaveClass('active')
  })

  it('offers Try again when the guide can’t load, and shows it once it can', async () => {
    let down = true
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
    })
    open()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Couldn’t load the guide')
    down = false
    fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('clears a failed load when a later refresh brings the guide', async () => {
    let finished = 1
    let down = true
    mockApi({
      '/livetv/status': () => ({ ...status, refreshesFinished: finished }),
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
      '/livetv/refresh': () => {
        down = false
        finished++
        return {}
      },
    })
    render(
      <SessionContext.Provider value={{ me: { profileId: 'p', method: 'api-key', isAdmin: true } as Me, profiles: undefined }}>
        <MemoryRouter initialEntries={['/livetv?view=guide']}>
          <LiveTvPage />
        </MemoryRouter>
      </SessionContext.Provider>,
    )
    await screen.findByText(/Couldn’t load the guide/)

    fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))

    expect(await screen.findByRole('button', { name: /Show 1\.1/ }, { timeout: 5000 })).toBeInTheDocument()
    expect(screen.queryByText(/Couldn’t load the guide/)).toBeNull()
  }, 15000)

  it('keeps the guide on screen when a later load fails', async () => {
    let down = false
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
    })
    open()
    await screen.findByRole('button', { name: /Show 1\.1/ })

    down = true
    fireEvent.click(screen.getByRole('button', { name: 'Show later' }))

    expect(await screen.findByText(/Couldn’t load the guide/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
  })

  it('fetches a programme’s description when its details open', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': answer(channels),
      '/livetv/programmes/602': { ...programme(602, 2, 'Show 7.3'), description: 'A long story.' },
    })
    open()

    fireEvent.click(await screen.findByRole('button', { name: /Show 7\.3/ }))

    expect(await screen.findByText('A long story.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Show 2\.1/ }))
    fireEvent.click(screen.getByRole('button', { name: /Show 7\.3/ }))
    expect(await screen.findByText('A long story.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.filter(([input]) => String(input).includes('/livetv/programmes/602'))).toHaveLength(1)
  })
})
