import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { PlaybackSection } from './PlaybackSection'
import { mockApi } from '../../test/mockApi'

const preferences = { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: true, subtitleSize: 'Standard', subtitlePosition: 'Bottom', subtitlesInBlackBars: false }

describe('Playback settings', () => {
  it('leaves books and podcasts in Continue watching to Settings › Podcasts and audiobooks', async () => {
    mockApi({ '/me/playback-preferences': { ...preferences, listeningInContinue: true } })
    render(
      <MemoryRouter>
        <PlaybackSection />
      </MemoryRouter>,
    )

    expect(await screen.findByRole('switch', { name: /Skip intros/ })).toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: 'Books and podcasts in Continue watching' })).not.toBeInTheDocument()
  })
})
