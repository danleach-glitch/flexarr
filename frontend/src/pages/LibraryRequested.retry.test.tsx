import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache, type Library, type MusicRequested } from '../api/client'
import { mockApi } from '../test/mockApi'
import { LibraryRequested } from './LibraryRequested'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  clearApiCache()
})

const library = { id: 'lib', name: 'Music', type: 'Music' } as Library
const album = (over: Partial<MusicRequested> = {}): MusicRequested => ({
  musicBrainzId: 'rg',
  title: 'City Lounge',
  artist: 'Various',
  artistMusicBrainzId: null,
  image: null,
  releaseDate: null,
  state: 'couldNotImport',
  progress: null,
  artistLibraryItemId: null,
  canRetry: true,
  ...over,
})
const open = () =>
  render(
    <MemoryRouter>
      <LibraryRequested library={library} viewSwitch={null} />
    </MemoryRouter>,
  )
const problem = (status: number, title: string) => new Response(JSON.stringify({ title }), { status })
const named = { name: 'Try again: City Lounge' }

describe('Try again, against the server', () => {
  it.each([
    ['says no', { canRetry: false }],
    ['is too old to say', { canRetry: undefined }],
  ])('isn’t offered when the server %s, and Why? still is', async (_, over) => {
    mockApi({ '/requests/music/requested': [album(over)], '/requests/music/status': { configured: true, reachable: true } })
    open()

    expect(await screen.findByRole('link', { name: /Why couldn’t/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', named)).toBeNull()
    expect(screen.getByRole('button', { name: 'Unrequest City Lounge' })).toBeInTheDocument()
  })

  it.each([
    [409, 'That album isn’t stuck.'],
    [429, 'You’ve asked for a lot today.'],
  ])('shows a %i in the server’s words, on the card, with the button still there', async (status, words) => {
    mockApi({
      '/requests/music/requested': [album()],
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': () => problem(status, words),
    })
    open()

    fireEvent.click(await screen.findByRole('button', named))

    expect(await screen.findByRole('alert')).toHaveTextContent(words)
    expect(screen.getByRole('button', named)).toBeEnabled()
  })

  it('keeps asking for a few minutes after, though nothing is moving yet, so the download shows', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    let state: MusicRequested['state'] = 'couldNotImport'
    let asked = 0
    mockApi({
      '/requests/music/requested': () => {
        asked++
        return [album({ state })]
      },
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': () => {
        state = 'searching'
      },
    })
    open()
    fireEvent.click(await screen.findByRole('button', named))
    expect(await screen.findByText('Searching')).toBeInTheDocument()

    // Searching on its own wouldn't be asked about again.
    state = 'downloading'
    await act(() => vi.advanceTimersByTimeAsync(30_000))
    await waitFor(() => expect(screen.getByText(/^Downloading/)).toBeInTheDocument())

    state = 'searching'
    await act(() => vi.advanceTimersByTimeAsync(5 * 60_000))
    const settled = asked
    await act(() => vi.advanceTimersByTimeAsync(2 * 60_000))
    expect(asked).toBe(settled)
  })

  it('takes its failure away once the album isn’t stuck', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    let state: MusicRequested['state'] = 'couldNotImport'
    mockApi({
      '/requests/music/requested': () => [album({ state })],
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': () => problem(409, 'Not stuck'),
    })
    open()
    fireEvent.click(await screen.findByRole('button', named))
    await screen.findByRole('alert')

    state = 'downloading'
    await act(() => vi.advanceTimersByTimeAsync(30_000))
    await waitFor(() => expect(screen.getByText(/^Downloading/)).toBeInTheDocument())
    expect(screen.queryByRole('alert')).toBeNull()

    // Stuck once more, it starts clean.
    state = 'couldNotImport'
    await act(() => vi.advanceTimersByTimeAsync(30_000))
    await screen.findByRole('button', named)
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('sends this album, and moves the keyboard to Unrequest when the button goes', async () => {
    const api = mockApi({
      '/requests/music/requested': [album()],
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': undefined,
    })
    open()
    const button = await screen.findByRole('button', named)
    button.focus()

    fireEvent.click(button)

    await waitFor(() => expect(screen.queryByRole('button', named)).toBeNull())
    expect(screen.getByRole('button', { name: 'Unrequest City Lounge' })).toHaveFocus()
    const post = api.mock.calls.find(([input, init]) => String(input).endsWith('/requests/music/retry') && init?.method === 'POST')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ musicBrainzId: 'rg' })
  })

  it('leaves the keyboard alone when it was somewhere else', async () => {
    mockApi({
      '/requests/music/requested': [album()],
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': undefined,
    })
    open()

    fireEvent.click(await screen.findByRole('button', named))

    await waitFor(() => expect(screen.queryByRole('button', named)).toBeNull())
    expect(screen.getByRole('button', { name: 'Unrequest City Lounge' })).not.toHaveFocus()
  })

  it('keeps focus on the button through an answer that says no', async () => {
    mockApi({
      '/requests/music/requested': [album()],
      '/requests/music/status': { configured: true, reachable: true },
      'POST /requests/music/retry': () => problem(409, 'Not stuck'),
    })
    open()
    const button = await screen.findByRole('button', named)
    button.focus()

    fireEvent.click(button)

    await screen.findByRole('alert')
    expect(button).toHaveFocus()
  })
})
