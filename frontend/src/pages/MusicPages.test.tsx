import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { AlbumType, ItemDetail, ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { detail, summary } from '../test/mockApi'
import { AlbumPage, ArtistPage, albumSections, albumTypeLabel } from './MusicPages'

const artist = summary({ id: 'artist', kind: 'Artist', title: 'Dire Straits' })
const album = (id: string, title: string, year: number, albumType: AlbumType | null) =>
  summary({ id, kind: 'Album', title, year, albumType, parentId: 'artist', seriesTitle: 'Dire Straits', seriesId: 'artist' })
const albums = (...raw: ReturnType<typeof summary>[]) => raw as unknown as ItemSummary[]

// Newest first, as the server sends an artist's albums.
const discography = [
  album('money', 'Money for Nothing', 1988, 'compilation'),
  album('alchemy', 'Alchemy', 1984, 'live'),
  album('ep', 'ExtendedancEPlay', 1983, 'ep'),
  album('pool', 'Twisting by the Pool', 1983, 'single'),
  album('gold', 'Love Over Gold', 1982, 'album'),
  album('movies', 'Making Movies', 1980, 'album'),
  album('communique', 'Communiqué', 1979, null), // not in Lidarr, so nobody has said what it is
]

function renderPage(page: React.ReactElement) {
  const player = { play: vi.fn(), playNext: vi.fn(), enqueue: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0 } as unknown as AudioPlayer
  render(
    <MemoryRouter>
      <AudioPlayerContext value={player}>{page}</AudioPlayerContext>
    </MemoryRouter>,
  )
}

const titlesIn = (region: HTMLElement) => Array.from(region.querySelectorAll('.card-title')).map((t) => t.textContent)
const facts = () => Array.from(document.querySelector('.music-header-text .facts')?.children ?? []).map((f) => f.textContent)

describe('albumSections', () => {
  it('shelves albums, then EPs and singles, then live albums and compilations, keeping the order within each', () => {
    expect(albumSections(albums(...discography)).map((s) => [s.title, s.albums.map((a) => a.title)])).toEqual([
      ['Albums', ['Love Over Gold', 'Making Movies', 'Communiqué']],
      ['EPs and singles', ['ExtendedancEPlay', 'Twisting by the Pool']],
      ['Live and compilations', ['Money for Nothing', 'Alchemy']],
    ])
  })

  it('leaves out sections with nothing in them, and files soundtracks and kinds it has never heard of under Albums', () => {
    const odd = albums(album('score', 'The Princess Bride', 1987, 'soundtrack'), album('odd', 'Something Odd', 1990, 'boxset' as AlbumType), album('bootleg', 'Bootleg', 1991, 'other'))
    expect(albumSections(odd).map((s) => [s.title, s.albums.map((a) => a.title)])).toEqual([
      ['Albums', ['The Princess Bride', 'Something Odd']],
      ['Other', ['Bootleg']],
    ])
  })
})

describe('ArtistPage', () => {
  it('shows each section under its heading, in order', () => {
    renderPage(<ArtistPage detail={detail(artist, { children: discography }) as unknown as ItemDetail} />)

    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual(['Albums', 'EPs and singles', 'Live and compilations'])
    expect(titlesIn(screen.getByRole('region', { name: 'Albums' }))).toEqual(['Love Over Gold', 'Making Movies', 'Communiqué'])
    expect(titlesIn(screen.getByRole('region', { name: 'EPs and singles' }))).toEqual(['ExtendedancEPlay', 'Twisting by the Pool'])
    expect(titlesIn(screen.getByRole('region', { name: 'Live and compilations' }))).toEqual(['Money for Nothing', 'Alchemy'])
    expect(screen.queryByRole('region', { name: 'Other' })).toBeNull()
    expect(screen.getByText('7 albums')).toBeInTheDocument()
  })
})

describe('AlbumPage', () => {
  const track = summary({ id: 't1', kind: 'Track', title: 'Twisting by the Pool', parentId: 'pool', durationSeconds: 200 })
  const page = (item: ReturnType<typeof summary>) => <AlbumPage detail={detail(item, { ancestors: [artist], children: [track] }) as unknown as ItemDetail} />

  it('says what kind of release it is, before the year', () => {
    renderPage(page(album('ep', 'ExtendedancEPlay', 1983, 'ep')))
    expect(facts()).toEqual(['Dire Straits', 'EP', '1983', '1 track', '3m'])
  })

  it.each([
    ['album' as AlbumType, 'Love Over Gold'],
    [null, 'Communiqué'],
  ])('says nothing of the kind for a plain album (%s)', (albumType, title) => {
    renderPage(page(album('plain', title, 1982, albumType)))
    expect(facts()).toEqual(['Dire Straits', '1982', '1 track', '3m'])
  })

  it('has a word for every kind worth one', () => {
    expect((['ep', 'single', 'live', 'compilation', 'soundtrack'] as AlbumType[]).map(albumTypeLabel)).toEqual(['EP', 'Single', 'Live', 'Compilation', 'Soundtrack'])
    expect(['album', 'other', null, undefined].map((t) => albumTypeLabel(t as AlbumType | null | undefined))).toEqual([null, null, null, null])
  })
})
