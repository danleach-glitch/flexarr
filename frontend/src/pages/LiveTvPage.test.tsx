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
      '/livetv/guide': { from: new Date(start).toISOString(), until: new Date(start + 6 * hour).toISOString(), channels },
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
})
