import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it } from 'vitest'
import { clearApiCache, type Library } from '../api/client'
import { AudioPlayerProvider } from '../hooks/audioPlayer'
import { LibrariesContext } from '../hooks/librariesContext'
import { mockApi, summary } from '../test/mockApi'
import { LibraryPage } from './LibraryPage'

const music: Library = { id: 'lib-music', name: 'Music', type: 'Music', folders: ['/media/music'], itemCount: 2, lastScannedAt: null, scan: null }

beforeEach(() => clearApiCache())

describe('A library with every Browse row switched off', () => {
  it('shows all its titles rather than saying there is nothing', async () => {
    mockApi({
      '/libraries/lib-music/browse': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
      '/libraries/lib-music/genres': [],
      '/libraries/lib-music/items': { total: 0, items: [] },
    })
    render(
      <AudioPlayerProvider>
        <LibrariesContext value={{ libraries: [music], reload: () => {} }}>
          <MemoryRouter initialEntries={['/library/lib-music']}>
            <Routes>
              <Route path="/library/:id" element={<LibraryPage />} />
            </Routes>
          </MemoryRouter>
        </LibrariesContext>
      </AudioPlayerProvider>,
    )

    expect(await screen.findByRole('link', { name: 'Artists', current: 'page' })).toBeInTheDocument()
    expect(screen.queryByText('Nothing here yet.')).not.toBeInTheDocument()
  })
})

function renderAt(path: string) {
  render(
    <AudioPlayerProvider>
      <LibrariesContext value={{ libraries: [music], reload: () => {} }}>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route path="/library/:id" element={<LibraryPage />} />
          </Routes>
        </MemoryRouter>
      </LibrariesContext>
    </AudioPlayerProvider>,
  )
}

describe('A music library', () => {
  const dummy = summary({ id: 'dummy', libraryId: 'lib-music', kind: 'Album', title: 'Dummy', year: 1994, childCount: 11, seriesTitle: 'Portishead', parentId: 'portishead' })
  const massive = summary({ id: 'massive', libraryId: 'lib-music', kind: 'Artist', title: 'Massive Attack', childCount: 5 })

  it('leads Browse with the album played last, carrying on, then artists with Radio', async () => {
    const sour = summary({ id: 'sour', kind: 'Track', title: 'Sour Times', parentId: 'dummy' })
    mockApi({
      '/libraries/lib-music/browse': { featured: [dummy, massive], overviews: {}, rows: [{ id: 'recent', title: 'New albums', style: 'poster', items: [dummy] }], upcoming: [], downloading: 0, resumes: { dummy: sour } },
    })
    renderAt('/library/lib-music')

    expect(await screen.findByRole('heading', { name: 'Dummy' })).toBeInTheDocument()
    expect(screen.getByText('Portishead')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Resume/ })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Albums' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Genres' })).toBeInTheDocument()
  })

  it('ends Browse with Your artists, and See all when there are more', async () => {
    mockApi({
      '/libraries/lib-music/browse': { featured: [], overviews: {}, rows: [{ id: 'recent', title: 'New albums', style: 'poster', items: [dummy] }], upcoming: [], downloading: 0 },
      '/libraries/lib-music/items': { total: 21, items: [massive] },
    })
    renderAt('/library/lib-music')

    expect(await screen.findByRole('heading', { name: 'Your artists' })).toBeInTheDocument()
    expect(screen.getAllByText('Massive Attack').length).toBeGreaterThan(0)
    // The heading opens every artist, as a row's See all does.
    expect(screen.getByRole('link', { name: 'Your artists' })).toHaveAttribute('href', '/library/lib-music?view=all')
  })

  it('lists every album, and a genre of them with Play and Shuffle', async () => {
    const api = mockApi({
      '/libraries/lib-music/albums': { total: 1, items: [dummy] },
      '/libraries/lib-music/album-genres': [{ name: 'Trip hop', count: 1, backdrop: null }],
    })
    renderAt('/library/lib-music?view=albums&genre=Trip%20hop')

    expect(await screen.findByRole('link', { name: 'Albums', current: 'page' })).toBeInTheDocument()
    expect(await screen.findByText('1 Trip hop album')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Shuffle/ })).toBeInTheDocument()
    expect(api.mock.calls.some(([url]) => String(url).includes('/albums?genre=Trip+hop'))).toBe(true)
  })

  it("opens a genre on its own page, titled, with a way back to Genres and Play and Shuffle", async () => {
    const api = mockApi({ '/libraries/lib-music/albums': { total: 1, items: [dummy] } })
    renderAt('/library/lib-music?view=genre&genre=Trip%20hop')

    expect(await screen.findByRole('heading', { name: 'Trip hop' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Genres/ })).toHaveAttribute('href', '/library/lib-music?view=genres')
    expect(screen.queryByRole('link', { name: 'Albums' })).not.toBeInTheDocument()
    expect(await screen.findByText('1 album')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Shuffle/ })).toBeInTheDocument()
    expect(api.mock.calls.some(([url]) => String(url).includes('/albums?genre=Trip+hop'))).toBe(true)
  })

  it('has genre tiles that count albums and open them', async () => {
    mockApi({ '/libraries/lib-music/album-genres': [{ name: 'Trip hop', count: 2, backdrop: null }] })
    renderAt('/library/lib-music?view=genres')

    const tile = await screen.findByRole('link', { name: /Trip hop/ })
    expect(tile).toHaveTextContent('2 albums')
    expect(tile).toHaveAttribute('href', '/library/lib-music?view=genre&genre=Trip%20hop')
  })

  it('fans out album covers on a genre tile without an artist’s fanart', async () => {
    mockApi({
      '/libraries/lib-music/album-genres': [
        { name: 'Shoegaze', count: 2, backdrop: null, covers: ['/api/v1/images/items/a1/poster?v=1', '/api/v1/images/items/a2/poster?v=1'] },
        { name: 'Trip hop', count: 1, backdrop: '/api/v1/images/items/ar1/backdrop?v=1', covers: null },
      ],
    })
    renderAt('/library/lib-music?view=genres')

    const covered = await screen.findByRole('link', { name: /Shoegaze/ })
    expect(covered).toHaveClass('book-genre-tile')
    expect(covered.querySelectorAll('.book-genre-fan img')).toHaveLength(2)
    const fanart = screen.getByRole('link', { name: /Trip hop/ })
    expect(fanart).not.toHaveClass('book-genre-tile')
    expect(fanart.querySelectorAll('img')).toHaveLength(1)
  })
})
