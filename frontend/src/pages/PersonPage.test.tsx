import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../api/client'
import { SessionContext } from '../hooks/sessionContext'
import { mockApi } from '../test/mockApi'
import { PersonPage } from './PersonPage'

afterEach(() => vi.unstubAllGlobals())

const person = { id: 'p1', name: 'Ursula K. Le Guin', image: '/api/v1/images/people/p1?v=1', credits: [], biography: 'An author.' }

function renderPage(isAdmin: boolean) {
  const me = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin, tailscaleLogin: null } as unknown as Me
  return render(
    <SessionContext value={{ me, profiles: [] }}>
      <MemoryRouter initialEntries={['/person/p1']}>
        <Routes>
          <Route path="/person/:id" element={<PersonPage />} />
        </Routes>
      </MemoryRouter>
    </SessionContext>,
  )
}

describe('PersonPage', () => {
  it('offers Edit to admins only', async () => {
    mockApi({ '/people/p1': person })
    renderPage(false)
    await screen.findByRole('heading', { name: 'Ursula K. Le Guin' })
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull()
  })

  it('shows the new photo once saved', async () => {
    let version = 1
    mockApi({
      'GET /people/p1': () => ({ ...person, image: `/api/v1/images/people/p1?v=${version}` }),
      'GET /people/p1/candidates': { photos: [{ source: 'audible', image: '/api/v1/images/people/p1/candidates/audible' }], biographies: [] },
      'PUT /people/p1': () => {
        version = 2
        return { ...person, image: '/api/v1/images/people/p1?v=2', photoSource: 'audible', locked: true }
      },
    })
    renderPage(true)

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    fireEvent.click(await screen.findByRole('radio', { name: 'Audible' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await waitFor(() => expect(document.querySelector('.person-portrait img')?.getAttribute('src')).toContain('v=2'))
  })
})
