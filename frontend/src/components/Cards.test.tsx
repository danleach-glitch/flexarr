import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { summary } from '../test/mockApi'
import { PosterCard } from './Cards'

const tracks = [summary({ id: 't1', kind: 'Track', title: 'Telegraph Road' }), summary({ id: 't2', kind: 'Track', title: 'Private Investigations' })] as unknown as ItemSummary[]

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getTracks: vi.fn(async () => tracks),
}))

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

  it('has no play button on a film', () => {
    const film = summary({ id: 'film', kind: 'Movie', title: 'Sintel' }) as unknown as ItemSummary
    const player = { play: vi.fn() } as unknown as AudioPlayer
    render(
      <MemoryRouter>
        <AudioPlayerContext value={player}>
          <PosterCard item={film} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )
    expect(screen.queryByRole('button', { name: /^Play / })).toBeNull()
  })
})
