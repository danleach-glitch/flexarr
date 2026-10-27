import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Shell } from '../../App'
import { configureSentry } from '../../lib/sentry'
import { mockApi, movies, status } from '../../test/mockApi'

// The SDK itself isn't wanted here: only that the page tells it what the switch says.
vi.mock('../../lib/sentry', () => ({ configureSentry: vi.fn(), captureForSentry: vi.fn() }))

beforeAll(() => {
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo
})

afterEach(() => {
  vi.unstubAllGlobals()
})

const admin = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const main = { id: 'owner', name: 'Dan', color: '#a9c2ec', hasPin: true, isMain: true, isCurrent: true, hiddenLibraryIds: [], image: null }

const entry = (id: string, message: string, properties: Record<string, string> = {}) => ({
  id,
  timestamp: new Date().toISOString(),
  level: 'Warning',
  area: 'playback',
  source: 'Flexarr.Playback',
  message,
  exception: id === 'e1' ? 'System.Exception: boom' : null,
  properties,
})

const settings = {
  level: 'Information',
  areas: [
    { key: 'playback', name: 'Playback', level: null },
    { key: 'library', name: 'Library', level: 'Debug' },
  ],
  retentionDays: 14,
  maxSizeMb: 200,
  errorReports: false,
}

describe('Settings › Logs', () => {
  it('error reports are off until switched on, with what’s sent and never sent in sight', async () => {
    let sent: unknown
    mockApi({
      '/libraries': [movies],
      '/me': admin,
      '/profiles': [main],
      '/logs': { entries: [], nextCursor: null },
      '/logs/settings': settings,
      'PUT /logs/settings': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return { ...settings, errorReports: true }
      },
      '/logs/uploads': [],
      '/system/status': { ...status, version: '0.44.0+abc', errorReports: { enabled: true, installId: 'install-1' } },
    })

    render(
      <MemoryRouter initialEntries={['/settings/logs']}>
        <Shell />
      </MemoryRouter>,
    )

    const toggle = await screen.findByRole('switch', { name: 'Send error reports to Flexarr’s developer' })
    expect(toggle).not.toBeChecked()
    expect(screen.getByText(/titles, file names or paths/)).toBeVisible()
    expect(screen.getByRole('link', { name: 'What exactly is sent' })).toHaveAttribute(
      'href',
      'https://github.com/danleach-glitch/flexarr/blob/main/docs/logging.md#error-reports',
    )

    const dot = toggle.closest('label')!.querySelector('.status-dot')!
    expect(dot).toHaveClass('bad')

    fireEvent.click(toggle)
    await waitFor(() => expect(sent).toEqual({ errorReports: true }))
    await waitFor(() => expect(configureSentry).toHaveBeenCalledWith({ enabled: true, installId: 'install-1' }, '0.44.0'))
    expect(toggle).toBeChecked()
    expect(dot).toHaveClass('ok')
  })

  it('what’s sent folds away, and stays folded next time', async () => {
    mockApi({
      '/libraries': [movies],
      '/me': admin,
      '/profiles': [main],
      '/logs': { entries: [], nextCursor: null },
      '/logs/settings': settings,
      '/logs/uploads': [],
    })
    const page = () =>
      render(
        <MemoryRouter initialEntries={['/settings/logs']}>
          <Shell />
        </MemoryRouter>,
      )

    const first = page()
    const disclosure = await screen.findByRole('button', { name: 'What’s sent, and what never is' })
    expect(disclosure).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(disclosure)
    expect(disclosure).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByText(/titles, file names or paths/)).not.toBeVisible()
    first.unmount()

    page()
    expect(await screen.findByRole('button', { name: 'What’s sent, and what never is' })).toHaveAttribute('aria-expanded', 'false')
    localStorage.clear()
  })

  it('lists entries, follows a playback, pages back and saves a level as it changes', async () => {
    const queries: URLSearchParams[] = []
    let saved: unknown
    mockApi({
      '/libraries': [movies],
      '/me': admin,
      '/profiles': [main],
      '/logs': (_: RequestInit | undefined, url: URL) => {
        queries.push(url.searchParams)
        if (url.searchParams.get('before')) return { entries: [entry('e0', 'Older line')], nextCursor: null }
        return { entries: [entry('e1', 'Transcode failed for #abcd1234', { playbackId: 'abcd1234' })], nextCursor: 'c1' }
      },
      '/logs/settings': settings,
      'PUT /logs/settings': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return { ...settings, level: 'Warning' }
      },
      '/logs/uploads': [
        { id: 'u1', device: 'Living room', platform: 'tvos', profile: 'Dan', appVersion: '0.43.1', note: null, uploadedAt: new Date().toISOString(), sizeBytes: 2048 },
      ],
    })

    render(
      <MemoryRouter initialEntries={['/settings/logs']}>
        <Shell />
      </MemoryRouter>,
    )

    // Opening a line shows its exception and properties; its playback id filters to that playback.
    fireEvent.click(await screen.findByText('Transcode failed for #abcd1234'))
    expect(screen.getByText('System.Exception: boom')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'abcd1234' }))
    expect(screen.getByRole('searchbox', { name: 'Search the log' })).toHaveValue('abcd1234')
    await waitFor(() => expect(queries.some((q) => q.get('q') === 'abcd1234')).toBe(true))

    fireEvent.click(await screen.findByRole('button', { name: 'Load older' }))
    expect(await screen.findByText('Older line')).toBeInTheDocument()
    expect(queries.at(-1)?.get('before')).toBe('c1')

    fireEvent.change(screen.getByRole('combobox', { name: 'Default level' }), { target: { value: 'Warning' } })
    await waitFor(() => expect(saved).toEqual({ level: 'Warning' }))
    expect(await screen.findByText('Saved')).toBeInTheDocument()

    expect(screen.getByText('Living room')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Download' })).toHaveAttribute('href', '/api/v1/logs/uploads/u1')
    expect(screen.getByRole('link', { name: 'Download diagnostics' })).toHaveAttribute('href', '/api/v1/logs/diagnostics')
  })
})
