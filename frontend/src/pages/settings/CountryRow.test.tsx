import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache } from '../../api/client'
import { NoticeHost } from '../../components/Notice'
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
    await screen.findByRole('option', { name: 'Automatic (United Kingdom)' })

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
    expect(screen.queryByRole('option', { name: /Automatic/ })).toBeNull()
  })

  describe('picking', () => {
    const open = async (put: (country: string) => unknown) => {
      mockApi({
        '/profiles': [],
        '/podcasts/countries': countries,
        '/settings': { podcasts: { country: null, resolvedCountry: 'gb', detectedCountry: 'gb' } },
        '/me': me,
        'PUT /settings': (init?: RequestInit) => put((JSON.parse(String(init?.body)) as { podcasts: { country: string } }).podcasts.country),
      })
      const view = renderRow()
      await screen.findByRole('option', { name: 'United States' })
      return { select: screen.getByRole<HTMLSelectElement>('combobox', { name: /Country/ }), ...view }
    }

    it('shows the pick and stays usable, busy, until it’s saved', async () => {
      let finish: (value: unknown) => void = () => {}
      const asked: string[] = []
      const { select } = await open((country) => {
        asked.push(country)
        return new Promise((resolve) => (finish = resolve))
      })

      fireEvent.change(select, { target: { value: 'us' } })

      expect(select.value).toBe('us')
      expect(select).toBeEnabled()
      expect(select).toHaveAttribute('aria-busy', 'true')
      await waitFor(() => expect(asked).toEqual(['us']))
      expect(select).toBeEnabled()
      finish({ podcasts: { country: 'us', resolvedCountry: 'us', detectedCountry: 'gb' } })
      await waitFor(() => expect(select).not.toHaveAttribute('aria-busy'))
      expect(select.value).toBe('us')
    })

    it('saves only where keyboard steps come to rest', async () => {
      const asked: string[] = []
      const { select } = await open((country) => {
        asked.push(country)
        return { podcasts: { country, resolvedCountry: country, detectedCountry: 'gb' } }
      })

      fireEvent.change(select, { target: { value: 'us' } })
      fireEvent.change(select, { target: { value: 'gb' } })
      fireEvent.change(select, { target: { value: 'us' } })
      fireEvent.change(select, { target: { value: 'gb' } })

      await waitFor(() => expect(asked).toEqual(['gb']))
      await waitFor(() => expect(select).not.toHaveAttribute('aria-busy'))
      expect(select.value).toBe('gb')
    })

    it('keeps a later pick when an earlier save fails, and saves it after', async () => {
      const asked: string[] = []
      let fail: (reason: Response) => void = () => {}
      const { select } = await open((country) => {
        asked.push(country)
        return country === 'us' ? new Promise((resolve) => (fail = resolve)) : { podcasts: { country, resolvedCountry: country, detectedCountry: 'gb' } }
      })

      fireEvent.change(select, { target: { value: 'us' } })
      await waitFor(() => expect(asked).toEqual(['us']))
      fireEvent.change(select, { target: { value: 'gb' } })
      fail(new Response('', { status: 500 }))

      await waitFor(() => expect(asked).toEqual(['us', 'gb']))
      await waitFor(() => expect(select).not.toHaveAttribute('aria-busy'))
      expect(select.value).toBe('gb')
      expect(screen.queryByRole('alert')).toBeNull()
    })

    it('says Saved once it has, and not before', async () => {
      let finish: (value: unknown) => void = () => {}
      const { select } = await open(() => new Promise((resolve) => (finish = resolve)))

      fireEvent.change(select, { target: { value: 'us' } })
      expect(screen.queryByText('Saved')).toBeNull()
      await new Promise((done) => setTimeout(done, 700))
      expect(screen.queryByText('Saved')).toBeNull()

      finish({ podcasts: { country: 'us', resolvedCountry: 'us', detectedCountry: 'gb' } })
      expect(await screen.findByText('Saved')).toHaveAttribute('role', 'status')
      // A further pick isn't saved yet.
      fireEvent.change(select, { target: { value: 'gb' } })
      expect(screen.queryByText('Saved')).toBeNull()
    })

    it('says a save failed after leaving, where it can still be seen', async () => {
      render(<NoticeHost />)
      const { select, unmount } = await open(() => new Response(JSON.stringify({ title: 'Not today' }), { status: 500 }))

      fireEvent.change(select, { target: { value: 'us' } })
      unmount()

      expect(await screen.findByText('Couldn’t save the country: Not today')).toBeInTheDocument()
    })

    it('doesn’t raise a notice for a failure the row can show', async () => {
      render(<NoticeHost />)
      const { select } = await open(() => new Response(JSON.stringify({ title: 'Not today' }), { status: 500 }))

      fireEvent.change(select, { target: { value: 'us' } })

      expect(await screen.findByRole('alert')).toHaveTextContent('Not today')
      expect(screen.queryByText(/Couldn’t save the country/)).toBeNull()
    })

    it('saves a pick made just before leaving', async () => {
      const asked: string[] = []
      const { select, unmount } = await open((country) => {
        asked.push(country)
        return { podcasts: { country, resolvedCountry: country, detectedCountry: 'gb' } }
      })

      fireEvent.change(select, { target: { value: 'us' } })
      unmount()

      await waitFor(() => expect(asked).toEqual(['us']))
    })
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

  it('offers Try again when it can’t load, and works once it can', async () => {
    let down = true
    mockApi({
      '/profiles': [],
      '/podcasts/countries': () => (down ? new Response('', { status: 500 }) : countries),
      '/settings': { podcasts: { country: null, resolvedCountry: 'gb', detectedCountry: 'gb' } },
      '/me': me,
    })
    renderRow()

    expect(await screen.findByRole('alert')).toHaveTextContent('Couldn’t load the countries')
    expect(screen.getByRole('combobox', { name: /Country/ })).toBeDisabled()

    down = false
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    await screen.findByRole('option', { name: 'United States' })
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull()
    expect(screen.getByRole('combobox', { name: /Country/ })).toBeEnabled()
  })
})
