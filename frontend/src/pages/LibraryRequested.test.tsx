import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { Library, MusicRequested, TitleRequested } from '../api/client'
import { requestedState } from '../lib/requestedState'
import { LibraryRequested } from './LibraryRequested'

const unrequested: unknown[] = []
let reachable = true
let musicAsked = 0
const albums: MusicRequested[] = [
  { musicBrainzId: 'rg-1', title: 'moisturizer', artist: 'Wet Leg', artistMusicBrainzId: 'a1', image: null, releaseDate: '2025-07-11', state: 'downloading', progress: 0.45, artistLibraryItemId: 'artist-1' },
  { musicBrainzId: 'rg-2', title: 'Next One', artist: 'Wet Leg', artistMusicBrainzId: 'a1', image: null, releaseDate: '2099-11-14', state: 'upcoming', progress: null, artistLibraryItemId: null },
]
const titles: TitleRequested[] = [
  { mediaType: 'tv', tmdbId: 1399, title: 'Thrones', year: 2011, poster: null, releaseDate: null, state: 'searching', progress: null, seasons: [2, 3], libraryItemId: 'show-1' },
  { mediaType: 'movie', tmdbId: 603, title: 'The Matrix', year: 1999, poster: null, releaseDate: null, state: 'downloading', progress: 0.5, seasons: null, libraryItemId: null },
]
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getTitlesRequested: async () => titles,
  getRequestStatus: async () => ({ configured: true, reachable: true }),
  unrequestTitle: async (...args: unknown[]) => {
    unrequested.push(args)
  },
  getMusicRequested: async () => {
    musicAsked++
    return albums
  },
  getMusicRequestStatusNow: async () => ({ configured: true, reachable }),
  unrequestAlbum: async (body: unknown) => {
    unrequested.push(body)
  },
}))

const library = { id: 'lib', name: 'Music', type: 'Music' } as Library

describe('LibraryRequested', () => {
  it('says where each album stands', () => {
    expect(requestedState(albums[0])).toBe('Downloading 45%')
    expect(requestedState({ ...albums[0], state: 'importing' })).toBe('Importing')
    expect(requestedState({ ...albums[0], state: 'searching' })).toBe('Searching')
    expect(requestedState(albums[1], new Date('2099-01-01'))).toMatch(/^Out 14 Nov$|^Out Nov 14$/)
  })

  it('lists what Lidarr awaits, and unrequests one after asking', async () => {
    render(
      <MemoryRouter>
        <LibraryRequested library={library} viewSwitch={null} />
      </MemoryRouter>,
    )

    expect((await screen.findAllByText('moisturizer')).length).toBeGreaterThan(0)
    expect(screen.getByText('Downloading 45%')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Wet Leg' }).getAttribute('href')).toBe('/item/artist-1')
    fireEvent.click(screen.getByRole('button', { name: 'Unrequest moisturizer' }))
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.queryAllByText('moisturizer')).toHaveLength(0))
    expect(unrequested).toEqual([{ musicBrainzId: 'rg-1' }])
    expect(screen.getAllByText('Next One').length).toBeGreaterThan(0)
  })

  it('says Lidarr isn’t answering, and greys Unrequest, rather than that nothing is coming', async () => {
    reachable = false
    render(
      <MemoryRouter>
        <LibraryRequested library={library} viewSwitch={null} />
      </MemoryRouter>,
    )

    expect(await screen.findByText('Lidarr isn’t answering.')).toBeTruthy()
    expect(screen.getAllByRole('button', { name: /^Unrequest / }).every((b) => (b as HTMLButtonElement).disabled)).toBe(true)
    reachable = true
  })

  it('lists the films and shows Seerr awaits, and takes back just the seasons asked for', async () => {
    unrequested.length = 0
    render(
      <MemoryRouter>
        <LibraryRequested library={{ id: 'tv', name: 'TV', type: 'TvShows' } as Library} viewSwitch={null} />
      </MemoryRouter>,
    )

    expect(await screen.findByText('Seasons 2 and 3')).toBeTruthy()
    expect(screen.getByText('Downloading 50%')).toBeTruthy()
    expect(screen.getAllByRole('link', { name: 'Thrones' })[0].getAttribute('href')).toBe('/item/show-1')
    expect(screen.getAllByRole('link', { name: 'The Matrix' })[0].getAttribute('href')).toBe('/discover/movie/603')
    fireEvent.click(screen.getAllByRole('button', { name: /^Unrequest / })[0])
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(unrequested).toEqual([['tv', 1399, [2, 3]]]))
  })

  it('asks again every 30 seconds while something is downloading', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      render(
        <MemoryRouter>
          <LibraryRequested library={library} viewSwitch={null} />
        </MemoryRouter>,
      )
      expect(await screen.findByText(/^Downloading/)).toBeTruthy()
      const before = musicAsked
      await vi.advanceTimersByTimeAsync(30_000)
      await waitFor(() => expect(musicAsked).toBe(before + 1))
    } finally {
      vi.useRealTimers()
    }
  })
})
