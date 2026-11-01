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

  it('shows books and podcast episodes in Continue watching with their covers, and resumes them in the audio player', () => {
    const userData = { positionSeconds: 1800, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null }
    const book = summary({
      id: 'b1',
      kind: 'Audiobook',
      title: 'Project Hail Mary',
      durationSeconds: 7200,
      artwork: { poster: '/api/v1/images/items/b1/poster', backdrop: null, logo: null, thumb: null },
      book: { authors: 'Andy Weir', authorCount: 1, narrator: null, series: null, sequence: null, durationSeconds: 7200, finishedAt: null },
      userData,
    })
    const episode = summary({
      id: 'p1',
      kind: 'PodcastEpisode',
      title: 'The long one',
      parentId: 'pod',
      seriesTitle: 'Hardcore History',
      durationSeconds: 3600,
      artwork: { poster: '/api/v1/images/items/pod/poster', backdrop: null, logo: null, thumb: null },
      userData: { ...userData, positionSeconds: 900 },
    })
    const items = [book, episode] as unknown as ItemSummary[]
    const player = { play: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
    const { container } = render(
      <MemoryRouter>
        <AudioPlayerContext value={player}>
          <HomeRows rows={[{ id: 'continue', title: 'Continue watching or listening', style: 'wide', items, link: null }]} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { name: 'Continue watching or listening' })).toBeInTheDocument()
    // Title, then author or podcast; the text links to the book's page, the episode's and the podcast's.
    expect(screen.getByRole('link', { name: 'Project Hail Mary' })).toHaveAttribute('href', '/item/b1')
    expect(screen.getByText('Andy Weir')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'The long one' })).toHaveAttribute('href', '/podcasts/episode/p1')
    expect(screen.getByRole('link', { name: 'Hardcore History' })).toHaveAttribute('href', '/podcasts/pod')
    expect(screen.getAllByLabelText('25% listened')).toHaveLength(2)
    // The square cover over a blurred copy of itself.
    const cards = container.querySelectorAll('.listen-card')
    expect(cards).toHaveLength(2)
    expect(cards[0].querySelector('.listen-backdrop')).toHaveAttribute('src', '/api/v1/images/items/b1/poster?w=92')
    expect(cards[0].querySelector('.listen-cover .artwork-square')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('link', { name: 'Resume Project Hail Mary' }))
    expect(player.play).toHaveBeenLastCalledWith([book])
    fireEvent.click(screen.getByRole('link', { name: 'Resume The long one' }))
    expect(player.play).toHaveBeenLastCalledWith([episode])
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
