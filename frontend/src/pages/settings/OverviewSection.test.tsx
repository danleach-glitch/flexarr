import { render, screen } from '@testing-library/react'
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
const score = { score: 87, titles: 100, withProblems: 13, duplicates: 2, unreadable: 0, standardDefinition: 0, missingArtwork: 11, fileProblems: 0 }

function open(routes: Record<string, unknown>) {
  const fetchMock = mockApi({ '/libraries': [movies], '/me': admin, '/profiles': [main], '/system/status': status, '/changelog': '', '/arr/status': [], ...routes })
  render(
    <MemoryRouter initialEntries={['/settings']}>
      <Shell />
    </MemoryRouter>,
  )
  return fetchMock
}

const asked = (fetchMock: ReturnType<typeof mockApi>, path: string) =>
  fetchMock.mock.calls.some(([input]) => new URL(String(input), 'http://localhost').pathname === `/api/v1${path}`)

describe('Settings › Overview', () => {
  it('shows library health’s score from the score alone, without the whole report', async () => {
    const fetchMock = open({ '/library-health/score': score, '/library-health': () => new Response('', { status: 500 }) })

    expect(await screen.findByText(/Score 87/)).toHaveTextContent(/Score 87\s+13 titles need a look/)
    expect(asked(fetchMock, '/library-health')).toBe(false)
  })

  it('on a server without the score alone, takes it from the report', async () => {
    open({ '/library-health': { score } })

    expect(await screen.findByText(/Score 87/)).toHaveTextContent(/Score 87\s+13 titles need a look/)
  })
})
