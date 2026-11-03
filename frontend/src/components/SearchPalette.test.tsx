import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { SearchPalette } from './SearchPalette'

afterEach(() => vi.unstubAllGlobals())

const pulp = summary({ id: 'pulp', kind: 'Artist', title: 'Pulp', childCount: 3 })
const differentClass = summary({ id: 'dc', kind: 'Album', title: 'Different Class', year: 1995, parentId: 'pulp', seriesTitle: 'Pulp', seriesId: 'pulp' })
const commonPeople = summary({ id: 'cp', kind: 'Track', title: 'Common People', parentId: 'dc', seriesTitle: 'Pulp', seriesId: 'pulp', trackNumber: 1 })

function Where() {
  return <p>At {useLocation().pathname}</p>
}

function renderPalette(onClose = vi.fn()) {
  const player = { play: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
  render(
    <MemoryRouter>
      <AudioPlayerContext value={player}>
        <SearchPalette onClose={onClose} />
        <Routes>
          <Route path="*" element={<Where />} />
        </Routes>
      </AudioPlayerContext>
    </MemoryRouter>,
  )
  return { player, onClose }
}

describe('SearchPalette', () => {
  it('groups artists, albums and tracks, naming the artist and album beneath each', async () => {
    mockApi({ '/search': { titles: [pulp], episodes: [], people: [], genres: [], albums: [differentClass], tracks: [{ item: commonPeople, album: 'Different Class' }] } })
    renderPalette()
    fireEvent.change(screen.getByLabelText('Search your library'), { target: { value: 'pulp' } })

    const titles = await screen.findByRole('group', { name: 'Titles' })
    expect(within(titles).getByText('3 albums')).toBeInTheDocument()
    expect(within(screen.getByRole('group', { name: 'Albums' })).getByText('Pulp, 1995')).toBeInTheDocument()
    expect(within(screen.getByRole('group', { name: 'Tracks' })).getByText('Pulp, Different Class')).toBeInTheDocument()
  })

  it('lists films, shows, artists and albums that aren’t in the library last, opening their own pages', async () => {
    const empty = { titles: [], episodes: [], people: [], genres: [], albums: [], tracks: [] }
    mockApi({
      '/search': { ...empty, titles: [pulp] },
      '/requests/status': { configured: true, reachable: true },
      '/requests/music/status': { configured: true, reachable: true },
      '/requests/search': [
        { tmdbId: 7, mediaType: 'movie', title: 'Pulp Fiction', year: 1994, overview: null, poster: null, backdrop: null, rating: 8, availability: 'None', libraryItemId: null },
        { tmdbId: 8, mediaType: 'tv', title: 'Pulp Show', year: 2001, overview: null, poster: null, backdrop: null, rating: 6, availability: 'Available', libraryItemId: null },
      ],
      '/requests/music/search': {
        artists: [],
        albums: [{ kind: 'album', deezerId: 42, title: 'This Is Hardcore', artist: 'Pulp', artistDeezerId: 1, year: 1998, image: null, availability: 'Requested', libraryItemId: null }],
      },
    })
    renderPalette()
    fireEvent.change(screen.getByLabelText('Search your library'), { target: { value: 'pulp' } })

    const outside = await screen.findByRole('group', { name: 'Not in your library' }, { timeout: 2000 })
    expect(within(outside).getByText('Movie, 1994')).toBeInTheDocument()
    expect(within(outside).getByText('Pulp, 1998, Requested')).toBeInTheDocument()
    expect(within(outside).queryByText('Pulp Show')).toBeNull() // Seerr says it's in hand
    expect(screen.getAllByRole('group').map((g) => g.getAttribute('aria-label'))).toEqual(['Titles', 'Not in your library'])

    fireEvent.click(within(outside).getByRole('option', { name: /Pulp Fiction/ }))
    expect(screen.getByText('At /discover/movie/7')).toBeInTheDocument()
  })

  it('opens a track’s album, and plays the track from its button without leaving the page', async () => {
    mockApi({ '/search': { titles: [], episodes: [], people: [], genres: [], albums: [], tracks: [{ item: commonPeople, album: 'Different Class' }] } })
    const { player, onClose } = renderPalette()
    fireEvent.change(screen.getByLabelText('Search your library'), { target: { value: 'common' } })

    fireEvent.click(await screen.findByRole('button', { name: 'Play Common People' }))
    expect(player.play).toHaveBeenCalledWith([commonPeople], 0)
    expect(onClose).toHaveBeenCalledTimes(1)
    expect(screen.getByText('At /')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('option', { name: /Common People/ }))
    expect(screen.getByText('At /item/dc')).toBeInTheDocument()
  })

  it('offers recent searches before anything’s typed, and searches again from one', async () => {
    const searched = vi.fn()
    mockApi({
      'GET /search/recent': { queries: ['pulp'], items: [differentClass] },
      '/search': (_: RequestInit | undefined, url: URL) => {
        searched(url.searchParams.get('q'))
        return { titles: [pulp], episodes: [], people: [], genres: [], albums: [], tracks: [] }
      },
    })
    renderPalette()

    const recent = await screen.findByRole('group', { name: 'Recent searches' })
    expect(within(screen.getByRole('group', { name: 'Recently opened' })).getByText('Different Class')).toBeInTheDocument()
    fireEvent.click(within(recent).getByRole('option', { name: /pulp/ }))

    expect(screen.getByLabelText('Search your library')).toHaveValue('pulp')
    expect(await screen.findByRole('group', { name: 'Titles' })).toBeInTheDocument()
    expect(searched).toHaveBeenCalledWith('pulp')
  })

  it('remembers a search once something’s picked from it, with what was opened', async () => {
    const recorded = vi.fn()
    mockApi({
      'GET /search/recent': { queries: [], items: [] },
      'POST /search/recent': (init?: RequestInit) => void recorded(JSON.parse(String(init?.body))),
      '/search': { titles: [pulp], episodes: [], people: [], genres: [], albums: [], tracks: [] },
    })
    renderPalette()
    fireEvent.change(screen.getByLabelText('Search your library'), { target: { value: 'pul' } })

    fireEvent.click(await screen.findByRole('option', { name: /Pulp/ }))
    expect(recorded).toHaveBeenCalledWith({ query: 'pul', itemId: 'pulp' })
    expect(recorded).toHaveBeenCalledTimes(1)
  })

  it('forgets one recent search, or clears them all', async () => {
    const forgotten = vi.fn()
    mockApi({
      'GET /search/recent': { queries: ['pulp', 'blur'], items: [differentClass] },
      'DELETE /search/recent': (_: RequestInit | undefined, url: URL) => void forgotten(url.search),
    })
    renderPalette()

    fireEvent.click(await screen.findByRole('button', { name: 'Remove blur from recent searches' }))
    expect(screen.queryByText('blur')).not.toBeInTheDocument()
    expect(forgotten).toHaveBeenCalledWith('?query=blur')

    fireEvent.click(screen.getByRole('button', { name: 'Clear' }))
    expect(screen.queryByRole('group', { name: 'Recent searches' })).not.toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Recently opened' })).not.toBeInTheDocument()
    expect(forgotten).toHaveBeenLastCalledWith('')
  })

  it('leaves Enter on a tabbed-to button to that button, keeps Tab inside, and closes on Escape', async () => {
    mockApi({ '/search': { titles: [], episodes: [], people: [], genres: [], albums: [], tracks: [{ item: commonPeople, album: 'Different Class' }] } })
    const { onClose } = renderPalette()
    const field = screen.getByLabelText('Search your library')
    fireEvent.change(field, { target: { value: 'common' } })
    const play = await screen.findByRole('button', { name: 'Play Common People' })

    // Enter on Play isn't the field's: the highlighted result doesn't open.
    play.focus()
    fireEvent.keyDown(play, { key: 'Enter' })
    expect(screen.getByText('At /')).toBeInTheDocument()

    // Tab from the last control comes back to the first, inside the palette.
    fireEvent.keyDown(play, { key: 'Tab' })
    expect(field).toHaveFocus()

    fireEvent.keyDown(field, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)
    fireEvent.keyDown(field, { key: 'Enter' })
    expect(screen.getByText('At /item/dc')).toBeInTheDocument()
  })
})
