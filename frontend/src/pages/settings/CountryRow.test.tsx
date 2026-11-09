import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
import { SessionProvider } from '../../hooks/session'
import { mockApi } from '../../test/mockApi'
import { CountryRow } from './CountryRow'

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
})

const countries = [
  { code: 'gb', name: 'United Kingdom' },
  { code: 'us', name: 'United States' },
]
const me = { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null }
const renderRow = () =>
  render(
    <SessionProvider>
      <CountryRow />
    </SessionProvider>,
  )

describe('Country', () => {
  it('saves the country, and shows the names age limits then go by', async () => {
    let country: string | null = null
    let saved: unknown
    mockApi({
      '/profiles': [],
      '/podcasts/countries': countries,
      '/settings': () => ({ podcasts: { country, resolvedCountry: country ?? 'gb', detectedCountry: 'gb' } }),
      '/me': () => ({ ...me, ratingSystem: country === 'us' ? 'us' : 'uk' }),
      'PUT /settings': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        country = 'us'
        return { podcasts: { country, resolvedCountry: country, detectedCountry: 'gb' } }
      },
    })
    renderRow()

    expect(await screen.findByText(/U, PG, 12, 15, 18/)).toBeInTheDocument()
    await screen.findByRole('option', { name: 'This server’s region (United Kingdom)' })

    fireEvent.change(screen.getByRole('combobox', { name: /Country/ }), { target: { value: 'us' } })

    expect(await screen.findByText(/G, PG, PG-13, TV-14, R/)).toBeInTheDocument()
    expect(saved).toEqual({ podcasts: { country: 'us' } })
  })

  it('names no region when the server can’t tell, whatever charts fall back to', async () => {
    mockApi({
      '/profiles': [],
      '/podcasts/countries': countries,
      '/settings': { podcasts: { country: null, resolvedCountry: 'us', detectedCountry: null } },
      '/me': { ...me, ratingSystem: 'uk' },
    })
    renderRow()

    expect(await screen.findByRole('option', { name: 'Not set' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /This server’s region/ })).toBeNull()
  })

  it('shows the pick and holds the select until it’s saved', async () => {
    let finish: (value: unknown) => void = () => {}
    mockApi({
      '/profiles': [],
      '/podcasts/countries': countries,
      '/settings': { podcasts: { country: null, resolvedCountry: 'gb', detectedCountry: 'gb' } },
      '/me': me,
      'PUT /settings': () => new Promise((resolve) => (finish = resolve)),
    })
    renderRow()
    await screen.findByRole('option', { name: 'United States' })
    const select = screen.getByRole<HTMLSelectElement>('combobox', { name: /Country/ })

    fireEvent.change(select, { target: { value: 'us' } })

    await vi.waitFor(() => expect(select).toBeDisabled())
    expect(select.value).toBe('us')
    finish({ podcasts: { country: 'us', resolvedCountry: 'us', detectedCountry: 'gb' } })
    await vi.waitFor(() => expect(select).toBeEnabled())
    expect(select.value).toBe('us')
  })

  it('says why a country wasn’t saved, and puts it back', async () => {
    mockApi({
      '/profiles': [],
      '/podcasts/countries': countries,
      '/settings': { podcasts: { country: null, resolvedCountry: 'gb', detectedCountry: 'gb' } },
      '/me': me,
      'PUT /settings': () => {
        throw new Error('Not today')
      },
    })
    renderRow()
    await screen.findByRole('option', { name: 'United States' })
    const select = screen.getByRole<HTMLSelectElement>('combobox', { name: /Country/ })

    fireEvent.change(select, { target: { value: 'us' } })

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(select.value).toBe('')
  })

  it('says so when it can’t load', async () => {
    mockApi({
      '/profiles': [],
      '/podcasts/countries': () => new Response('', { status: 500 }),
      '/settings': { podcasts: { country: null, resolvedCountry: 'gb', detectedCountry: 'gb' } },
      '/me': me,
    })
    renderRow()

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: /Country/ })).toBeDisabled()
  })
})
