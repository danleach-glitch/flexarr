import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { ListeningSection } from './ListeningSection'
import { LayoutSection } from './LayoutSection'
import { SettingsPage } from './SettingsPage'
import { mockApi } from '../../test/mockApi'

const playback = { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: true, subtitleSize: 'Standard', subtitlePosition: 'Bottom', subtitlesInBlackBars: false }
const podcasts = { autoDownload: true, keepEpisodes: 3, speed: 1.3, showInSidebar: true, skipBackSeconds: 15, skipForwardSeconds: 30, smartRewind: true }

const renderPage = () =>
  render(
    <MemoryRouter>
      <ListeningSection />
    </MemoryRouter>,
  )

describe('Podcasts & audiobooks settings', () => {
  it('saves the shared speed, skips and smart rewind as podcast preferences', async () => {
    const sent: unknown[] = []
    mockApi({
      '/podcasts/preferences': podcasts,
      '/me/playback-preferences': playback,
      'PUT /podcasts/preferences': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as object
        sent.push(body)
        return { ...podcasts, ...body }
      },
    })
    renderPage()

    // A speed saved elsewhere that isn't one of the choices is offered too.
    const speed = await screen.findByRole('combobox', { name: 'Speed' })
    expect(speed).toHaveValue('1.3')
    fireEvent.change(speed, { target: { value: '1.5' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Skip back' }), { target: { value: '30' } })
    fireEvent.click(screen.getByRole('switch', { name: /Smart rewind/ }))

    await vi.waitFor(() => expect(sent).toEqual([{ speed: 1.5 }, { skipBackSeconds: 30 }, { smartRewind: false }]))
  })

  it('keeps a saved change when a later one fails', async () => {
    let state = podcasts
    mockApi({
      '/podcasts/preferences': () => state,
      '/me/playback-preferences': playback,
      'PUT /podcasts/preferences': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        if ('smartRewind' in body) return new Response('', { status: 500 })
        state = { ...state, ...body }
        return state
      },
    })
    renderPage()

    fireEvent.change(await screen.findByRole('combobox', { name: 'Skip back' }), { target: { value: '45' } })
    await vi.waitFor(() => expect(screen.getByRole('combobox', { name: 'Skip back' })).toHaveValue('45'))
    fireEvent.click(screen.getByRole('switch', { name: /Smart rewind/ }))

    expect(await screen.findByText("That didn't save. Try again.")).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: /Smart rewind/ })).toBeChecked()
    expect(screen.getByRole('combobox', { name: 'Skip back' })).toHaveValue('45')
  })

  it('leaves out what an older server has no settings for', async () => {
    mockApi({ '/podcasts/preferences': { autoDownload: true, keepEpisodes: 3, speed: 1, showInSidebar: true }, '/me/playback-preferences': playback })
    renderPage()

    expect(await screen.findByRole('combobox', { name: 'Speed' })).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Skip back' })).not.toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: /Smart rewind/ })).not.toBeInTheDocument()
  })

  it('has every profile’s Continue watching switch in Home & browse', async () => {
    const sent: unknown[] = []
    mockApi({
      '/settings/layout': { home: [], movies: [], shows: [], combinable: false, combineLibraries: false },
      '/me/playback-preferences': { ...playback, listeningInContinue: true },
      'PUT /me/playback-preferences': (init?: RequestInit) => {
        const body: unknown = JSON.parse(String(init?.body))
        sent.push(body)
        return body
      },
    })
    render(
      <MemoryRouter>
        <LayoutSection />
      </MemoryRouter>,
    )

    fireEvent.click(await screen.findByRole('switch', { name: /Books and podcasts in Continue watching/ }))
    await vi.waitFor(() => expect(sent).toEqual([{ ...playback, listeningInContinue: false }]))
  })

  it.each(['podcasts', 'audiobooks'])('sends the old /settings/%s address to the new page', async (old) => {
    mockApi({ '/me': { isAdmin: false }, '/podcasts/preferences': podcasts, '/me/playback-preferences': playback })
    render(
      <MemoryRouter initialEntries={[`/settings/${old}`]}>
        <Routes>
          <Route path="/settings/:section?" element={<SettingsPage />} />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByRole('heading', { name: 'Podcasts & audiobooks', level: 2 })).toBeInTheDocument()
  })
})
