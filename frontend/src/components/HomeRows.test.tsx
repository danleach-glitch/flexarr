import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import type { HomeRow, UpcomingItem } from '../api/client'
import { AudioPlayerProvider } from '../hooks/audioPlayer'
import { HomeRows } from './HomeRows'

const comingSoon: HomeRow = { id: 'upcoming', title: 'Coming soon', style: 'upcoming', items: [], link: null }
const dune: UpcomingItem = {
  id: 'radarr-Radarr-1', kind: 'Movie', title: 'Dune: Part Three', seriesTitle: null, seasonNumber: null, episodeNumber: null,
  date: new Date(Date.now() + 3 * 86_400_000).toISOString(), when: 'Digital release', overview: null, poster: null, still: null,
  libraryItemId: null, available: false, download: null,
}

function rows(upcoming?: UpcomingItem[]) {
  return render(
    <AudioPlayerProvider>
      <MemoryRouter>
        <HomeRows rows={[comingSoon]} upcoming={upcoming} />
      </MemoryRouter>
    </AudioPlayerProvider>,
  )
}

describe("A library Browse's Coming soon row", () => {
  it('shows what is on its way, with See all opening Coming soon', () => {
    rows([dune])
    expect(screen.getAllByText('Dune: Part Three').length).toBeGreaterThan(0)
    expect(screen.getByRole('link', { name: /Coming soon/ })).toHaveAttribute('href', '/upcoming')
  })

  it('is left out with nothing on its way', () => {
    rows()
    expect(screen.queryByText('Coming soon')).not.toBeInTheDocument()
  })
})
