import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi, summary } from '../../test/mockApi'
import { HealthSection } from './HealthSection'

afterEach(() => vi.unstubAllGlobals())

const album = (id: string, title: string) => summary({ id, kind: 'Album', title, seriesTitle: 'Beyoncé' })
const artist = (id: string, title: string) => summary({ id, kind: 'Artist', title })

const health = {
  score: { score: 50, titles: 4, withProblems: 2, duplicates: 0, unreadable: 0, standardDefinition: 0, missingArtwork: 0, fileProblems: 0, albumsWithoutCover: 1, duplicateAlbums: 1, albumsWithRepeatedTracks: 1, splitArtists: 1 },
  duplicates: [],
  reclaimableBytes: 0,
  unreadable: [],
  missingArtwork: [],
  matchedByTitle: [],
  standardDefinition: [],
  standardDefinitionTotal: 0,
  downloading: {},
  held: [],
  albumsWithoutCover: [album('c1', '25')],
  duplicateAlbums: [{ albums: [album('l1', 'Lemonade'), album('l2', 'Lemonade (Deluxe)')], artist: 'Beyoncé' }],
  albumsWithRepeatedTracks: [{ album: album('r1', 'Abbey Road'), artist: 'The Beatles', repeated: 2 }],
  splitArtists: [{ artists: [artist('b1', 'Beyoncé'), artist('b2', 'Beyonce')] }],
}

describe('HealthSection, music', () => {
  it('lists music’s problems, counts albums as titles, and merges keeping the one picked', async () => {
    const merged: unknown[] = []
    mockApi({
      'GET /library-health': health,
      'GET /library-health/check': { running: false, checked: 0, total: 0, finishedAt: null, problems: [] },
      'POST /library-health/merge-artists': (init?: RequestInit) => void merged.push(JSON.parse(String(init?.body))),
    })
    render(
      <MemoryRouter>
        <HealthSection />
      </MemoryRouter>,
    )

    expect(await screen.findByText('2 of 4 movies, episodes and albums have no problems.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /Albums without a cover/ })).toBeInTheDocument()
    expect(screen.getByText(/2 copies: Lemonade, Lemonade \(Deluxe\)/)).toBeInTheDocument()
    expect(screen.getByText(/2 tracks twice/)).toBeInTheDocument()

    const split = screen.getByRole('heading', { name: /Artists under two names/ }).closest('section')!
    fireEvent.click(within(split).getByRole('button', { name: 'Merge' }))
    const dialog = await screen.findByRole('alertdialog')
    fireEvent.click(within(dialog).getByRole('radio', { name: 'Beyonce' }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Merge' }))

    await waitFor(() => expect(merged).toEqual([{ keep: 'b2', merge: ['b1'] }]))
  })
})
