import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { requestTitle, searchMissing, type ShowMissing } from '../api/client'
import { ShowGaps } from './ShowGaps'

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  requestTitle: vi.fn(async () => undefined),
  searchMissing: vi.fn(async () => undefined),
}))

const gaps: ShowMissing = {
  source: 'sonarr',
  canRequest: true,
  canSearch: true,
  seasons: [
    { seasonNumber: 2, name: null, episodes: 10, year: 2021, poster: null, availability: 'None' },
    { seasonNumber: 3, name: null, episodes: 8, year: 2023, poster: null, availability: 'Requested' },
  ],
  episodes: [{ seasonNumber: 1, episodeNumber: 4, title: 'The Fourth', airDate: '2019-03-01T20:00:00Z', still: null, monitored: false }],
  upcoming: [{ seasonNumber: 4, episodeNumber: 1, title: null, airDate: '2099-11-12T20:00:00Z', still: null, monitored: true }],
  counts: [{ seasonNumber: 1, have: 9, aired: 10 }],
}

describe('ShowGaps', () => {
  it('requests just a missing season, requests a season’s missing episodes from Sonarr, and lists what’s coming', async () => {
    render(<ShowGaps showId="show" tmdbId={99} gaps={gaps} />)

    expect(screen.getByRole('heading', { name: 'Coming up' })).toBeInTheDocument()
    expect(screen.getByText(/^Airs /)).toBeInTheDocument()
    expect(screen.getAllByText('Requested')).toHaveLength(1)

    fireEvent.click(screen.getByRole('button', { name: 'Request Season 2' }))
    await waitFor(() => expect(requestTitle).toHaveBeenCalledWith('tv', 99, [2]))

    fireEvent.click(screen.getByRole('button', { name: 'Request season 1’s missing episodes' }))
    await waitFor(() => expect(searchMissing).toHaveBeenCalledWith('show', 1))
    const seasonOne = document.querySelector('.card') as HTMLElement
    await waitFor(() => expect(seasonOne).toHaveTextContent('Requested'))
    expect(screen.queryByText('Search')).not.toBeInTheDocument()
  })

  it('says Requested for a season’s missing episodes Sonarr is already looking for', () => {
    const looking = { ...gaps, episodes: gaps.episodes.map((e) => ({ ...e, monitored: true })) }
    render(<ShowGaps showId="show" tmdbId={99} gaps={looking} />)

    expect(screen.queryByRole('button', { name: 'Request season 1’s missing episodes' })).not.toBeInTheDocument()
    expect(document.querySelector('.card')).toHaveTextContent('Requested')
  })

  it('offers neither to a profile that can’t request or search', () => {
    render(<ShowGaps showId="show" tmdbId={99} gaps={{ ...gaps, canRequest: false, canSearch: false }} />)

    // A season it has some of is a card in the row, beside the ones it has none of, saying how many are missing.
    const titles = [...document.querySelectorAll('.card-title')].map((e) => e.textContent)
    expect(titles).toEqual(['Season 1', 'Season 2', 'Season 3'])
    expect(screen.getByText('1 episode missing')).toBeInTheDocument()
    expect(screen.queryByText('The Fourth')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
