import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useParams } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { summary } from '../test/mockApi'
import { PosterCard } from './Cards'

const tracks = [summary({ id: 't1', kind: 'Track', title: 'Telegraph Road' }), summary({ id: 't2', kind: 'Track', title: 'Private Investigations' })] as unknown as ItemSummary[]

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getTracks: vi.fn(async () => tracks),
  getNextUp: vi.fn(async () => ({ id: 'next' })),
}))

function PlayingId() {
  return <span>Playing {useParams().id}</span>
}

describe('PosterCard', () => {
  it('plays an album from the play button on its artwork, while the artwork itself still opens the album', async () => {
    const album = summary({ id: 'album', kind: 'Album', title: 'Love Over Gold', seriesTitle: 'Dire Straits', year: 1982 }) as unknown as ItemSummary
    const player = { play: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
    render(
      <MemoryRouter>
        <AudioPlayerContext value={player}>
          <PosterCard item={album} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Play Love Over Gold' }))
    await waitFor(() => expect(player.play).toHaveBeenCalledWith(tracks, 0, { openNowPlaying: true }))
    expect(screen.getByRole('link', { name: 'Love Over Gold' })).toHaveAttribute('href', '/item/album')
  })

  it.each([
    [{ id: 'film', kind: 'Movie', title: 'Sintel' }, 'Play Sintel', 'film'],
    [{ id: 'show', kind: 'Series', title: 'Severance' }, 'Play the next episode of Severance', 'next'],
    [{ id: 'season', kind: 'Season', title: 'Season 2', seriesId: 'show', seriesTitle: 'Severance' }, 'Play the next episode of Severance', 'next'],
  ])('plays a %o from the play button on its artwork', async (fields, label, playing) => {
    const item = summary(fields as Parameters<typeof summary>[0]) as unknown as ItemSummary
    render(
      <MemoryRouter>
        <AudioPlayerContext value={{ play: vi.fn() } as unknown as AudioPlayer}>
          <Routes>
            <Route path="/" element={<PosterCard item={item} />} />
            <Route path="/play/:id" element={<PlayingId />} />
          </Routes>
        </AudioPlayerContext>
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('button', { name: label }))
    expect(await screen.findByText(`Playing ${playing}`)).toBeInTheDocument()
  })
})
