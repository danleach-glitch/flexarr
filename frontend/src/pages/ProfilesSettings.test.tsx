import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import App, { Shell } from '../App'
import { mockApi, movies, status } from '../test/mockApi'

beforeAll(() => {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
  localStorage.clear()
  window.history.replaceState(null, '', '/')
})

const renderAt = (path: string) =>
  render(
    <MemoryRouter initialEntries={[path]}>
      <Shell />
    </MemoryRouter>,
  )

const adminHere = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }
const kids = { id: 'c1', name: 'Kids', color: '#e8a0bf', hasPin: false, isMain: false, isCurrent: false, hiddenLibraryIds: ['lib-movies'], image: null }
const shows = { ...movies, id: 'lib-shows', name: 'TV Shows', type: 'Shows', scan: null }

describe('Profile form', () => {
  it('switching Edit from one profile to another shows and saves the second one’s settings', async () => {
    let sent: Record<string, unknown> | undefined
    mockApi({
      // Settings lists every library (?all=true); the nav, only those this profile sees.
      '/libraries': (_: RequestInit | undefined, url: URL) => (url.searchParams.get('all') ? [movies, shows] : []),
      '/me': adminHere,
      '/profiles': [main, kids],
      'PUT /profiles/c1': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return kids
      },
    })

    renderAt('/settings/profiles')
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Dan' }))
    expect(await screen.findByRole('form', { name: 'Edit Dan' })).toBeInTheDocument()
    // The form stands in for Dan's row.
    expect(screen.queryByRole('button', { name: 'Edit Dan' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Edit Kids' }))

    const form = await screen.findByRole('form', { name: 'Edit Kids' })
    expect(screen.queryByRole('form', { name: 'Edit Dan' })).not.toBeInTheDocument()
    expect(within(form).getByLabelText('Name')).toHaveValue('Kids')
    expect(within(form).getByRole('switch', { name: 'Movies' })).not.toBeChecked()
    fireEvent.click(within(form).getByRole('button', { name: 'Save profile' }))

    await vi.waitFor(() => expect(sent).toBeDefined())
    expect(sent).toMatchObject({ name: 'Kids', color: '#e8a0bf', hiddenLibraryIds: ['lib-movies'] })
    expect(sent).not.toHaveProperty('currentPin')
  })

  it('asks for the PIN twice, names the colours and gives a new profile a colour of its own', async () => {
    let created: Record<string, unknown> | undefined
    mockApi({
      '/libraries': [],
      '/me': adminHere,
      '/profiles': [main, kids],
      'POST /profiles': (init?: RequestInit) => {
        created = JSON.parse(String(init?.body))
        return { ...kids, id: 'c2' }
      },
    })

    renderAt('/settings/profiles')
    fireEvent.click(await screen.findByRole('button', { name: 'Add a profile' }))
    const form = screen.getByRole('form', { name: 'New profile' })
    // Blue and pink are taken.
    expect(within(form).getByRole('radio', { name: 'Green' })).toHaveAttribute('aria-checked', 'true')

    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Sam' } })
    // The PIN fields wait until one is being added.
    expect(within(form).queryByLabelText('Confirm PIN')).not.toBeInTheDocument()
    fireEvent.click(within(form).getByRole('button', { name: 'Add a PIN' }))
    fireEvent.change(within(form).getByLabelText('PIN'), { target: { value: '2468' } })
    fireEvent.change(within(form).getByLabelText('Confirm PIN'), { target: { value: '2469' } })
    fireEvent.submit(form)
    expect(await within(form).findByRole('alert')).toHaveTextContent('The two PINs don’t match')
    expect(created).toBeUndefined()

    fireEvent.change(within(form).getByLabelText('Confirm PIN'), { target: { value: '2468' } })
    fireEvent.submit(form)
    await vi.waitFor(() => expect(created).toMatchObject({ name: 'Sam', pin: '2468', color: '#9fd8b9' }))
  })

  it('says the main profile’s PIN also unlocks Settings away from home', async () => {
    mockApi({ '/libraries': [], '/me': adminHere, '/profiles': [main, kids] })
    renderAt('/settings/profiles')
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Dan' }))
    expect(screen.getByText('The main profile’s PIN also unlocks Settings away from home.')).toBeInTheDocument()
  })
})

describe('Settings for another profile', () => {
  it('switches to the main profile with its PIN and comes back to the same page', async () => {
    window.history.replaceState(null, '', '/settings/playback')
    sessionStorage.setItem('flexarr.pickProfile', '1')
    sessionStorage.setItem('flexarr.pickProfileFor', 'owner') // what Switch to the main profile leaves before reloading
    let chosen = 'c1'
    mockApi({
      '/system/status': status,
      '/profiles': () => [{ ...main, isCurrent: chosen === 'owner' }, { ...kids, isCurrent: chosen === 'c1' }],
      '/me': () => ({ profileId: chosen, method: 'local', zone: 'Local', isAdmin: chosen === 'owner', tailscaleLogin: null }),
      'POST /profiles/owner/select': () => {
        chosen = 'owner'
        return { token: 'owner.sig', profile: main }
      },
      '/libraries': [],
      '/settings': { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null },
    })

    render(<App />)
    // Straight to the main profile's PIN, not the list.
    fireEvent.change(await screen.findByLabelText('PIN'), { target: { value: '1357' } })
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
    expect(window.location.pathname).toBe('/settings/playback')
    expect(screen.getByRole('link', { name: 'Playback' })).toHaveClass('active')
  })

  it('offers a button to switch to the main profile', async () => {
    mockApi({ '/libraries': [], '/me': { profileId: 'c1', method: 'session', zone: 'Local', isAdmin: false, tailscaleLogin: null }, '/profiles': [main, kids] })
    const reload = vi.fn()
    vi.stubGlobal('location', { ...window.location, reload })

    renderAt('/settings/server')
    fireEvent.click(await screen.findByRole('button', { name: 'Switch to the main profile' }))

    expect(sessionStorage.getItem('flexarr.pickProfileFor')).toBe('owner')
    expect(reload).toHaveBeenCalled()
  })
})

describe('Profile menu', () => {
  it('signs a browser out from any profile', async () => {
    const fetchMock = mockApi({
      '/libraries': [],
      '/me': { profileId: 'c1', method: 'session', zone: 'Internet', isAdmin: false, tailscaleLogin: null },
      '/profiles': [{ ...main, isCurrent: false }, { ...kids, isCurrent: true }],
      '/home': { featured: [], overviews: {}, rows: [], upcoming: [], downloading: 0 },
      'DELETE /session': undefined,
    })
    vi.stubGlobal('confirm', () => true)
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })

    renderAt('/')
    fireEvent.click(await screen.findByRole('button', { name: 'Profile: Kids' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Sign out of this browser' }))

    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/'))
    expect(fetchMock.mock.calls.some(([input, init]) => String(input).endsWith('/session') && init?.method === 'DELETE')).toBe(true)
  })
})
