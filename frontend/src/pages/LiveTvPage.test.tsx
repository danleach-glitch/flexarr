import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import * as livetv from '../lib/livetv'
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
