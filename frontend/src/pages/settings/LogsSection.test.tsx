import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Shell } from '../../App'
import { mockApi, movies } from '../../test/mockApi'

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
}

describe('Settings › Logs', () => {
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
