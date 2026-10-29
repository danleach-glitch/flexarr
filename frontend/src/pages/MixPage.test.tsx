import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { MixPage } from './MixPage'

afterEach(() => vi.unstubAllGlobals())

const track = (id: string, title: string, artist: string) =>
  summary({ id, kind: 'Track', title, parentId: `album-${artist}`, seriesTitle: artist, seriesId: artist, durationSeconds: 180 }) as unknown as ItemSummary
const tracks = [track('t1', 'Sultans of Swing', 'Dire Straits'), track('t2', 'Time', 'Pink Floyd'), track('t3', 'Creep', 'Radiohead')]
const detail = {
  summary: { id: 'a1b2c3d4', name: 'Rock Mix', description: 'Dire Straits, Pink Floyd & more', covers: ['/api/v1/images/items/x/poster'], trackCount: 3 },
  tracks,
}

function renderPage(id = 'a1b2c3d4') {
  const player = { play: vi.fn(), playNext: vi.fn(), enqueue: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
  render(
    <MemoryRouter initialEntries={[`/mix/${id}`]}>
      <AudioPlayerContext value={player}>
        <Routes>
          <Route path="mix/:id" element={<MixPage />} />
        </Routes>
      </AudioPlayerContext>
    </MemoryRouter>,
  )
  return player
}

describe('MixPage', () => {
  it('shows the mix and plays it from the start, shuffled, or from the track pressed', async () => {
    mockApi({ '/music/mixes/a1b2c3d4': detail })
    const player = renderPage()

    expect(await screen.findByRole('heading', { level: 1, name: 'Rock Mix' })).toBeInTheDocument()
    expect(screen.getByText('Made for you')).toBeInTheDocument()
    expect(screen.getByText('Dire Straits, Pink Floyd & more')).toBeInTheDocument()
    expect(screen.getByText('3 tracks')).toBeInTheDocument()
    expect(screen.getByText('9m')).toBeInTheDocument()
    const list = screen.getByRole('list', { name: 'Tracks in Rock Mix' })
    expect(within(list).getAllByRole('listitem').map((li) => li.querySelector('.track-title')?.textContent)).toEqual(['Sultans of Swing', 'Time', 'Creep'])
    // Nothing to edit: the server made it.
    expect(screen.queryByRole('button', { name: /Rename|Delete|Move/ })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Play' }))
    expect(player.play).toHaveBeenLastCalledWith(tracks, 0, { openNowPlaying: true })
    fireEvent.click(screen.getByRole('button', { name: /^3\s*Creep/ }))
    expect(player.play).toHaveBeenLastCalledWith(tracks, 2)

    fireEvent.click(screen.getByRole('button', { name: 'Shuffle' }))
    const [queue, index] = (player.play as ReturnType<typeof vi.fn>).mock.lastCall as [ItemSummary[], number]
    expect(index).toBe(0)
    expect([...queue].sort((a, b) => a.id.localeCompare(b.id))).toEqual(tracks)
  })

  it('offers the queue actions for the whole mix', async () => {
    mockApi({ '/music/mixes/a1b2c3d4': detail })
    const player = renderPage()
    await screen.findByRole('heading', { level: 1, name: 'Rock Mix' })

    fireEvent.click(screen.getByRole('button', { name: 'More for Rock Mix' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Add to queue' }))
    await vi.waitFor(() => expect(player.enqueue).toHaveBeenCalledWith(tracks))
  })

  it("says so when the mix was yesterday's", async () => {
    mockApi({})
    renderPage('gone')

    expect(await screen.findByText(/yesterday/)).toBeInTheDocument()
  })
})
