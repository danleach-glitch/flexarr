import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { PlaybackSection } from './PlaybackSection'
import { mockApi } from '../../test/mockApi'

const preferences = { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: true, subtitleSize: 'Standard', subtitlePosition: 'Bottom', subtitlesInBlackBars: false }

const renderPlayback = () =>
  render(
    <MemoryRouter>
      <PlaybackSection />
    </MemoryRouter>,
  )

describe('Playback settings', () => {
  it('turns books and podcasts in Continue watching off, keeping the other preferences as they are', async () => {
    const sent: unknown[] = []
    mockApi({
      '/me/playback-preferences': { ...preferences, listeningInContinue: true },
      'PUT /me/playback-preferences': (init?: RequestInit) => {
        const body: unknown = JSON.parse(String(init?.body))
        sent.push(body)
        return body
      },
    })
    renderPlayback()

    const toggle = await screen.findByRole('switch', { name: 'Books and podcasts in Continue watching' })
    expect(toggle).toBeChecked()
    fireEvent.click(toggle)

    await vi.waitFor(() => expect(sent).toEqual([{ ...preferences, listeningInContinue: false }]))
    expect(toggle).not.toBeChecked()
  })

  it('leaves the switch out for a server without it', async () => {
    mockApi({ '/me/playback-preferences': preferences })
    renderPlayback()

    expect(await screen.findByRole('switch', { name: /Skip intros/ })).toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: 'Books and podcasts in Continue watching' })).not.toBeInTheDocument()
  })
})
