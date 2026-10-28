import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Shell } from '../../App'
import { mockApi, movies } from '../../test/mockApi'

beforeAll(() => {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
})

const admin = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }

describe('Settings › Bring over from Plex', () => {
  it('in the Mac app, finds Plex in the folder chosen on the Mac, and leaves out profile pictures', async () => {
    const asked: (string | null)[] = []
    const looked: (string | null)[] = []
    mockApi({
      '/libraries': [movies],
      '/me': admin,
      '/profiles': [main],
      '/filesystem': { path: '/Volumes', parent: '/', directories: [], chooseOnMac: true },
      'POST /filesystem/choose': (_: unknown, url: URL) => {
        asked.push(url.searchParams.get('start'))
        return { path: '/Users/dan/Library/Application Support/Plex Media Server' }
      },
      '/import/plex': (_: unknown, url: URL) => {
        const path = url.searchParams.get('path')
        looked.push(path)
        return path
          ? { found: true, path, accounts: [{ id: 1, name: 'Dan', watched: 12, inProgress: 3 }] }
          : { found: false, path: null, accounts: [] }
      },
    })
    render(
      <MemoryRouter initialEntries={['/settings/import']}>
        <Shell />
      </MemoryRouter>,
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Choose Plex’s folder on the Mac…' }))

    expect(await screen.findByText(/Dan: 12 watched, 3 in progress/)).toBeInTheDocument()
    expect(asked).toEqual(['~/Library/Application Support/Plex Media Server'])
    expect(looked.at(-1)).toBe('/Users/dan/Library/Application Support/Plex Media Server/Plug-in Support/Databases/com.plexapp.plugins.library.db')
    expect(screen.queryByText('Use Plex profile pictures')).not.toBeInTheDocument()
  })
})
