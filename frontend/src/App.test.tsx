import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App, { Shell } from './App'
import { mockApi, movies, status, summary } from './test/mockApi'

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Shell status={status} />
    </MemoryRouter>,
  )
}

describe('App', () => {
  it('shows server status and libraries on the home page', async () => {
    mockApi({ '/system/status': status, '/libraries': [movies], '/items/recent': [] })

    render(<App />)

    expect(await screen.findByText('0.1.0')).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: /Movies/ })).toBeInTheDocument()
  })

  it('asks for an API key when the server requires one, then retries with it', async () => {
    let authorized = false
    const fetchMock = mockApi({
      '/system/status': (init?: RequestInit) => {
        authorized = (init?.headers as Record<string, string> | undefined)?.['X-Api-Key'] === 'secret'
        return authorized ? status : new Response('', { status: 401 })
      },
      '/libraries': [],
      '/items/recent': [],
    })

    render(<App />)
    fireEvent.change(await screen.findByLabelText('API key'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Connect' }))

    expect(await screen.findByText('0.1.0')).toBeInTheDocument()
    expect(authorized).toBe(true)
    expect(fetchMock).toHaveBeenCalled()
  })

  it('reports when the server is unreachable', async () => {
    mockApi({ '/system/status': new Response('', { status: 503, statusText: 'Unavailable' }) })

    render(<App />)

    expect(await screen.findByText(/Can't reach the server/)).toBeInTheDocument()
  })
})

describe('Library page', () => {
  it('lists items and shows scan progress', async () => {
    mockApi({
      '/libraries': [movies],
      '/libraries/lib-movies/items': {
        total: 2,
        items: [summary({ id: 'm1', title: 'The Nice Guys', year: 2016, resolution: '1080p' }), summary({ id: 'm2', title: 'Arrival', year: 2016 })],
      },
    })

    renderAt('/library/lib-movies')

    expect(await screen.findByText('The Nice Guys')).toBeInTheDocument()
    expect(screen.getByText('1080p')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('4 of 10 new files')
  })
})

describe('Item page', () => {
  it('shows a season as an episode list with breadcrumbs', async () => {
    mockApi({
      '/libraries': [],
      '/items/season-6': {
        item: summary({ id: 'season-6', kind: 'Season', title: 'Season 6', seasonNumber: 6, childCount: 1 }),
        path: null,
        tvdbId: null,
        tmdbId: null,
        imdbId: null,
        ancestors: [summary({ id: 'series', kind: 'Series', title: '30 Rock' })],
        children: [
          summary({ id: 'e6', kind: 'Episode', title: 'Hey Baby Whats Wrong', seasonNumber: 6, episodeNumber: 6, episodeNumberEnd: 7, durationSeconds: 2640 }),
        ],
        files: [],
      },
    })

    renderAt('/item/season-6')

    const episode = await screen.findByRole('link', { name: /Hey Baby Whats Wrong/ })
    expect(within(episode).getByText('6–7')).toBeInTheDocument()
    expect(within(episode).getByText('44m')).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent('30 Rock')
  })
})

describe('Settings', () => {
  it('shows validation errors from the server when adding a library', async () => {
    mockApi({
      '/libraries': [],
      '/filesystem': { path: '/Volumes', parent: '/', directories: [{ name: 'Media', path: '/Volumes/Media' }] },
      'POST /libraries': new Response(JSON.stringify({ errors: { folders: ['/Volumes overlaps a folder in another library.'] } }), { status: 400 }),
    })

    renderAt('/settings')
    fireEvent.click(await screen.findByRole('button', { name: 'Add library' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Use this folder' }))
    fireEvent.click(within(screen.getByRole('form', { name: 'Add library' })).getByRole('button', { name: 'Add library' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('overlaps a folder in another library')
  })
})
