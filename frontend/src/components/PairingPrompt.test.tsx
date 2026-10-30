import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Me } from '../api/client'
import { SessionContext } from '../hooks/sessionContext'
import { setPairingOpenElsewhere } from '../lib/pairing'
import { mockApi } from '../test/mockApi'
import { PairingPrompt } from './PairingPrompt'
import { WaitingPairings } from './WaitingPairings'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  setPairingOpenElsewhere(undefined)
})

const admin: Me = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const child: Me = { ...admin, profileId: 'c1', isAdmin: false }
const later = () => new Date(Date.now() + 5 * 60_000).toISOString()
const livingRoom = () => ({ code: 'ABC-DEF', deviceName: 'Living room', expiresAt: later(), requestedFrom: '192.168.1.70', zone: 'Local' })
const bedroom = () => ({ code: 'GHJ-KLM', deviceName: 'Bedroom', expiresAt: later(), requestedFrom: '100.64.0.7', zone: 'Tailnet' })

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, profiles: undefined }}>
      <PairingPrompt />
    </SessionContext.Provider>,
  )

describe('Pairing prompt', () => {
  it('asks an admin about a waiting device, with its code and where it asked from', async () => {
    mockApi({ '/pairing/waiting': [livingRoom()] })
    renderAs(admin)
    expect(await screen.findByText('Connect “Living room”?')).toBeInTheDocument()
    expect(screen.getByText('It’s asking from your home network (192.168.1.70).')).toBeInTheDocument()
    expect(screen.getByText('ABC-DEF')).toBeInTheDocument()
  })

  it('doesn’t ask about a device turned away a moment ago and asking again by itself', async () => {
    const fetchMock = mockApi({ '/pairing/waiting': [{ ...livingRoom(), turnedAway: true }] })
    renderAs(admin)
    await waitFor(() => expect(fetchMock).toHaveBeenCalled())
    await act(async () => {})
    expect(screen.queryByText(/Connect “/)).not.toBeInTheDocument()
  })

  it('never asks someone who isn’t an admin', async () => {
    const fetchMock = mockApi({ '/pairing/waiting': [livingRoom()] })
    renderAs(child)
    await act(async () => {})
    expect(fetchMock).not.toHaveBeenCalled()
    expect(screen.queryByText(/Connect “/)).not.toBeInTheDocument()
  })

  it('stops asking a server without the list', async () => {
    const fetchMock = mockApi({})
    renderAs(admin)
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    // The 404 is read, then the tab is shown again: no second look.
    await act(() => new Promise((resolve) => setTimeout(resolve, 0)))
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })
    await act(async () => {})
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('allows, says so, and then the next device takes its place', async () => {
    let waiting = [livingRoom(), bedroom()]
    const approve = vi.fn(() => {
      waiting = waiting.slice(1)
      return undefined
    })
    mockApi({ '/pairing/waiting': () => waiting, 'POST /pairing/code/ABC-DEF/approve': approve })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Allow' }))

    expect(await screen.findByText('“Living room” is connected')).toBeInTheDocument()
    expect(approve).toHaveBeenCalledTimes(1)
    expect(await screen.findByText('Connect “Bedroom”?', {}, { timeout: 4000 })).toBeInTheDocument()
    expect(screen.getByText('It’s asking over Tailscale (100.64.0.7).')).toBeInTheDocument()
  })

  it('declines, and the device is not asked about again', async () => {
    const decline = vi.fn(() => undefined)
    mockApi({ '/pairing/waiting': () => [livingRoom()], 'POST /pairing/code/ABC-DEF/decline': decline })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Don’t allow' }))
    await vi.waitFor(() => expect(screen.queryByText('Connect “Living room”?')).not.toBeInTheDocument())
    expect(decline).toHaveBeenCalledTimes(1)
  })

  it('closes without deciding, leaving the device waiting', async () => {
    const fetchMock = mockApi({ '/pairing/waiting': () => [livingRoom()] })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Not now' }))
    expect(screen.queryByText('Connect “Living room”?')).not.toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it('says why an approval failed, and stays', async () => {
    mockApi({
      '/pairing/waiting': [livingRoom()],
      'POST /pairing/code/ABC-DEF/approve': new Response(JSON.stringify({ detail: 'That code has expired.' }), { status: 400 }),
    })
    renderAs(admin)
    fireEvent.click(await screen.findByRole('button', { name: 'Allow' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That code has expired.')
    expect(screen.getByText('Connect “Living room”?')).toBeInTheDocument()
  })

  it('leaves a device to Connect a device while that page is asking about it', async () => {
    setPairingOpenElsewhere('ABC-DEF')
    mockApi({ '/pairing/waiting': [livingRoom(), bedroom()] })
    renderAs(admin)
    expect(await screen.findByText('Connect “Bedroom”?')).toBeInTheDocument()
    expect(screen.queryByText('Connect “Living room”?')).not.toBeInTheDocument()
  })

  it('says what kind of device is asking', async () => {
    mockApi({ '/pairing/waiting': [{ ...livingRoom(), deviceType: 'AppleTv' }] })
    renderAs(admin)
    expect(await screen.findByText('Apple TV · asking from your home network (192.168.1.70)')).toBeInTheDocument()
  })

  it('ignores a kind of device newer than this page', async () => {
    mockApi({ '/pairing/waiting': [{ ...livingRoom(), deviceType: 'Toaster' }] })
    renderAs(admin)
    expect(await screen.findByText('It’s asking from your home network (192.168.1.70).')).toBeInTheDocument()
  })

  it('keeps out of the way of the Waiting to connect list, looking once for both', async () => {
    const fetchMock = mockApi({ '/pairing/waiting': [livingRoom()] })
    render(
      <SessionContext.Provider value={{ me: admin, profiles: undefined }}>
        <PairingPrompt />
        <WaitingPairings />
      </SessionContext.Provider>,
    )
    expect(await screen.findByText('Living room')).toBeInTheDocument()
    expect(screen.queryByText('Connect “Living room”?')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
