import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
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

describe('Country', () => {
  it('saves the country, and shows the names age limits then go by', async () => {
    let country: string | null = null
    let saved: unknown
    mockApi({
      '/podcasts/countries': countries,
      '/settings': () => ({ podcasts: { country, resolvedCountry: country ?? 'gb' } }),
      '/me': () => ({ ...me, ratingSystem: country === 'us' ? 'us' : 'uk' }),
      'PUT /settings': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        country = 'us'
        return { podcasts: { country, resolvedCountry: country } }
      },
    })
    render(<CountryRow />)

    expect(await screen.findByText(/U, PG, 12, 15, 18/)).toBeInTheDocument()
    const select = await screen.findByRole('combobox', { name: /Country/ })
    await screen.findByRole('option', { name: 'This server’s region (United Kingdom)' })

    fireEvent.change(select, { target: { value: 'us' } })

    expect(await screen.findByText(/G, PG, PG-13, TV-14, R/)).toBeInTheDocument()
    expect(saved).toEqual({ podcasts: { country: 'us' } })
  })

  it('says why a country wasn’t saved', async () => {
    mockApi({
      '/podcasts/countries': countries,
      '/settings': { podcasts: { country: null, resolvedCountry: 'gb' } },
      '/me': me,
      'PUT /settings': () => {
        throw new Error('Not today')
      },
    })
    render(<CountryRow />)
    await screen.findByRole('option', { name: 'United States' })

    fireEvent.change(screen.getByRole('combobox', { name: /Country/ }), { target: { value: 'us' } })

    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })
})
