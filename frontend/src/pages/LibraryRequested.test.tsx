import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import type { Library, MusicRequested } from '../api/client'
import { requestedState } from '../lib/requestedState'
import { LibraryRequested } from './LibraryRequested'

const unrequested: unknown[] = []
const albums: MusicRequested[] = [
  { musicBrainzId: 'rg-1', title: 'moisturizer', artist: 'Wet Leg', artistMusicBrainzId: 'a1', image: null, releaseDate: '2025-07-11', state: 'downloading', progress: 0.45, artistLibraryItemId: 'artist-1' },
  { musicBrainzId: 'rg-2', title: 'Next One', artist: 'Wet Leg', artistMusicBrainzId: 'a1', image: null, releaseDate: '2099-11-14', state: 'upcoming', progress: null, artistLibraryItemId: null },
]
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getMusicRequested: async () => albums,
  unrequestAlbum: async (body: unknown) => {
    unrequested.push(body)
  },
}))

const library = { id: 'lib', name: 'Music', type: 'Music' } as Library

describe('LibraryRequested', () => {
  it('says where each album stands', () => {
    expect(requestedState(albums[0])).toBe('Downloading 45%')
    expect(requestedState({ ...albums[0], state: 'importing' })).toBe('Importing')
    expect(requestedState({ ...albums[0], state: 'searching' })).toBe('Searching')
    expect(requestedState(albums[1], new Date('2099-01-01'))).toMatch(/^Out 14 Nov$|^Out Nov 14$/)
  })

  it('lists what Lidarr awaits, and unrequests one after asking', async () => {
    render(
      <MemoryRouter>
        <LibraryRequested library={library} viewSwitch={null} />
      </MemoryRouter>,
    )

    expect((await screen.findAllByText('moisturizer')).length).toBeGreaterThan(0)
    expect(screen.getByText('Downloading 45%')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Wet Leg' }).getAttribute('href')).toBe('/item/artist-1')
    fireEvent.click(screen.getAllByRole('button', { name: 'Unrequest' })[0])
    fireEvent.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.queryAllByText('moisturizer')).toHaveLength(0))
    expect(unrequested).toEqual([{ musicBrainzId: 'rg-1' }])
    expect(screen.getAllByText('Next One').length).toBeGreaterThan(0)
  })
})
