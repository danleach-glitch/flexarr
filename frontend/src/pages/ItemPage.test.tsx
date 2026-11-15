import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../api/client'
import { SessionProvider } from '../hooks/session'
import { detail, mockApi, summary } from '../test/mockApi'
import { ItemPage } from './ItemPage'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

function show(homeVideo: boolean, folders = 1) {
  const series = summary({ id: 's1', kind: 'Series', title: 'Holidays', childCount: folders, homeVideo })
  const seasons = Array.from({ length: folders }, (_, i) => summary({ id: `f${i + 1}`, kind: 'Season', title: `Folder ${i + 1}`, parentId: 's1', seasonNumber: i + 1, childCount: 1, homeVideo }))
  const video = summary({ id: 'v1', kind: 'Episode', title: 'Beach', parentId: 'f1', seasonNumber: 1, episodeNumber: 1, homeVideo })
  mockApi({ '/items/s1': detail(series, { children: seasons, season: detail(seasons[0], { children: [video] }) }) })
  return render(
    <MemoryRouter initialEntries={['/item/s1']}>
      <Routes>
        <Route path="item/:id" element={<ItemPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('a home-video folder’s page', () => {
  it('speaks of folders and videos, with no tab for a lone folder', async () => {
    const page = show(true)
    const toolbar = await page.findByText('1 video')
    expect(toolbar).toBeInTheDocument()
    expect(screen.getByText('1 folder')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Mark folder as watched' })).toBeInTheDocument()
    expect(screen.queryByRole('tablist')).toBeNull()
    expect(screen.queryByText(/season|episode/i)).toBeNull()
  })

  it('keeps a tab per folder when there are several', async () => {
    show(true, 2)
    const tabs = await screen.findByRole('tablist', { name: 'Folders' })
    expect(within(tabs).getAllByRole('tab')).toHaveLength(2)
  })

  it('leaves a show’s seasons and episodes as they are', async () => {
    show(false)
    await screen.findByText('1 episode')
    expect(screen.getByText('1 season')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Mark season as watched' })).toBeInTheDocument()
    expect(screen.getByRole('tablist', { name: 'Seasons' })).toBeInTheDocument()
  })
})

describe('an episode’s ⋯', () => {
  it('goes to the show, with Delete apart below it, for the main profile', async () => {
    const series = summary({ id: 's1', kind: 'Series', title: 'Holidays', childCount: 1 })
    const episode = summary({ id: 'e1', kind: 'Episode', title: 'Pilot', parentId: 'f1', seasonNumber: 2, episodeNumber: 1 })
    mockApi({
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null },
      '/profiles': [],
      '/items/e1': detail(episode, { ancestors: [series] }),
      '/items/s1': detail(series),
    })
    render(
      <SessionProvider>
        <MemoryRouter initialEntries={['/item/e1']}>
          <Routes>
            <Route path="item/:id" element={<ItemPage />} />
          </Routes>
        </MemoryRouter>
      </SessionProvider>,
    )
    fireEvent.click(await screen.findByRole('button', { name: 'More actions' }))
    const menu = await screen.findByRole('menu')
    const items = within(menu).getAllByRole('menuitem').map((m) => m.textContent)
    expect(items).toEqual(['Go to show', 'Delete…'])
  })
})
