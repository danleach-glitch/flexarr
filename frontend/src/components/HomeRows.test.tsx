import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { summary } from '../test/mockApi'
import { HomeRows } from './HomeRows'

const track = (id: string, title: string) => summary({ id, kind: 'Track', title, parentId: 'album', seriesTitle: 'Blur', seriesId: 'blur' })
const tracks = [track('t1', 'Girls & Boys'), track('t2', 'Parklife'), track('t3', 'Tender')] as unknown as ItemSummary[]

describe('HomeRows', () => {
  it('plays a Most played row from the track pressed, and links its text to the album and artist', () => {
    const player = { play: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
    render(
      <MemoryRouter>
        <AudioPlayerContext value={player}>
          <HomeRows rows={[{ id: 'music-top', title: 'Most played', style: 'tracks', items: tracks, link: null }]} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('link', { name: 'Play Parklife' }))
    expect(player.play).toHaveBeenCalledWith(tracks, 1)
    expect(screen.getByRole('link', { name: 'Parklife' })).toHaveAttribute('href', '/item/album')
    expect(screen.getAllByRole('link', { name: 'Blur' })[0]).toHaveAttribute('href', '/item/blur')
  })

  it('shows a Made for you row as mix cards that open the mix', () => {
    const mixes = [
      { id: 'a1b2c3d4', name: 'Rock Mix', description: 'Dire Straits, Pink Floyd & more', covers: ['/api/v1/images/items/x/poster'], trackCount: 40 },
      { id: 'e5f6', name: 'Miles Davis Mix', description: 'Made from the artists you play', covers: [], trackCount: 12 },
    ]
    render(
      <MemoryRouter>
        <HomeRows rows={[{ id: 'music-mixes', title: 'Made for you', style: 'mixes', items: [], link: null, mixes }]} />
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { name: 'Made for you' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Rock Mix/ })).toHaveAttribute('href', '/mix/a1b2c3d4')
    expect(screen.getByRole('link', { name: /Miles Davis Mix/ })).toHaveTextContent('Made from the artists you play')
  })
})
