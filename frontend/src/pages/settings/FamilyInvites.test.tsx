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

describe('Settings › Remote access › Family invites', () => {
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
      '/remote/guests/people': ['gran', 'sue'],
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
    // Your household is there already, waiting for setup.
    expect(screen.getByRole('heading', { name: 'Your household' })).toBeVisible()
    expect(screen.getByText('Set up Tailscale below first.')).toBeVisible()
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
      '/profiles': [main, gran],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
    })
    render(<FamilyInvites />)
    expect(await screen.findByText('Guests could reach more than Flexarr.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Check again' })).toBeVisible()
    // Guests stay listed, to remove someone; only adding waits.
    expect(await screen.findByText('Gran')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Remove Gran' })).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Add new guest' })).not.toBeInTheDocument()
    expect(screen.getByText('Adding guests waits for Tailscale setup, below.')).toBeVisible()
    expect(screen.queryByText('+ Add device')).not.toBeInTheDocument()
  })

  it('cancelling a new guest leaves the row empty for next time, and Escape closes it', async () => {
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
    })
    render(<FamilyInvites />)
    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Gran' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByText(/There’s already a profile called/)).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    expect(screen.getByLabelText('Name')).toHaveValue('')
    expect(screen.queryByText(/There’s already a profile called/)).not.toBeInTheDocument()
    fireEvent.keyDown(screen.getByLabelText('Name'), { key: 'Escape' })
    expect(screen.queryByLabelText('Name')).not.toBeInTheDocument()
  })

  it('adds a device by its code: waiting, then joined, then the panel closes and the device is New', async () => {
    let tv: unknown
    let joined = false
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran, { ...gran, id: 'sue', name: 'Aunt Sue' }],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': () => (joined ? [device(), device({ id: 'd2', name: 'Gran’s Apple TV', path: 'Direct' })] : [device()]),
      'POST /remote/guests/tv-code': (init?: RequestInit) => {
        tv = JSON.parse(String(init?.body))
        return { id: 'i2', name: '', profileId: 'gran', profileName: 'Gran', via: 'TvCode', createdAt: '', expiresAt: '' }
      },
    })
    render(<FamilyInvites />)

    // Gran has a device; Aunt Sue, with a profile but no devices yet, is suggested by name; the main profile never is.
    expect(await screen.findByText('iPad')).toBeVisible()

    fireEvent.click(document.getElementById('guest-add-gran')!)
    const connect = screen.getByRole('button', { name: 'Connect' })
    expect(connect).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Type the code their device shows'), { target: { value: 'abcd-efgh' } })
    fireEvent.click(connect)
    await waitFor(() => expect(tv).toEqual({ code: 'ABCD-EFGH', name: null, profileId: 'gran' }))

    // Waiting: the code box is gone, so nothing looks like it wants the code again.
    expect(await screen.findByText('Waiting for their device to join…')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Connect' })).not.toBeInTheDocument()

    joined = true
    expect(await screen.findByText('✓ Gran’s Apple TV joined', {}, { timeout: 4000 })).toBeVisible()
    expect(await screen.findByText('New', {}, { timeout: 4000 })).toBeVisible()
    expect(screen.queryByText(/Waiting for/)).not.toBeInTheDocument()
  }, 15000)

  it('refuses a name that’s taken, offering to add a device for that guest instead', async () => {
    let made = false
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran, { ...gran, id: 'sue', name: 'Aunt Sue' }],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
      'POST /profiles': () => {
        made = true
        return gran
      },
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'aunt sue' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByText(/There’s already a profile called/)).toBeVisible()
    expect(made).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Add a device for Aunt Sue instead' }))
    expect(await screen.findByLabelText('Type the code their device shows')).toBeVisible()
    expect(screen.getByText('Aunt Sue')).toBeVisible()
  })

  it('refuses the main profile’s name, with nothing to offer', async () => {
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [],
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Dan' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByText(/There’s already a profile called/)).toBeVisible()
    expect(screen.queryByRole('button', { name: /Add a device for .* instead/ })).not.toBeInTheDocument()
  })

  it('shows how each guest is connected, and removing one points to Tailscale', async () => {
    let removed = false
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/people': ['gran', 'sue'],
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
    // Asked first.
    expect(await screen.findByText('Remove Gran’s iPad?')).toBeVisible()
    expect(removed).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))
    expect(await screen.findByText(/can’t reach Flexarr any more. To free/)).toBeVisible()
    expect(screen.getByRole('link', { name: 'Tailscale’s Machines' })).toHaveAttribute('href', 'https://login.tailscale.com/admin/machines')
  })

  it('invites someone new by name, making their profile here', async () => {
    let made: unknown
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [],
      'POST /profiles': (init?: RequestInit) => {
        made = JSON.parse(String(init?.body))
        return { ...gran, id: 'dad', name: 'Dad' }
      },
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: ' Dad ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    await waitFor(() => expect(made).toMatchObject({ name: 'Dad' }))
    // Straight on to their device, under their name, before the profile list has caught up.
    expect(await screen.findByLabelText('Type the code their device shows')).toBeVisible()
    expect(screen.getByText('Dad')).toBeVisible()
  })

  it('cancelling before any device joins removes the profile it just made', async () => {
    let removed: string | undefined
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [],
      'POST /profiles': { ...gran, id: 'mum', name: 'Mum' },
      'DELETE /profiles/mum': () => {
        removed = 'mum'
        return undefined
      },
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Mum' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    expect(await screen.findByLabelText('Type the code their device shows')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(removed).toBe('mum'))
  })

  it('removing a guest, once confirmed, removes their devices and their profile', async () => {
    const gone: string[] = []
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/people': ['gran', 'sue'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
      'DELETE /remote/guests/devices/d1': () => {
        gone.push('device')
        return undefined
      },
      'DELETE /profiles/gran': () => {
        gone.push('profile')
        return undefined
      },
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Remove Gran' }))
    expect(await screen.findByText('Remove Gran?')).toBeVisible()
    expect(screen.getByText(/their profile and its watch history go/)).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Keep' }))
    expect(gone).toEqual([])

    fireEvent.click(screen.getByRole('button', { name: 'Remove Gran' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(gone).toEqual(['device', 'profile']))
  })

  it('a guest with no devices left stays listed, to add one or remove them', async () => {
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [],
      '/remote/guests/people': ['gran'],
    })
    render(<FamilyInvites />)

    expect(await screen.findByText('Gran')).toBeVisible()
    expect(screen.getByText('No devices')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Add a device for Gran' })).toHaveTextContent('+ Add device')
    expect(screen.getByRole('button', { name: 'Remove Gran' })).toBeVisible()
  })

  it('names each connection plainly', () => {
    expect(describeGuest(device({ path: 'Direct' }))).toMatch(/^Gran · Direct · last used/)
    expect(describeGuest(device({ path: 'PeerRelay' }))).toMatch(/Through your relay/)
    expect(describeGuest(device({ online: false }))).toMatch(/Offline/)
    // A household device: online or not, never how.
    expect(describeGuest(device({ household: true }), false)).toMatch(/^Online · last used/)
    expect(describeGuest(device({ household: true, online: false }), false)).toMatch(/^Offline · last used/)
  })

  describe('Your household (away from home)', () => {
    const claire = { ...main, id: 'claire', name: 'Claire', isMain: false, isCurrent: false, hiddenLibraryIds: [] }

    it('lists everyone who lives here but the main profile, and adds a device as theirs, not a guest', async () => {
      let tv: unknown
      mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, gran, claire],
        '/remote/guests/people': ['gran'],
        '/remote/guests/invites': [],
        '/remote/guests/devices': [device()],
        '/libraries': [],
        'POST /remote/guests/tv-code': (init?: RequestInit) => {
          tv = JSON.parse(String(init?.body))
          return { id: 'i3', name: '', profileId: 'claire', profileName: 'Claire', via: 'TvCode', createdAt: '', expiresAt: '', household: true }
        },
      })
      render(<FamilyInvites />)

      expect(await screen.findByRole('heading', { name: 'Your household' })).toBeVisible()
      // Claire has nothing away from home yet: one line, with Add device beside her name.
      expect(await screen.findByRole('button', { name: 'Add a device for Claire' })).toBeVisible()
      expect(document.getElementById('household-claire')).toHaveClass('compact')
      expect(screen.queryByText(/No devices away from home/)).not.toBeInTheDocument()
      // Gran is a guest, so only under Guests; the main profile is in neither.
      expect(document.getElementById('household-gran')).toBeNull()
      expect(document.getElementById('household-owner')).toBeNull()

      fireEvent.click(screen.getByRole('button', { name: 'Add a device for Claire' }))
      // Their app, signed in at home, offers it in its settings; not signed in, on its first screen.
      expect(screen.getByText('Settings › Server › Use away from home')).toBeVisible()
      expect(screen.getByText('Join someone’s Flexarr')).toBeVisible()
      fireEvent.change(screen.getByLabelText('Type the code their device shows'), { target: { value: 'abcd-efgh' } })
      fireEvent.click(screen.getByRole('button', { name: 'Connect' }))
      await waitFor(() => expect(tv).toEqual({ code: 'ABCD-EFGH', name: null, profileId: 'claire', household: true }))
    })

    it('asks for each list once for both groups, and lists no one as household until it knows who the guests are', async () => {
      let release: (ids: string[]) => void = () => undefined
      const fetch = mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, gran, claire],
        '/remote/guests/people': () => new Promise<string[]>((resolve) => (release = resolve)),
        '/remote/guests/invites': [],
        '/remote/guests/devices': [],
      })
      render(<FamilyInvites />)

      expect(await screen.findByRole('heading', { name: 'Your household' })).toBeVisible()
      await waitFor(() => expect(fetch.mock.calls.some(([url]) => String(url).endsWith('/profiles'))).toBe(true))
      // Gran is a guest, but that isn't known yet: nobody is listed rather than her for a moment.
      expect(document.getElementById('household-gran')).toBeNull()
      expect(document.getElementById('household-claire')).toBeNull()
      release(['gran'])
      expect(await screen.findByText('Claire')).toBeVisible()
      expect(document.getElementById('household-gran')).toBeNull()
      const asked = (path: string) => fetch.mock.calls.filter(([url]) => new URL(String(url), 'http://localhost').pathname === `/api/v1${path}`).length
      expect(asked('/remote/guests/devices')).toBe(1)
      expect(asked('/remote/guests/invites')).toBe(1)
      expect(asked('/remote/guests/people')).toBe(1)
    })

    it('files a household device under the profile it was added for, though it has switched to another', async () => {
      mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, claire, { ...claire, id: 'tom', name: 'Tom' }],
        '/remote/guests/people': [],
        '/remote/guests/invites': [],
        '/remote/guests/devices': [device({ id: 'c1', name: 'Claire’s iPad', profileId: 'tom', profileName: 'Tom', homeProfileId: 'claire', household: true })],
      })
      render(<FamilyInvites />)

      expect(await screen.findByText('iPad')).toBeVisible()
      expect(document.getElementById('household-claire')?.textContent).toMatch(/iPad/)
      expect(document.getElementById('household-tom')?.textContent).not.toMatch(/iPad/)
    })

    it('says when child lock keeps a device on its profile, and when they’re also on your tailnet', async () => {
      mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, claire, { ...claire, id: 'tom', name: 'Tom', isKids: true }],
        '/remote/guests/people': [],
        '/remote/guests/invites': [],
        '/remote/guests/devices': [],
        '/remote': { links: [{ login: 'claire@example.com', name: 'Claire', profileId: 'claire' }] },
      })
      render(<FamilyInvites />)

      expect(await screen.findByText('Child lock keeps their device on Tom.')).toBeVisible()
      expect(await screen.findByText('Also on your tailnet as claire@example.com')).toBeVisible()
      expect(document.getElementById('household-tom')?.textContent).not.toMatch(/tailnet/)
      expect(document.getElementById('household-claire')?.textContent).not.toMatch(/Child lock/)
    })

    it('signing out a household device says their profile stays', async () => {
      let removed = false
      mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, claire],
        '/remote/guests/people': [],
        '/remote/guests/invites': [],
        '/remote/guests/devices': () => (removed ? [] : [device({ id: 'c1', name: 'Claire’s iPad', profileId: 'claire', profileName: 'Claire', household: true })]),
        'DELETE /remote/guests/devices/c1': () => {
          removed = true
          return undefined
        },
      })
      render(<FamilyInvites />)

      fireEvent.click(await screen.findByRole('button', { name: 'Sign out Claire’s iPad' }))
      expect(await screen.findByText(/Claire’s profile and what they’ve watched stay/)).toBeVisible()
      expect(screen.getByText(/Downloads on it are deleted/)).toBeVisible()
      // Not a guest: no Remove guest for Claire anywhere.
      expect(screen.queryByRole('button', { name: 'Remove Claire' })).not.toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))
      await waitFor(() => expect(removed).toBe(true))
      expect(await screen.findByText(/is signed out/)).toBeVisible()
    })

    it('typing someone who lives here as a new guest offers Your household instead', async () => {
      let made = false
      mockApi({
        '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
        '/profiles': [main, claire],
        '/remote/guests/people': [],
        '/remote/guests/invites': [],
        '/remote/guests/devices': [],
        'POST /profiles': () => {
          made = true
          return claire
        },
      })
      render(<FamilyInvites />)

      fireEvent.click(await screen.findByRole('button', { name: 'Add new guest' }))
      fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'claire' } })
      fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
      fireEvent.click(await screen.findByRole('button', { name: 'Do they live with you? Add their device under Your household' }))
      expect(made).toBe(false)
      expect(await screen.findByLabelText('Type the code their device shows')).toBeVisible()
      expect(document.getElementById('household-claire')?.querySelector('#guest-code-claire')).not.toBeNull()
    })
  })

  it('moves a guest to Your household, once confirmed, and both groups catch up', async () => {
    let moved: string | undefined
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, gran],
      '/remote/guests/people': () => (moved ? [] : ['gran']),
      '/remote/guests/invites': [],
      '/remote/guests/devices': () => [device(moved ? { household: true, homeProfileId: 'gran' } : {})],
      'POST /remote/guests/people/gran/move-to-household': () => {
        moved = 'gran'
        return undefined
      },
    })
    render(<FamilyInvites />)

    fireEvent.click(await screen.findByRole('button', { name: 'Move to Your household' }))
    expect(await screen.findByText('Move Gran to Your household?')).toBeVisible()
    expect(screen.getByText('They’ll watch as Gran with nothing taken away, on every device they’ve joined with.')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Move' }))
    await waitFor(() => expect(moved).toBe('gran'))
    // Now under Your household with her iPad, and no longer a guest.
    await waitFor(() => expect(document.getElementById('household-gran')?.textContent).toMatch(/iPad/))
    expect(screen.queryByRole('button', { name: 'Remove Gran' })).not.toBeInTheDocument()
  })

  it('each guest card says which libraries they see, with Change opening their profile there', async () => {
    mockApi({
      '/remote/guests': setup({ clientId: 'abc', hasClient: true, check: safe }),
      '/profiles': [main, { ...gran, hiddenLibraryIds: ['l2'] }],
      '/remote/guests/people': ['gran'],
      '/remote/guests/invites': [],
      '/remote/guests/devices': [device()],
      '/libraries': [
        { id: 'l1', name: 'Films' },
        { id: 'l2', name: 'Adult films' },
        { id: 'l3', name: 'TV' },
      ],
    })
    render(<FamilyInvites />)

    expect(await screen.findByText(/Sees Films, TV/)).toBeVisible()
    expect(screen.getByRole('link', { name: 'Change the libraries Gran sees' })).toHaveAttribute('href', '/settings/profiles?edit=gran#libraries')
  })
})
