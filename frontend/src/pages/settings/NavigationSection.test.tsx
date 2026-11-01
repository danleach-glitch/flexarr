import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { NavigationSection } from './NavigationSection'
import { mockApi } from '../../test/mockApi'

const me = { id: 'claire', name: 'Claire', color: '#a9c2ec', hasPin: false, isMain: false, isCurrent: true, hiddenLibraryIds: [], image: null, libraryOrder: [] }

describe('Navigation settings', () => {
  it('hides a menu item for the profile, and offers only what it has', async () => {
    const sent: unknown[] = []
    mockApi({
      '/profiles': [{ ...me, hiddenNavigation: ['upcoming'] }],
      '/podcasts/preferences': { autoDownload: true, keepEpisodes: 3, speed: 1, showInSidebar: true },
      '/livetv/status': { configured: false },
      'PUT /profiles/claire/navigation': (init?: RequestInit) => {
        const body: unknown = JSON.parse(String(init?.body))
        sent.push(body)
        return { ...me, hiddenNavigation: (body as { hidden: string[] }).hidden }
      },
    })
    render(
      <MemoryRouter>
        <NavigationSection />
      </MemoryRouter>,
    )

    const discover = await screen.findByRole('switch', { name: 'Discover' })
    expect(screen.getByRole('switch', { name: 'Coming soon' })).not.toBeChecked()
    // No music library and no Live TV source: nothing to hide.
    expect(screen.queryByRole('switch', { name: 'Playlists' })).not.toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: 'Live TV' })).not.toBeInTheDocument()

    fireEvent.click(discover)
    await vi.waitFor(() => expect(sent).toEqual([{ hidden: ['upcoming', 'discover'] }]))
    expect(discover).not.toBeChecked()
  })

  it('offers nothing to hide on a server from before it', async () => {
    mockApi({ '/profiles': [me], '/podcasts/preferences': { autoDownload: true, keepEpisodes: 3, speed: 1, showInSidebar: true }, '/livetv/status': { configured: true } })
    render(
      <MemoryRouter>
        <NavigationSection />
      </MemoryRouter>,
    )

    expect(await screen.findByRole('heading', { name: 'Navigation' })).toBeInTheDocument()
    await vi.waitFor(() => expect(screen.queryByRole('switch', { name: 'Watchlist' })).not.toBeInTheDocument())
  })
})
