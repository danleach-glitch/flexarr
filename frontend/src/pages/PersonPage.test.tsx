import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../api/client'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { SessionContext } from '../hooks/sessionContext'
import { UnlockPrompt } from '../components/UnlockDialog'
import { mockApi, summary } from '../test/mockApi'
import { PersonPage } from './PersonPage'

afterEach(() => vi.unstubAllGlobals())

const book = summary({ id: 'b1', kind: 'Book', title: 'A Wizard of Earthsea' })
const film = summary({ id: 'f1', kind: 'Movie', title: 'Sintel' })
const person = {
  id: 'p1',
  name: 'Ursula K. Le Guin',
  image: '/api/v1/images/people/p1?v=1',
  credits: [{ item: book, role: 'Author', character: null }],
  biography: 'An author.',
}
const candidates = { photos: [{ source: 'audible', image: '/api/v1/images/people/p1/candidates/audible' }], biographies: [] }
const owner = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }

function renderPage(access: boolean | Partial<Me>) {
  const me = { ...owner, ...(typeof access === 'boolean' ? { isAdmin: access } : access) } as unknown as Me
  return render(
    <SessionContext value={{ me, profiles: [] }}>
      <AudioPlayerContext value={{ play: vi.fn() } as unknown as AudioPlayer}>
        <MemoryRouter initialEntries={['/person/p1']}>
          <Routes>
            <Route path="/person/:id" element={<PersonPage />} />
          </Routes>
          <UnlockPrompt />
        </MemoryRouter>
      </AudioPlayerContext>
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

  it('offers Edit only for people with book credits', async () => {
    mockApi({ '/people/p1': { ...person, credits: [{ item: film, role: 'Actor', character: 'Sintel' }] } })
    renderPage(true)
    await screen.findByRole('heading', { name: 'Ursula K. Le Guin' })
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull()
  })

  it('asks the main profile for the PIN when settings are locked, then edits', async () => {
    let unlocked = false
    mockApi({
      '/me': () => ({ ...owner, isAdmin: unlocked, canUnlock: !unlocked }),
      'POST /admin/unlock': () => {
        unlocked = true
        return { unlockedUntil: new Date(Date.now() + 900_000).toISOString() }
      },
      'GET /people/p1': person,
      'GET /people/p1/candidates': candidates,
    })
    renderPage({ isAdmin: false, canUnlock: true })

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    expect(screen.queryByRole('dialog', { name: 'Edit Ursula K. Le Guin' })).toBeNull()
    fireEvent.change(await screen.findByLabelText('Admin PIN'), { target: { value: '2468' } })
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }))
    expect(await screen.findByRole('dialog', { name: 'Edit Ursula K. Le Guin' })).toBeTruthy()
    expect(await screen.findByRole('radio', { name: 'Audible' })).toBeTruthy()
  })

  it('doesn’t edit when the PIN is cancelled', async () => {
    mockApi({ '/me': { ...owner, isAdmin: false, canUnlock: true }, 'GET /people/p1': person })
    renderPage({ isAdmin: false, canUnlock: true })

    fireEvent.click(await screen.findByRole('button', { name: 'Edit' }))
    await screen.findByLabelText('Admin PIN')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('shows the new photo once saved', async () => {
    let version = 1
    mockApi({
      'GET /people/p1': () => ({ ...person, image: `/api/v1/images/people/p1?v=${version}` }),
      '/me': owner,
      'GET /people/p1/candidates': candidates,
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
