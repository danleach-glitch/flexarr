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
})
