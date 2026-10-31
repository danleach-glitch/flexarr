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

const me = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }
const base = { available: true, watchlistFull: false, importing: false, queued: 0 }

function open(routes: Record<string, unknown>) {
  mockApi({ '/libraries': [movies], '/me': me, '/profiles': [main], ...routes } as Parameters<typeof mockApi>[0])
  render(
    <MemoryRouter initialEntries={['/settings/trakt']}>
      <Shell />
    </MemoryRouter>,
  )
}

describe('Settings › Trakt', () => {
  it('connects by a code, which the page shows while the server waits', async () => {
    let connected = false
    const pending = { state: 'Waiting', userCode: 'ABCD1234', verificationUrl: 'https://trakt.tv/activate', qrUrl: 'https://trakt.tv/activate', expiresAt: '' }
    open({
      '/trakt/status': () => ({ ...base, state: 'disconnected', pending: connected ? pending : null }),
      'POST /trakt/connect': () => {
        connected = true
        return pending
      },
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Connect' }))

    expect(await screen.findByText('ABCD1234')).toBeVisible()
    expect(screen.getByRole('link', { name: 'trakt.tv/activate' })).toHaveAttribute('href', 'https://trakt.tv/activate')
  })

  it('previews the import before bringing anything across', async () => {
    let confirmed = false
    open({
      '/trakt/status': { ...base, state: 'connected', username: 'Dan', lastSyncAt: new Date().toISOString() },
      'POST /trakt/import': (_: unknown, url?: URL) => {
        if (url?.searchParams.get('confirm') === 'true') {
          confirmed = true
          return undefined
        }
        return { traktFilms: 12, traktEpisodes: 340, inLibrary: 200, newToFlexarr: 41, notOnServer: 150, unmatched: 2, toTrakt: 5 }
      },
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Look' }))

    expect(await screen.findByText('Trakt has 12 films and 340 episodes')).toBeVisible()
    expect(screen.getByText(/41 not yet watched in Flexarr/)).toBeVisible()
    expect(confirmed).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Bring across' }))
    await vi.waitFor(() => expect(confirmed).toBe(true))
  })

  it('says plainly when Trakt signed Flexarr out', async () => {
    open({ '/trakt/status': { ...base, state: 'needsSignIn', username: 'Dan' } })

    expect(await screen.findByText('Trakt signed Flexarr out.')).toBeVisible()
    expect(screen.getByText('Connect Dan again')).toBeVisible()
  })

  it('brings chosen Trakt lists across as collections', async () => {
    let saved: unknown
    open({
      '/trakt/status': { ...base, state: 'connected', username: 'Dan', importedAt: new Date().toISOString() },
      '/trakt/lists': [
        { id: 'dan/55', name: 'Sci-fi night', itemCount: 12, liked: false, selected: false },
        { id: 'sam/77', name: 'Best of 2024', owner: 'Sam', itemCount: 1, liked: true, selected: true },
      ],
      'PUT /trakt/lists': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return undefined
      },
    })

    const toggle = await screen.findByRole('switch', { name: /Sci-fi night/ })
    expect(screen.getByText('Sam · 1 title')).toBeVisible()
    fireEvent.click(toggle)

    await vi.waitFor(() => expect(saved).toEqual({ ids: ['sam/77', 'dan/55'] }))
  })
})
