import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { WatchlistButton } from '../components/WatchlistButton'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { WatchlistPage } from './WatchlistPage'

afterEach(() => vi.unstubAllGlobals())

const entry = (overrides: Record<string, unknown>) => ({
  id: 'w1',
  mediaType: 'movie',
  tmdbId: 1,
  title: 'Title',
  year: 2026,
  poster: null,
  backdrop: null,
  addedAt: '2026-10-30T10:00:00Z',
  status: 'NotAvailable',
  releaseDate: null,
  libraryItemId: null,
  item: null,
  source: 'flexarr',
  ...overrides,
})

describe('Watchlist', () => {
  it('shows each title where it has got to, and filters to what is ready', async () => {
    mockApi({
      '/watchlist': [
        entry({ id: 'a', title: 'Dune', status: 'Ready', libraryItemId: 'i1', item: summary({ id: 'i1', title: 'Dune' }) }),
        entry({ id: 'b', title: 'Being Fetched', status: 'OnItsWay', tmdbId: 2 }),
        entry({ id: 'c', title: 'Not Out', status: 'Coming', tmdbId: 3, releaseDate: '2027-03-05' }),
        entry({ id: 'd', title: 'Nobody Asked', tmdbId: 4, streaming: ['Netflix'] }),
      ],
    })
    render(
      <MemoryRouter>
        <AudioPlayerContext value={{ play: vi.fn() } as unknown as AudioPlayer}>
          <WatchlistPage />
        </AudioPlayerContext>
      </MemoryRouter>,
    )

    const states = () => [...document.querySelectorAll('.external-state')].map((e) => e.textContent)
    await screen.findAllByText('Being Fetched')
    // The date is in the browser's own format.
    expect(states()).toEqual(['On its way', expect.stringMatching(/^Out .*2027$/)])
    expect(screen.getByRole('button', { name: 'Request' })).toBeInTheDocument()
    expect(screen.getByText('On Netflix')).toBeInTheDocument()
    // Ready first, then on its way, coming, and the rest.
    const order = [...document.querySelectorAll('.watchlist-card')].map((c) => c.querySelector('.card-title, a[aria-label]')?.textContent || c.textContent)
    expect(order[0]).toContain('Dune')
    expect(order[1]).toContain('Being Fetched')
    expect(order[3]).toContain('Nobody Asked')

    fireEvent.click(screen.getByRole('button', { name: 'Ready to watch' }))
    expect(screen.queryByText('Being Fetched')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Dune' })).toHaveAttribute('href', '/item/i1')
  })

  it('says how to add titles when the list is empty', async () => {
    mockApi({ '/watchlist': [] })
    render(
      <MemoryRouter>
        <WatchlistPage />
      </MemoryRouter>,
    )
    expect(await screen.findByText(/Nothing here yet/)).toBeInTheDocument()
  })

  it('adds a title from its button and takes it off again', async () => {
    const calls: string[] = []
    mockApi({
      'POST /watchlist': (init?: RequestInit) => {
        calls.push(`add ${String(init?.body)}`)
        return { entry: entry({}), requested: true }
      },
      'DELETE /watchlist': (_: RequestInit | undefined, url: URL) => {
        calls.push(`remove ${url.search}`)
        return undefined
      },
    })
    const requested = vi.fn()
    render(<WatchlistButton titleKey={{ mediaType: 'movie', tmdbId: 7 }} title="Heat" on={false} onRequested={requested} />)

    fireEvent.click(screen.getByRole('button', { name: /Watchlist/ }))
    expect(await screen.findByRole('button', { name: /On watchlist/ })).toHaveAttribute('aria-pressed', 'true')
    expect(requested).toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: /On watchlist/ }))
    expect(await screen.findByRole('button', { name: /^\s*Watchlist/ })).toHaveAttribute('aria-pressed', 'false')
    expect(calls).toEqual(['add {"mediaType":"movie","tmdbId":7}', 'remove ?mediaType=movie&tmdbId=7'])
  })
})
