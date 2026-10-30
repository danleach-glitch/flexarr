import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { deleteItem, setLibraryOrder } from '../api/client'
import { mockApi } from '../test/mockApi'
import { UnlockPrompt } from './UnlockDialog'

afterEach(() => vi.unstubAllGlobals())

const locked = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null, canUnlock: true }

function server() {
  const calls: string[] = []
  let unlocked = false
  mockApi({
    '/me': () => ({ ...locked, isAdmin: unlocked, canUnlock: !unlocked }),
    'POST /admin/unlock': (init?: RequestInit) => {
      const { pin } = JSON.parse(String(init?.body)) as { pin: string }
      calls.push(`unlock ${pin}`)
      if (pin !== '2468') return new Response(JSON.stringify({ title: 'That PIN isn’t right.' }), { status: 403 })
      unlocked = true
      return { unlockedUntil: new Date(Date.now() + 900_000).toISOString() }
    },
    'DELETE /items/f1': () => {
      calls.push(`delete ${unlocked ? 'unlocked' : 'locked'}`)
      return unlocked ? new Response(null, { status: 204 }) : new Response('', { status: 403 })
    },
    'PUT /profiles/owner/library-order': () => {
      calls.push('order')
      return new Response(JSON.stringify({ title: 'Not yours.' }), { status: 403 })
    },
  })
  return calls
}

describe('A change refused because settings are locked', () => {
  it('asks for the PIN, then goes ahead', async () => {
    const calls = server()
    render(
      <MemoryRouter>
        <UnlockPrompt />
      </MemoryRouter>,
    )

    const deleting = deleteItem('f1')
    fireEvent.change(await screen.findByLabelText('Main profile’s PIN'), { target: { value: '1111' } })
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That PIN isn’t right.')
    fireEvent.change(screen.getByLabelText('Main profile’s PIN'), { target: { value: '2468' } })
    fireEvent.click(screen.getByRole('button', { name: 'Unlock' }))

    await deleting
    expect(calls).toEqual(['delete locked', 'unlock 1111', 'unlock 2468', 'delete unlocked'])
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('fails as before when the PIN is cancelled', async () => {
    const calls = server()
    render(
      <MemoryRouter>
        <UnlockPrompt />
      </MemoryRouter>,
    )

    const deleting = deleteItem('f1')
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

    await expect(deleting).rejects.toMatchObject({ status: 403 })
    expect(calls).toEqual(['delete locked'])
  })

  it("isn't asked for a refusal that has nothing to do with the lock", async () => {
    mockApi({
      '/me': { ...locked, isAdmin: true, canUnlock: false },
      'PUT /profiles/owner/library-order': () => new Response(JSON.stringify({ title: 'Not yours.' }), { status: 403 }),
    })
    render(
      <MemoryRouter>
        <UnlockPrompt />
      </MemoryRouter>,
    )

    await expect(setLibraryOrder('owner', [])).rejects.toMatchObject({ status: 403 })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
