import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { AddToPlaylistDialog, AddToPlaylistHost, openAddToPlaylist } from './AddToPlaylist'

afterEach(() => vi.unstubAllGlobals())

const playlist = (id: string, name: string, trackCount = 3) => ({
  id,
  name,
  trackCount,
  durationSeconds: trackCount * 200,
  covers: [],
  cover: null,
  createdAt: '2026-10-25T10:00:00Z',
  updatedAt: '2026-10-25T10:00:00Z',
})

describe('Add to playlist', () => {
  it('adds the track to the playlist chosen, skipping it if it is already there, and says so', async () => {
    const sent: { url: string; body: unknown }[] = []
    let already = false
    mockApi({
      '/playlists': [playlist('p1', 'Road trip'), playlist('p2', 'Sunday morning', 1)],
      'POST /playlists/p1/items': (init?: RequestInit, url?: URL) => {
        sent.push({ url: String(url?.pathname), body: JSON.parse(String(init?.body)) })
        return { added: already ? 0 : 1, summary: playlist('p1', 'Road trip', 4) }
      },
    })
    render(<AddToPlaylistDialog item={{ id: 't1', title: 'Tender' }} onClose={() => undefined} />)

    expect(screen.getByRole('dialog', { name: 'Add Tender to a playlist' })).toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: /Road trip/ }))
    expect(await screen.findByRole('status')).toHaveTextContent('Added to Road trip.')
    expect(sent).toEqual([{ url: '/api/v1/playlists/p1/items', body: { itemIds: ['t1'], skipDuplicates: true } }])

    already = true
    fireEvent.click(screen.getByRole('button', { name: /Road trip/ }))
    await vi.waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Already in Road trip.'))
    // "1 track" and "3 tracks": the list describes each playlist.
    expect(screen.getByRole('button', { name: /Sunday morning/ })).toHaveTextContent('1 track')
  })

  it('starts a new playlist with the track from the form, without a browser prompt', async () => {
    let created: unknown
    mockApi({
      '/playlists': [],
      'POST /playlists': (init?: RequestInit) => {
        created = JSON.parse(String(init?.body))
        return playlist('p9', 'Chill', 1)
      },
    })
    render(<AddToPlaylistDialog item={{ id: 'album-1', title: '13' }} onClose={() => undefined} />)

    expect(await screen.findByText('No playlists yet. Start one below.')).toBeInTheDocument()
    const create = screen.getByRole('button', { name: 'Create' })
    expect(create).toBeDisabled()
    fireEvent.change(screen.getByLabelText('New playlist'), { target: { value: ' Chill ' } })
    fireEvent.click(create)

    expect(await screen.findByRole('status')).toHaveTextContent('Made Chill with it.')
    expect(created).toEqual({ name: 'Chill', itemIds: ['album-1'] })
  })

  it('opens from anywhere through the host, and closes on Escape', async () => {
    mockApi({ '/playlists': [playlist('p1', 'Road trip')] })
    render(<AddToPlaylistHost />)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    act(() => openAddToPlaylist({ id: 't1', title: 'Tender' }))
    expect(await screen.findByRole('dialog', { name: 'Add Tender to a playlist' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /Road trip/ })).toBeInTheDocument()

    fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})
