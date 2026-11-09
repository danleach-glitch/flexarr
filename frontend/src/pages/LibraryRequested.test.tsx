import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Library, MusicRequested, TitleRequested } from '../api/client'
import { requestedState } from '../lib/requestedState'
import { LibraryRequested } from './LibraryRequested'

const unrequested: unknown[] = []
const retried: unknown[] = []
let retryFails = false
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
  retryAlbum: async (body: unknown) => {
    if (retryFails) throw new Error('Lidarr said no')
    retried.push(body)
  },
  unrequestAlbum: async (body: unknown) => {
    unrequested.push(body)
  },
}))

const library = { id: 'lib', name: 'Music', type: 'Music' } as Library

describe('LibraryRequested', () => {
  it('says where each album stands', () => {
    expect(requestedState(albums[0])).toBe('Downloading, 45%')
    expect(requestedState({ ...albums[0], state: 'importing' })).toBe('Importing')
    expect(requestedState({ ...albums[0], state: 'couldNotImport' })).toBe('Couldn’t import')
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
    expect(screen.getByText('Downloading, 45%')).toBeTruthy()
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
    expect(screen.getByText('Downloading, 50%')).toBeTruthy()
    expect(screen.getAllByRole('link', { name: 'Thrones' })[0].getAttribute('href')).toBe('/item/show-1')
    expect(screen.getAllByRole('link', { name: 'The Matrix' })[0].getAttribute('href')).toBe('/discover/movie/603')
    fireEvent.click(screen.getAllByRole('button', { name: /^Unrequest / })[0])
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(unrequested).toEqual([['tv', 1399, [2, 3]]]))
  })

  it('shows the title in place of a cover that won’t load', async () => {
    albums.push({ ...albums[1], musicBrainzId: 'rg-3', title: 'No Cover', image: '/covers/gone.jpg' })
    try {
      const { container } = render(
        <MemoryRouter>
          <LibraryRequested library={library} viewSwitch={null} />
        </MemoryRouter>,
      )
      await screen.findByText('No Cover')
      fireEvent.error(container.querySelector('img')!)

      expect(container.querySelector('img')).toBeNull()
      expect(container.querySelectorAll('.artwork-fallback')).toHaveLength(3)
      expect([...container.querySelectorAll('.artwork-fallback')].some((f) => f.textContent === 'No Cover')).toBe(true)
    } finally {
      albums.pop()
    }
  })

  describe('an album that couldn’t be imported', () => {
    const stuck: MusicRequested = { ...albums[1], musicBrainzId: 'rg-stuck', title: 'City Lounge', state: 'couldNotImport', canRetry: true }
    const openWithStuck = async () => {
      albums.unshift(stuck)
      render(
        <MemoryRouter>
          <LibraryRequested library={library} viewSwitch={null} />
        </MemoryRouter>,
      )
      return screen.findByRole('button', { name: 'Try again: City Lounge' })
    }
    afterEach(() => {
      if (albums.includes(stuck)) albums.splice(albums.indexOf(stuck), 1)
      retried.length = 0
      retryFails = false
    })

    it('says why, in the guide, and starts over on Try again', async () => {
      const button = await openWithStuck()
      expect(screen.getByRole('link', { name: 'Why couldn’t City Lounge be imported?' })).toHaveAttribute('href', 'https://flexarr.io/guide/requests#couldn-t-import')
      expect(screen.getByRole('button', { name: 'Unrequest City Lounge' })).toBeInTheDocument()

      fireEvent.click(button)

      await waitFor(() => expect(retried).toEqual([{ musicBrainzId: 'rg-stuck' }]))
      // Lidarr's queue may still say it for a moment: the card has moved on regardless.
      expect(await screen.findByText('Searching')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Try again: City Lounge' })).toBeNull()
      expect(screen.queryByRole('link', { name: /Why couldn’t/ })).toBeNull()
    })

    it('says why when it can’t try again, and still offers it', async () => {
      retryFails = true
      const button = await openWithStuck()

      fireEvent.click(button)

      expect(await screen.findByRole('alert')).toHaveTextContent('Lidarr said no')
      expect(screen.getByRole('button', { name: 'Try again: City Lounge' })).toBeEnabled()
    })

    it('moves the keyboard to the next card’s Unrequest, not its Try again', async () => {
      await openWithStuck()
      albums.push({ ...stuck, musicBrainzId: 'rg-stuck-2', title: 'Second' })
      try {
        cleanup()
        render(
          <MemoryRouter>
            <LibraryRequested library={library} viewSwitch={null} />
          </MemoryRouter>,
        )
        fireEvent.click(await screen.findByRole('button', { name: 'Unrequest City Lounge' }))
        fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))
        await waitFor(() => expect(screen.getByRole('button', { name: 'Unrequest moisturizer' })).toHaveFocus())
      } finally {
        albums.pop()
      }
    })

    it('greys Try again while Lidarr isn’t answering', async () => {
      reachable = false
      try {
        expect(await openWithStuck()).toBeDisabled()
      } finally {
        reachable = true
      }
    })

    it('isn’t offered for anything else', async () => {
      render(
        <MemoryRouter>
          <LibraryRequested library={library} viewSwitch={null} />
        </MemoryRouter>,
      )
      await screen.findByText(/^Downloading/)
      expect(screen.queryByRole('button', { name: /^Try again/ })).toBeNull()
      expect(screen.queryByRole('link', { name: /Why/ })).toBeNull()
    })
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
