import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { clearApiCache, type Me } from '../api/client'
import { SessionContext } from '../hooks/sessionContext'
import { DiscoverPage } from './DiscoverPage'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

const genres = [
  { name: 'Drama', movies: 0, shows: 1, backdrop: null },
  { name: 'Comedy', movies: 1, shows: 3, backdrop: null },
]

function openPage({ seerr, isAdmin, at = '/discover' }: { seerr: boolean; isAdmin: boolean; at?: string }) {
  const fetch = mockApi({
    '/requests/status': { configured: seerr, reachable: seerr },
    '/requests/music/status': { configured: false, reachable: false },
    '/requests/discover': [],
    '/requests/search': [],
    '/genres': genres,
  })
  render(
    <SessionContext.Provider value={{ me: { profileId: 'p', method: 'session', isAdmin } as Me, profiles: undefined }}>
      <MemoryRouter initialEntries={[at]}>
        <DiscoverPage />
      </MemoryRouter>
    </SessionContext.Provider>,
  )
  return fetch
}

describe('Discover', () => {
  it('says what it needs, with a way to set it up, when Seerr isn’t connected', async () => {
    openPage({ seerr: false, isAdmin: true })
    expect(await screen.findByText('Connect Seerr to search for new movies and shows, and request them.')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Set up Seerr' }).getAttribute('href')).toBe('/settings/connections')
    expect(screen.queryByRole('searchbox')).toBeNull()
    expect(await screen.findByText('Your library by genre')).toBeTruthy()
  })

  it('tells other profiles in plain words, with no link to Settings', async () => {
    openPage({ seerr: false, isAdmin: false })
    expect(await screen.findByText('Finding new movies and shows isn’t set up on this server yet.')).toBeTruthy()
    expect(screen.queryByRole('link', { name: 'Set up Seerr' })).toBeNull()
  })

  it('doesn’t search for a title it can’t find, when Seerr isn’t connected', async () => {
    const fetch = openPage({ seerr: false, isAdmin: true, at: '/discover?q=matrix' })
    expect(await screen.findByText('Connect Seerr to search for new movies and shows, and request them.')).toBeTruthy()
    expect(screen.queryByText(/Nothing found/)).toBeNull()
    expect(fetch.mock.calls.some(([url]) => String(url).includes('/requests/search'))).toBe(false)
  })

  it('offers the search when Seerr is connected, and counts one show as one', async () => {
    openPage({ seerr: true, isAdmin: true })
    expect(await screen.findByText('1 show')).toBeTruthy()
    expect(screen.getByText('1 movie and 3 shows')).toBeTruthy()
    expect(screen.getByRole('searchbox')).toBeTruthy()
    expect(screen.queryByText('Set up Seerr')).toBeNull()
  })
})
