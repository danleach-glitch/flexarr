import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it } from 'vitest'
import { clearApiCache, type Library } from '../api/client'
import { AudioPlayerProvider } from '../hooks/audioPlayer'
import { LibrariesContext } from '../hooks/librariesContext'
import { mockApi } from '../test/mockApi'
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

    expect(await screen.findByRole('link', { name: 'All artists', current: 'page' })).toBeInTheDocument()
    expect(screen.queryByText('Nothing here yet.')).not.toBeInTheDocument()
  })
})
