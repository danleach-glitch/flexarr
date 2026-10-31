import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { GuestDevice, RemoteGuests } from '../../api/client'
import { describeGuest } from '../../lib/guests'
import { mockApi } from '../../test/mockApi'
import { FamilyInvites } from './FamilyInvites'

afterEach(() => {
  vi.unstubAllGlobals()
})

const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }
const gran = { ...main, id: 'gran', name: 'Gran', isMain: false, isCurrent: false }

const setup = (overrides: Partial<RemoteGuests> = {}): RemoteGuests => ({
  enabled: true,
  available: true,
  problem: null,
  tailscaleRunning: true,
  address: '100.64.0.1',
  dnsName: 'mini.tail.ts.net',
  flexarrPort: 8383,
  tag: 'tag:flexarr-guest',
  clientId: '',
  hasClient: false,
  snippet: '"hosts": { "flexarr": "100.64.0.1" }',
  check: null,
  relay: { port: null, working: false },
  waiting: 0,
  devices: 0,
  ...overrides,
})

const safe = { safe: true, problems: [], checkedAt: new Date().toISOString(), validated: true }

const device = (overrides: Partial<GuestDevice> = {}): GuestDevice => ({
  id: 'd1',
  name: 'Gran’s iPad',
  profileId: 'gran',
  profileName: 'Gran',
  deviceType: 'IPad',
  createdAt: new Date().toISOString(),
  lastUsedAt: new Date().toISOString(),
  address: '100.64.0.9',
  online: true,
  path: 'Derp',
  lastSeen: null,
  tailscaleAdminUrl: 'https://login.tailscale.com/admin/machines',
  ...overrides,
})

describe('Settings › Access › Family invites', () => {
  it('stays hidden while the server has them turned off', async () => {
    const fetch = mockApi({ '/remote/guests': setup({ enabled: false }) })
    const { container } = render(<FamilyInvites />)
    await waitFor(() => expect(fetch).toHaveBeenCalled())
    expect(container).toBeEmptyDOMElement()
  })

  it('says why when the server can’t do them', async () => {
    mockApi({ '/remote/guests': setup({ available: false, problem: 'Family invites need Tailscale on the server, which Flexarr in Docker can’t use yet.' }) })
    render(<FamilyInvites />)
    expect(await screen.findByText(/Flexarr in Docker can’t use yet/)).toBeVisible()
  })

  it('folds the setup away and back under one title', async () => {
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [],
    })
    render(<FamilyInvites />)
    const show = await screen.findByRole('button', { name: 'Show' })
    expect(screen.getByText('Tailscale setup')).toBeVisible()
    fireEvent.click(show)
    expect(screen.getByText('Paste this into your tailnet policy')).toBeVisible()
    expect(screen.getByText('Tailscale setup')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Hide' }))
    expect(screen.queryByText('Paste this into your tailnet policy')).not.toBeInTheDocument()
  })

  it('walks through the setup with the snippet, and saves the client', async () => {
    let saved: unknown
    mockApi({
      '/remote/guests': setup(),
      'PUT /remote/guests/client': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return setup({ clientId: 'abc', hasClient: true, check: safe })
      },
    })
    render(<FamilyInvites />)

    expect(await screen.findByText('Paste this into your tailnet policy')).toBeVisible()
    expect(screen.getByText(/"flexarr": "100.64.0.1"/)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Copy the policy' })).toBeVisible()

    fireEvent.change(screen.getByLabelText('Client ID'), { target: { value: ' abc ' } })
    fireEvent.change(screen.getByLabelText('Secret'), { target: { value: 'tskey-client-xyz' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(saved).toEqual({ clientId: 'abc', clientSecret: 'tskey-client-xyz' }))
  })

  it('shows what the safety check found, and nothing to invite with until it passes', async () => {
    mockApi({
      '/remote/guests': setup({
        clientId: 'abc',
        hasClient: true,
        check: { safe: false, problems: ['Guests could reach more than Flexarr.', 'address "100.64.0.2:22": want: Drop, got: Accept'], checkedAt: '', validated: true },
      }),
    })
    render(<FamilyInvites />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Guests could reach more than Flexarr.')
    expect(screen.getByRole('button', { name: 'Check again' })).toBeVisible()
    expect(screen.queryByText('Invite someone')).not.toBeInTheDocument()
  })

  it('lists guests by person, and adds a device by its code or a link', async () => {
    let tv: unknown
    let link: unknown
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran, { ...gran, id: 'sue', name: 'Aunt Sue' }],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
      'POST /remote/guests/invites': (init?: RequestInit) => {
        link = JSON.parse(String(init?.body))
        return { invite: { id: 'i1', name: '', profileId: 'gran', profileName: 'Gran', via: 'Link', createdAt: '', expiresAt: '' }, link: 'https://flexarr.io/join#abc' }
      },
      'POST /remote/guests/tv-code': (init?: RequestInit) => {
        tv = JSON.parse(String(init?.body))
        return { id: 'i2', name: '', profileId: 'gran', profileName: 'Gran', via: 'TvCode', createdAt: '', expiresAt: '' }
      },
    })
    render(<FamilyInvites />)

    // Gran has a device; Aunt Sue is offered to invite; the main profile never is.
    expect(await screen.findByText('Gran’s iPad')).toBeVisible()
    const who = screen.getByLabelText<HTMLSelectElement>('Who to invite')
    expect(Array.from(who.options).map((o) => o.text)).toEqual(['Invite someone…', 'Aunt Sue'])

    // Any device's code: phone, tablet or TV alike.
    fireEvent.click(screen.getByRole('button', { name: 'Add device' }))
    const connect = screen.getByRole('button', { name: 'Connect' })
    expect(connect).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Their code'), { target: { value: 'abcd-efgh' } })
    fireEvent.click(connect)
    await waitFor(() => expect(tv).toEqual({ code: 'ABCD-EFGH', name: null, profileId: 'gran' }))
    expect(await screen.findByText(/Their device joins in a moment/)).toBeVisible()

    // Or a link to message them, with its QR code.
    fireEvent.click(screen.getByRole('button', { name: 'Or send them a link instead' }))
    expect(await screen.findByText('https://flexarr.io/join#abc')).toBeVisible()
    expect(link).toEqual({ name: null, profileId: 'gran' })
    expect(screen.getByRole('img', { name: 'QR code of the invite' }).querySelector('svg')).not.toBeNull()
  })

  it('shows how each guest is connected, and removing one points to Tailscale', async () => {
    let removed = false
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/invites': [],
      '/remote/guests/devices': () => (removed ? [] : [device()]),
      'DELETE /remote/guests/devices/d1': () => {
        removed = true
        return undefined
      },
    })
    render(<FamilyInvites />)

    expect(await screen.findByText(/^Slow relay · last used/)).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Remove Gran’s iPad' }))
    expect(await screen.findByText(/can’t reach Flexarr any more/)).toBeVisible()
    expect(screen.getByRole('link', { name: 'Tailscale’s Machines' })).toHaveAttribute('href', 'https://login.tailscale.com/admin/machines')
  })

  it('names each connection plainly', () => {
    expect(describeGuest(device({ path: 'Direct' }))).toMatch(/^Gran · Direct · last used/)
    expect(describeGuest(device({ path: 'PeerRelay' }))).toMatch(/Through your relay/)
    expect(describeGuest(device({ online: false }))).toMatch(/Offline/)
  })
})
