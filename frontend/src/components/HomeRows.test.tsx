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
})
