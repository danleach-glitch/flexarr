import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Shell } from '../../App'
import type { Updates } from '../../api/client'
import { mockApi, movies } from '../../test/mockApi'

beforeAll(() => {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
})

const admin = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }

const updates = (over: Partial<Updates> = {}): Updates => ({
  version: '0.50.0',
  isDevelopment: false,
  install: 'MacLaunchAgent',
  canInstall: true,
  latest: { version: '0.51.0', notes: '### Updates\n- Update the server from **Settings**', url: 'https://github.com/danleach-glitch/flexarr/releases/tag/v0.51.0', publishedAt: '2026-10-28T12:00:00Z' },
  available: true,
  lastChecked: new Date().toISOString(),
  checkError: null,
  automatic: false,
  progress: { stage: 'Idle', version: null, received: 0, total: null, error: null },
  lastResult: null,
  inUse: [],
  ...over,
})

function open(routes: Record<string, unknown>) {
  mockApi({ '/libraries': [movies], '/me': admin, '/profiles': [main], ...routes })
  render(
    <MemoryRouter initialEntries={['/settings/updates']}>
      <Shell />
    </MemoryRouter>,
  )
}

describe('Settings › Updates', () => {
  it('shows a newer release with its notes, marks the page in the list, and installs after asking', async () => {
    let installed = false
    open({
      '/updates': updates({ inUse: ['Something is playing.'] }),
      'POST /updates/install': () => {
        installed = true
        return updates({ progress: { stage: 'Downloading', version: '0.51.0', received: 0, total: 100, error: null } })
      },
    })

    expect(await screen.findByText('Flexarr 0.51.0 is available')).toBeVisible()
    expect(screen.getByText('Update the server from')).toBeVisible()
    expect(await screen.findByRole('img', { name: 'Flexarr 0.51.0 is available' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Update now' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText('Something is playing.')).toBeVisible()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Update anyway' }))

    await waitFor(() => expect(installed).toBe(true))
    expect(await screen.findByText('Downloading Flexarr 0.51.0')).toBeVisible()
  })

  it('on Docker, says how to update instead, and offers no automatic install', async () => {
    open({ '/updates': updates({ install: 'Docker', canInstall: false }) })

    expect(await screen.findByText(/docker compose pull flexarr/)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Update now' })).not.toBeInTheDocument()
    expect(screen.getByText(/can’t install them/)).toBeVisible()
    expect(screen.queryByRole('switch', { name: /Install updates automatically/ })).not.toBeInTheDocument()
  })

  it('a development build shows the latest release but is never offered it', async () => {
    open({ '/updates': updates({ version: '0.51.0-dev.1a2b3c4d', isDevelopment: true, available: false, canInstall: false }) })

    expect(await screen.findByText('0.51.0-dev.1a2b3c4d')).toBeVisible()
    expect(screen.getByText('Latest release: 0.51.0')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Update now' })).not.toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: /Install updates automatically/ })).not.toBeInTheDocument()
  })

  it('automatic updates are off to begin with, and saved when switched on', async () => {
    let sent: unknown
    open({
      '/updates': updates({ available: false, latest: null }),
      'PUT /updates/settings': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return updates({ available: false, latest: null, automatic: true })
      },
    })

    const toggle = await screen.findByRole('switch', { name: /Install updates automatically/ })
    expect(toggle).not.toBeChecked()
    fireEvent.click(toggle)
    await waitFor(() => expect(sent).toEqual({ automatic: true }))
    await waitFor(() => expect(toggle).toBeChecked())
  })
})
