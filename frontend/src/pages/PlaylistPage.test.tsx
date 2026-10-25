import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { PlaylistPage } from './PlaylistsPage'

afterEach(() => vi.unstubAllGlobals())

const track = (id: string, title: string) =>
  summary({ id, kind: 'Track', title, parentId: 'album-1', seriesTitle: 'Blur', seriesId: 'blur', durationSeconds: 200 }) as unknown as ItemSummary
const tracks = [track('t1', 'One'), track('t2', 'Two'), track('t3', 'Three')]
const detail = () => ({
  summary: { id: 'p1', name: 'Road trip', trackCount: 3, durationSeconds: 600, covers: [], cover: null, createdAt: '2026-10-25T10:00:00Z', updatedAt: '2026-10-25T10:00:00Z' },
  tracks: tracks.map((t, i) => ({ entryId: i + 1, track: t })),
})

function renderPage(player: Partial<AudioPlayer> = {}) {
  const full = { play: vi.fn(), playNext: vi.fn(), enqueue: vi.fn(), current: undefined, playing: false, queue: [], index: 0, duration: 0, ...player } as unknown as AudioPlayer
  render(
    <MemoryRouter initialEntries={['/playlist/p1']}>
      <AudioPlayerContext value={full}>
        <Routes>
          <Route path="playlist/:id" element={<PlaylistPage />} />
          <Route path="playlists" element={<p>All playlists</p>} />
        </Routes>
      </AudioPlayerContext>
    </MemoryRouter>,
  )
  return full
}

const titles = () => within(screen.getByRole('list', { name: 'Tracks in Road trip' })).getAllByRole('listitem').map((li) => li.querySelector('.track-title')?.textContent)

describe('PlaylistPage', () => {
  it('shows the playlist and plays it from the start, or from the track pressed', async () => {
    mockApi({ '/playlists/p1': detail() })
    const player = renderPage()

    expect(await screen.findByRole('heading', { level: 1, name: 'Road trip' })).toBeInTheDocument()
    expect(screen.getByText('3 tracks')).toBeInTheDocument()
    expect(screen.getByText('10m')).toBeInTheDocument()
    expect(titles()).toEqual(['One', 'Two', 'Three'])

    fireEvent.click(screen.getByRole('button', { name: 'Play' }))
    expect(player.play).toHaveBeenLastCalledWith(tracks, 0)
    fireEvent.click(screen.getByRole('button', { name: /^2\s*Two/ }))
    expect(player.play).toHaveBeenLastCalledWith(tracks, 1)
  })

  it('moves a track with the arrow buttons, showing the new order at once and telling the server where it went', async () => {
    const moves: { entry: string; body: unknown }[] = []
    mockApi({
      '/playlists/p1': detail(),
      'PUT /playlists/p1/items/2/position': (init?: RequestInit) => {
        moves.push({ entry: '2', body: JSON.parse(String(init?.body)) })
      },
    })
    renderPage()
    await screen.findByRole('heading', { level: 1, name: 'Road trip' })

    fireEvent.click(screen.getByRole('button', { name: 'Move Two down' }))
    expect(titles()).toEqual(['One', 'Three', 'Two'])
    await vi.waitFor(() => expect(moves).toEqual([{ entry: '2', body: { position: 2 } }]))
    // The last track can't go further down, nor the first up.
    expect(screen.getByRole('button', { name: 'Move Two down' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move One up' })).toBeDisabled()
  })

  it('removes a track, and renames and deletes the playlist', async () => {
    const calls: string[] = []
    let name = 'Road trip'
    mockApi({
      '/playlists/p1': () => ({ ...detail(), summary: { ...detail().summary, name } }),
      'DELETE /playlists/p1/items/1': () => {
        calls.push('remove 1')
      },
      'PUT /playlists/p1': (init?: RequestInit) => {
        name = (JSON.parse(String(init?.body)) as { name: string }).name
        calls.push(`rename ${name}`)
        return { ...detail().summary, name }
      },
      'DELETE /playlists/p1': () => {
        calls.push('delete')
      },
    })
    renderPage()
    await screen.findByRole('heading', { level: 1, name: 'Road trip' })

    fireEvent.click(screen.getByRole('button', { name: 'Remove One from the playlist' }))
    expect(titles()).toEqual(['Two', 'Three'])
    await vi.waitFor(() => expect(calls).toContain('remove 1'))

    fireEvent.click(screen.getByRole('button', { name: 'Rename' }))
    const form = screen.getByRole('form', { name: 'Rename playlist' })
    fireEvent.change(within(form).getByLabelText('Playlist name'), { target: { value: 'Long drive' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Long drive' })).toBeInTheDocument()
    expect(calls).toContain('rename Long drive')

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))
    const dialog = screen.getByRole('alertdialog', { name: 'Delete Long drive?' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete playlist' }))
    expect(await screen.findByText('All playlists')).toBeInTheDocument()
    expect(calls).toContain('delete')
  })
})
