import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../api/client'
import { tooBroad } from '../lib/setupGuide'
import { mockApi } from '../test/mockApi'
import { SetupGuidePage } from './SetupGuidePage'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

const main = { id: 'owner', name: 'Main profile', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }
const podcasts = { id: 'pod', name: 'Podcasts', type: 'Podcasts', folders: ['/data/podcasts'], itemCount: 0, lastScannedAt: null, scan: null }
const folder = (app: string, path: string, state = 'Found') => ({ app, kind: app, path, state, local: null, suggested: null, agreeing: 0, compared: 0, certain: false })
const guide = (skipped: string[] = []) => ({ opened: true, dismissed: false, skipped, apps: false, libraries: false, profiles: false, devices: false, away: false })

function renderAt(step: string) {
  render(
    <MemoryRouter initialEntries={[`/setup?step=${step}`]}>
      <Routes>
        <Route path="setup" element={<SetupGuidePage />} />
        <Route path="/" element={<p>Home</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** The body of the last request made as `METHOD /path`. */
function sent(api: ReturnType<typeof mockApi>, method: string, path: string) {
  const call = api.mock.calls.filter(([url, init]) => (init?.method ?? 'GET') === method && new URL(String(url), 'http://x').pathname === `/api/v1${path}`).at(-1)
  return call && JSON.parse(String(call[1]?.body))
}

describe('Setup guide', () => {
  it('asks for a name first, with no Skip, and gives it to the main profile', async () => {
    const api = mockApi({
      '/profiles': [main],
      'PUT /profiles/owner': { ...main, name: 'Dan' },
      'PUT /setup-guide': undefined,
      '/settings': { arr: [], pathMappings: [] },
      '/arr/discover': [],
    })
    renderAt('name')

    const name = await screen.findByRole('textbox', { name: 'Your name' })
    expect(name).toHaveValue('') // not "Main profile"
    expect(screen.queryByRole('button', { name: 'Skip' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled()

    fireEvent.change(name, { target: { value: 'Dan' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByRole('heading', { name: 'Connect your apps' })).toBeInTheDocument()
    expect(sent(api, 'PUT', '/profiles/owner')).toMatchObject({ name: 'Dan', color: '#a9c2ec' })
  })

  it('offers an app’s folder as a library, one of each kind, and adds it with Next', async () => {
    const api = mockApi({
      '/libraries': [podcasts],
      '/arr/folders': [folder('Sonarr', '/data/tv'), folder('Radarr', '/movies', 'Missing')],
      'POST /libraries': { ...podcasts, id: 'tv', name: 'TV Shows', type: 'TvShows', folders: ['/data/tv'] },
      'PUT /setup-guide': undefined,
      '/profiles': [main],
    })
    renderAt('libraries')

    expect(await screen.findByText('/data/tv')).toBeInTheDocument()
    expect(screen.getAllByText('No folder chosen')).toHaveLength(2) // Movies and Music
    expect(screen.getByText(/can’t see Radarr’s/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Next' }))

    expect(await screen.findByRole('heading', { name: 'Who’s watching?' })).toBeInTheDocument()
    expect(sent(api, 'POST', '/libraries')).toEqual({ name: 'TV Shows', type: 'TvShows', folders: ['/data/tv'] })
  })

  it('warns about a whole drive or home folder', async () => {
    mockApi({ '/libraries': [podcasts], '/arr/folders': [folder('Radarr', '/Volumes/Media Drive')], 'PUT /setup-guide': undefined })
    renderAt('libraries')

    expect(await screen.findByText(/That’s a whole drive or home folder/)).toBeInTheDocument()
    expect([tooBroad('/'), tooBroad('/Users/dan/'), tooBroad('C:\\'), tooBroad('/data'), tooBroad('/Volumes/Media/Films')]).toEqual([true, true, true, false, false])
  })

  it('adds a skip to what the server has already, rather than replacing it', async () => {
    const api = mockApi({ '/profiles': [main], '/setup-guide': guide(['apps']), 'PUT /setup-guide': undefined })
    renderAt('profiles')

    fireEvent.click(await screen.findByRole('button', { name: 'Skip' }))

    expect(await screen.findByRole('heading', { name: 'Watch on all your devices' })).toBeInTheDocument()
    await waitFor(() => expect(sent(api, 'PUT', '/setup-guide')?.skipped).toEqual(['apps', 'profiles']))
  })

  it('finishes without Tailscale, counting it as passed over', async () => {
    const remote = { tailscale: { installed: false, running: false, privateAccess: false }, inContainer: false, inMacApp: false, tailnetAddress: null }
    const api = mockApi({
      '/remote': remote,
      '/setup-guide': guide(),
      'PUT /setup-guide': undefined,
      '/libraries': [podcasts],
      '/arr/folders': [],
    })
    renderAt('away')

    expect(await screen.findByText(/Not installed on this server yet/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Get Tailscale/ })).toHaveAttribute('href', 'https://tailscale.com/download')
    expect(screen.queryByRole('button', { name: 'Not now' })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Finish' }))

    expect(await screen.findByRole('heading', { name: 'Nearly there' })).toBeInTheDocument()
    await waitFor(() => expect(sent(api, 'PUT', '/setup-guide')?.skipped).toEqual(['away']))
  })
})
