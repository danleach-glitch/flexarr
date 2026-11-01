import { fireEvent, render, screen, within } from '@testing-library/react'
import { act } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../api/client'
import { SessionContext } from '../hooks/sessionContext'
import { mockApi } from '../test/mockApi'
import { WaitingPairings } from './WaitingPairings'

afterEach(() => {
  vi.unstubAllGlobals()
})

const admin: Me = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const child: Me = { ...admin, profileId: 'c1', isAdmin: false }
const later = () => new Date(Date.now() + 5 * 60_000).toISOString()
const livingRoom = () => ({ code: 'ABC-DEF', deviceName: 'Living room', expiresAt: later(), requestedFrom: '192.168.1.70', zone: 'Local', deviceType: 'AppleTv' })
const pixel = () => ({ code: 'GHJ-KLM', deviceName: 'Pixel', expiresAt: later(), requestedFrom: '100.64.0.7', zone: 'Tailnet', deviceType: 'AndroidPhone' })
const old = () => ({ code: 'NPQ-RST', deviceName: 'Bedroom', expiresAt: later(), requestedFrom: null, zone: 'Internet' })

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, profiles: undefined }}>
      <WaitingPairings />
    </SessionContext.Provider>,
  )

const row = (name: string) => screen.getByText(name).closest('li') as HTMLElement

describe('Waiting to connect', () => {
  it('lists each waiting device with its kind, where it asked from and its code', async () => {
    mockApi({ '/pairing/waiting': [livingRoom(), pixel(), old()] })
    renderAs(admin)
    expect(await screen.findByText('Living room')).toBeInTheDocument()
    expect(within(row('Living room')).getByText('Apple TV, from your home network (192.168.1.70)')).toBeInTheDocument()
    expect(within(row('Living room')).getByText('ABC-DEF')).toBeInTheDocument()
    expect(within(row('Pixel')).getByText('Android phone, over Tailscale (100.64.0.7)')).toBeInTheDocument()
    // An app that hasn't said what it is: just where from, with a word of caution off the home network.
    expect(within(row('Bedroom')).getByText('from the internet')).toBeInTheDocument()
    expect(within(row('Bedroom')).getByText(/Not from your home network/)).toBeInTheDocument()
    expect(within(row('Living room')).queryByText(/Not from your home network/)).not.toBeInTheDocument()
  })

  it('says when nothing is waiting', async () => {
    mockApi({ '/pairing/waiting': [] })
    renderAs(admin)
    expect(await screen.findByText('No devices are waiting. When one asks to connect, it shows here.')).toBeInTheDocument()
  })

  it('allows one, says so, and the rest stay', async () => {
    let waiting = [livingRoom(), pixel()]
    const approve = vi.fn(() => {
      waiting = waiting.slice(1)
      return undefined
    })
    mockApi({ '/pairing/waiting': () => waiting, 'POST /pairing/code/ABC-DEF/approve': approve })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Allow Living room' }))
    expect(await screen.findByRole('status')).toHaveTextContent('“Living room” is connected.')
    expect(approve).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('Living room')).not.toBeInTheDocument()
    expect(screen.getByText('Pixel')).toBeInTheDocument()
  })

  it('declines one, and it leaves the list', async () => {
    let waiting = [livingRoom()]
    const decline = vi.fn(() => {
      waiting = []
      return undefined
    })
    mockApi({ '/pairing/waiting': () => waiting, 'POST /pairing/code/ABC-DEF/decline': decline })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Don’t allow Living room' }))
    expect(await screen.findByText('No devices are waiting. When one asks to connect, it shows here.')).toBeInTheDocument()
    expect(decline).toHaveBeenCalledTimes(1)
  })

  it('says why an approval failed, and keeps the device', async () => {
    mockApi({
      '/pairing/waiting': [livingRoom()],
      'POST /pairing/code/ABC-DEF/approve': new Response(JSON.stringify({ detail: 'That code has expired.' }), { status: 400 }),
    })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Allow Living room' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That code has expired.')
    expect(screen.getByText('Living room')).toBeInTheDocument()
  })

  it('isn’t shown to someone who isn’t an admin', async () => {
    const fetchMock = mockApi({ '/pairing/waiting': [livingRoom()] })
    renderAs(child)
    await act(async () => {})
    expect(fetchMock).not.toHaveBeenCalled()
    expect(screen.queryByText('Waiting to connect')).not.toBeInTheDocument()
  })

  it('isn’t shown by a server without the list', async () => {
    const fetchMock = mockApi({})
    renderAs(admin)
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    await act(async () => {})
    expect(screen.queryByText('Waiting to connect')).not.toBeInTheDocument()
  })
})
