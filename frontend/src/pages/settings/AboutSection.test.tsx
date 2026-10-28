import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Shell } from '../../App'
import { mockApi, movies, status } from '../../test/mockApi'

beforeAll(() => {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
})

const admin = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }

function open(routes: Record<string, unknown>) {
  mockApi({ '/libraries': [movies], '/me': admin, '/profiles': [main], '/system/status': status, '/changelog': '', ...routes })
  render(
    <MemoryRouter initialEntries={['/settings/about']}>
      <Shell />
    </MemoryRouter>,
  )
}

describe('Settings › About', () => {
  it('in the Mac app, turns starting at login on, and says when macOS wants it allowed', async () => {
    const sent: unknown[] = []
    open({
      '/mac-app': { startAtLogin: 'Off' },
      'PUT /mac-app/start-at-login': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)))
        return { startAtLogin: 'NeedsApproval' }
      },
    })

    fireEvent.click(await screen.findByRole('switch', { name: /Start when you log in/ }))

    expect(await screen.findByText(/Login Items and turn on Flexarr Server/)).toBeInTheDocument()
    expect(sent).toEqual([{ enabled: true }])
    expect(screen.getByRole('switch', { name: /Start when you log in/ })).toBeChecked()
  })

  it('elsewhere, has no start-at-login switch', async () => {
    open({})

    expect(await screen.findByText('Version')).toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: /Start when you log in/ })).not.toBeInTheDocument()
  })
})
