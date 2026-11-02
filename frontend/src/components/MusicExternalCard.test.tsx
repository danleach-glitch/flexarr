import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, type MusicExternal } from '../api/client'
import { MusicExternalCard } from './MusicExternalCard'

// A plain function rather than vi.fn: vitest's mock leaves a rejected call's promise unhandled, which fails the test.
const sent: unknown[] = []
let refusal: Error | undefined
vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  requestMusic: async (body: unknown) => {
    sent.push(body)
    if (refusal) throw refusal
  },
}))

const album: MusicExternal = {
  kind: 'album',
  deezerId: 730000661,
  title: 'moisturizer',
  artist: 'Wet Leg',
  artistDeezerId: 134334152,
  year: 2025,
  image: '/api/v1/images/remote?url=x&s=y',
  availability: 'None',
  libraryItemId: null,
  recordType: 'album',
}
const artist: MusicExternal = { ...album, kind: 'artist', deezerId: 134334152, title: 'Wet Leg', artist: null, year: null }

function renderCard(item: MusicExternal) {
  return render(
    <MemoryRouter>
      <MusicExternalCard item={item} />
    </MemoryRouter>,
  )
}

describe('MusicExternalCard', () => {
  beforeEach(() => {
    sent.length = 0
    refusal = undefined
  })

  it('requests just the album, then says it is requested', async () => {
    renderCard(album)

    expect(screen.getByRole('link').getAttribute('href')).toBe('/discover/music/album/730000661')
    expect(screen.getByText('Wet Leg, 2025')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Request album' }))

    await waitFor(() => expect(screen.getByText('Requested')).toBeTruthy())
    expect(sent).toEqual([{ kind: 'album', deezerId: 730000661 }])
  })

  it('follows an artist, then says Following', async () => {
    renderCard(artist)

    expect(screen.getByRole('link').querySelector('.artwork-round')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Follow artist' }))

    await waitFor(() => expect(screen.getByText('Following')).toBeTruthy())
    expect(sent).toEqual([{ kind: 'artist', deezerId: 134334152 }])
  })

  it('opens an album you have in the library, with nothing to request', () => {
    renderCard({ ...album, availability: 'Available', libraryItemId: 'a1' })

    expect(screen.getByRole('link').getAttribute('href')).toBe('/item/a1')
    expect(screen.getByText('In your library')).toBeTruthy()
    expect(screen.queryByRole('button')).toBeNull()
  })

  it('requests an album only Lidarr lists by its MusicBrainz id, with no page to open', async () => {
    renderCard({ ...album, deezerId: 0, musicBrainzId: 'rg-1', image: null })

    expect(screen.queryByRole('link')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Request album' }))

    await waitFor(() => expect(sent).toEqual([{ kind: 'album', musicBrainzId: 'rg-1' }]))
  })

  it('offers Try again with the server’s reason when Lidarr refuses', async () => {
    refusal = new ApiError(502, 'Bad Gateway', ['Lidarr didn’t take the request: down.'])
    renderCard(album)

    fireEvent.click(screen.getByRole('button', { name: 'Request album' }))

    await waitFor(() => expect(screen.getByText('Try again')).toBeTruthy())
    expect(screen.getByRole('button').getAttribute('title')).toBe('Lidarr didn’t take the request: down.')
  })
})
