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
  unfollowArtist: async (body: unknown) => {
    sent.push({ unfollow: body })
  },
  unrequestAlbum: async (body: unknown) => {
    sent.push({ unrequest: body })
    if (refusal) throw refusal
  },
  getMusicRequestStatus: async () => status,
}))
let status = { configured: true, reachable: true }

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
    status = { configured: true, reachable: true }
  })

  it('requests just the album, then says it is requested', async () => {
    renderCard(album)

    expect(screen.getByRole('link').getAttribute('href')).toBe('/discover/music/album/730000661')
    expect(screen.getByText('Wet Leg, 2025')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Request album moisturizer' }))

    await waitFor(() => expect(screen.getByText('Requested')).toBeTruthy())
    expect(sent).toEqual([{ kind: 'album', deezerId: 730000661 }])
  })

  it('follows an artist, then says Following', async () => {
    renderCard(artist)

    expect(screen.getByRole('link').querySelector('.artwork-round')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Follow Wet Leg' }))

    await waitFor(() => expect(screen.getByText('Following')).toBeTruthy())
    expect(sent).toEqual([{ kind: 'artist', deezerId: 134334152 }])
  })

  it('says Following for an artist in the library Lidarr follows, and Follow for one it doesn’t', () => {
    const { unmount } = renderCard({ ...artist, availability: 'Available', libraryItemId: 'ar1', followed: true })
    expect(screen.getByRole('link').getAttribute('href')).toBe('/item/ar1')
    expect(screen.getByRole('button', { name: 'Following Wet Leg' })).toBeTruthy()
    unmount()

    renderCard({ ...artist, availability: 'Available', libraryItemId: 'ar1', followed: false })
    expect(screen.getByRole('button', { name: 'Follow Wet Leg' })).toBeTruthy()
  })

  it('offers nothing for a library artist on a server that doesn’t say who is followed', () => {
    renderCard({ ...artist, availability: 'Available', libraryItemId: 'ar1' })
    expect(screen.queryByRole('button')).toBeNull()
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
    fireEvent.click(screen.getByRole('button', { name: 'Request album moisturizer' }))

    await waitFor(() => expect(sent).toEqual([{ kind: 'album', musicBrainzId: 'rg-1' }]))
  })

  it('offers Try again, saying why, when Lidarr doesn’t answer', async () => {
    refusal = new ApiError(502, 'Bad Gateway', ['Lidarr isn’t answering. Try again later.'])
    renderCard(album)

    fireEvent.click(screen.getByRole('button', { name: 'Request album moisturizer' }))

    await waitFor(() => expect(screen.getByText('Try again')).toBeTruthy())
    expect(screen.getByRole('alert').textContent).toBe('Lidarr isn’t answering. Try again later.')
  })

  it('says Can’t request, and why, when Lidarr can’t find it', async () => {
    refusal = new ApiError(409, 'Conflict', ['Lidarr can’t find Nowhere by Nobody.'])
    renderCard(album)

    fireEvent.click(screen.getByRole('button', { name: 'Request album moisturizer' }))

    await waitFor(() => expect(screen.getByText('Can’t request')).toBeTruthy())
    expect(screen.queryByRole('button')).toBeNull()
    expect(screen.getByRole('alert').textContent).toBe('Lidarr can’t find Nowhere by Nobody.')
  })

  it('asks before taking a request back, then offers it again', async () => {
    renderCard({ ...album, availability: 'Requested' })

    fireEvent.click(await screen.findByRole('button', { name: 'Requested moisturizer' }))
    expect(screen.getByRole('alertdialog').textContent).toContain('Unrequest moisturizer?')
    fireEvent.click(screen.getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Request album moisturizer' })).toBeTruthy())
    expect(sent).toEqual([{ unrequest: { deezerId: 730000661 } }])
  })

  it('stops following after asking, and Keep following leaves it be', async () => {
    renderCard({ ...artist, availability: 'Requested' })

    fireEvent.click(await screen.findByRole('button', { name: 'Following Wet Leg' }))
    fireEvent.click(screen.getByRole('button', { name: 'Keep following' }))
    expect(sent).toEqual([])
    fireEvent.click(screen.getByRole('button', { name: 'Following Wet Leg' }))
    expect(screen.getByRole('alertdialog').textContent).toContain('Stop following Wet Leg?')
    fireEvent.click(screen.getByRole('button', { name: 'Stop following' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Follow Wet Leg' })).toBeTruthy())
    expect(sent).toEqual([{ unfollow: { deezerId: 134334152 } }])
  })

  it('offers nothing without Lidarr, and greys the button while it isn’t answering', async () => {
    status = { configured: false, reachable: false }
    const { unmount } = renderCard(album)
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Request album moisturizer' })).toBeNull())
    unmount()

    status = { configured: true, reachable: false }
    renderCard(album)
    await waitFor(() => expect((screen.getByRole('button', { name: 'Request album moisturizer' }) as HTMLButtonElement).disabled).toBe(true))
  })

  it('says to update the server when it has no undo yet', async () => {
    refusal = new ApiError(404, 'Not Found', [])
    renderCard({ ...album, availability: 'Requested' })

    fireEvent.click(await screen.findByRole('button', { name: 'Requested moisturizer' }))
    fireEvent.click(screen.getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.getByRole('alert').textContent).toBe('Update the server to undo this.'))
  })

  it('says the server’s own words for a 404 that carries them', async () => {
    refusal = new ApiError(404, 'Not Found', ['Deezer doesn’t know that any more.'], undefined, { detail: 'Deezer doesn’t know that any more.' })
    renderCard({ ...album, availability: 'Requested' })

    fireEvent.click(await screen.findByRole('button', { name: 'Requested moisturizer' }))
    fireEvent.click(screen.getByRole('button', { name: 'Unrequest' }))

    await waitFor(() => expect(screen.getByRole('alert').textContent).toBe('Deezer doesn’t know that any more.'))
  })
})
