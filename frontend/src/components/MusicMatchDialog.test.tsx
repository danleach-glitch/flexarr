import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemDetail, MusicMatch } from '../api/client'
import { mockApi } from '../test/mockApi'
import { MusicMatchById, MusicMatchDialog } from './MusicMatchDialog'

afterEach(() => vi.unstubAllGlobals())

const album = (overrides: Partial<ItemDetail> = {}) =>
  ({
    item: { id: 'a1', kind: 'Album', title: 'Moisturiser', artwork: { poster: '/api/v1/items/a1/image?type=poster' } },
    path: '/music/Wet Leg/Moisturiser',
    ancestors: [{ id: 'ar1', title: 'Wet Leg' }],
    matchLocked: false,
    detailsLocked: false,
    ...overrides,
  }) as unknown as ItemDetail

const result = (overrides: Partial<MusicMatch>): MusicMatch => ({
  provider: 'lidarr',
  id: '9',
  instance: 'Lidarr',
  title: 'moisturizer',
  artist: 'Wet Leg',
  year: 2025,
  tracks: 12,
  type: 'album',
  cover: '/api/v1/images/arr?url=a',
  coverValue: 'arr://Lidarr/mediacover/album/9/cover.jpg',
  current: false,
  inLibrary: null,
  followed: false,
  ...overrides,
})

const results = [
  result({}),
  result({ provider: 'deezer', id: '730000661', instance: null, title: 'moisturizer (Deluxe)', type: 'ep', cover: '/api/v1/images/remote?url=b', coverValue: 'https://cdn-images.dzcdn.net/images/cover/x/1000x1000.jpg' }),
]

describe('MusicMatchDialog', () => {
  it('lists Lidarr’s and Deezer’s albums, and uses the one chosen with another result’s cover', async () => {
    const sent: unknown[] = []
    mockApi({ 'GET /items/a1/music-match': results, 'POST /items/a1/music-match': (init?: RequestInit) => void sent.push(JSON.parse(String(init?.body))) })
    const onFixed = vi.fn()
    render(<MusicMatchDialog detail={album()} onFixed={onFixed} onClose={() => undefined} />)

    expect(await screen.findByText('In Lidarr')).toBeInTheDocument()
    expect(screen.getByText('Deezer')).toBeInTheDocument()
    expect(screen.getByText('EP')).toBeInTheDocument()
    fireEvent.click(screen.getAllByRole('option')[0])
    expect(screen.getByRole('radio', { name: 'moisturizer’s' })).toHaveAttribute('aria-checked', 'true')
    fireEvent.click(screen.getByRole('radio', { name: 'From moisturizer (Deluxe)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Use moisturizer' }))

    await waitFor(() => expect(onFixed).toHaveBeenCalled())
    expect(sent).toEqual([{ provider: 'lidarr', id: '9', instance: 'Lidarr', cover: 'https://cdn-images.dzcdn.net/images/cover/x/1000x1000.jpg' }])
  })

  it('keeps the cover there, warns that edited details go, and can match automatically again', async () => {
    const sent: unknown[] = []
    const reset = vi.fn()
    mockApi({
      'GET /items/a1/music-match': results,
      'POST /items/a1/music-match': (init?: RequestInit) => void sent.push(JSON.parse(String(init?.body))),
      'DELETE /items/a1/music-match': () => void reset(),
    })
    const onFixed = vi.fn()
    const { unmount } = render(<MusicMatchDialog detail={album({ detailsLocked: true })} onFixed={onFixed} onClose={() => undefined} />)

    fireEvent.click((await screen.findAllByRole('option'))[1])
    expect(screen.getByRole('status')).toHaveTextContent('The details you edited are replaced by moisturizer (Deluxe)’s.')
    fireEvent.click(screen.getByRole('radio', { name: 'Keep this one' }))
    fireEvent.click(screen.getByRole('button', { name: 'Use moisturizer (Deluxe)' }))
    await waitFor(() => expect(onFixed).toHaveBeenCalled())
    expect(sent).toEqual([{ provider: 'deezer', id: '730000661', instance: null, cover: 'current' }])
    unmount()

    render(<MusicMatchDialog detail={album({ matchLocked: true })} onFixed={onFixed} onClose={() => undefined} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Match automatically again' }))
    await waitFor(() => expect(reset).toHaveBeenCalled())
  })

  it('shows a dialog while an album from Library health loads, and why when it has gone', async () => {
    let answer: (r: Response) => void = () => undefined
    mockApi({ 'GET /items/gone': () => new Promise<Response>((resolve) => (answer = resolve)) })
    const onClose = vi.fn()
    render(<MusicMatchById id="gone" onFixed={() => undefined} onClose={onClose} />)

    expect(screen.getByRole('dialog', { name: 'Fix match' })).toHaveAttribute('aria-busy', 'true')
    answer(new Response('', { status: 404 }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(onClose).toHaveBeenCalled()
  })
})
