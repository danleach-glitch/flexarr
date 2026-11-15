import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
import { SessionProvider } from '../../hooks/session'
import { mockApi } from '../../test/mockApi'
import { SettingsPage } from './SettingsPage'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

function open(me: Record<string, unknown>) {
  mockApi({
    '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null, ...me },
    '/profiles': [],
  })
  render(
    <SessionProvider>
      <MemoryRouter initialEntries={['/settings/playback']}>
        <Routes>
          <Route path="settings/:section" element={<SettingsPage />} />
        </Routes>
      </MemoryRouter>
    </SessionProvider>,
  )
}

describe('Settings’ Lock settings', () => {
  const soon = new Date(Date.now() + 10 * 60_000).toISOString()

  it('isn’t offered while there’s no admin PIN to lock with', async () => {
    open({ adminPinMissing: true, unlockedUntil: soon })
    expect(await screen.findByRole('link', { name: 'Choose an admin PIN' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Lock settings' })).toBeNull()
  })

  it('is offered once a PIN is set and the settings are unlocked', async () => {
    open({ adminPinMissing: false, unlockedUntil: soon })
    expect(await screen.findByRole('button', { name: 'Lock settings' })).toBeInTheDocument()
  })
})
