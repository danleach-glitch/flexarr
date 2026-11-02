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

  it('has genre tiles that count albums and open them', async () => {
    mockApi({ '/libraries/lib-music/album-genres': [{ name: 'Trip hop', count: 2, backdrop: null }] })
    renderAt('/library/lib-music?view=genres')

    const tile = await screen.findByRole('link', { name: /Trip hop/ })
    expect(tile).toHaveTextContent('2 albums')
    expect(tile).toHaveAttribute('href', '/library/lib-music?view=albums&genre=Trip%20hop')
  })
})
