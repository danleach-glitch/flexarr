import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { NowPlaying } from './NowPlaying'

afterEach(() => vi.unstubAllGlobals())

const track = summary({ id: 'cp', kind: 'Track', title: 'Common People', parentId: 'dc', seriesTitle: 'Pulp', seriesId: 'pulp', durationSeconds: 300 })

/** The bar with something playing, and Now Playing opened and closed as the real player does. */
function Bar() {
  const [open, setOpen] = useState(false)
  const player = {
    current: track,
    queue: [track],
    index: 0,
    playing: true,
    duration: 300,
    speed: 1,
    skipSteps: { back: 15, forward: 30 },
    book: null,
    analyser: null,
    nowPlayingOpen: open,
    setNowPlayingOpen: setOpen,
    play: vi.fn(),
    toggle: vi.fn(),
    seek: vi.fn(),
    next: vi.fn(),
    previous: vi.fn(),
    skipBy: vi.fn(),
    stop: vi.fn(),
  } as unknown as AudioPlayer
  return (
    <MemoryRouter>
      <AudioPlayerContext value={player}>
        <NowPlaying />
      </AudioPlayerContext>
    </MemoryRouter>
  )
}

describe('NowPlaying', () => {
  it('opens Now Playing over the window from the cover, with the bar still there, and Escape puts it away', async () => {
    mockApi({})
    render(<Bar />)
    const bar = screen.getByRole('complementary', { name: 'Now playing' })

    fireEvent.click(screen.getByRole('button', { name: 'Open Now Playing' }))

    // The view's code comes on demand: the bar stays put while it does.
    expect(bar).toBeVisible()
    const close = await screen.findByRole('button', { name: 'Close Now Playing' })
    expect(screen.getByRole('dialog', { name: 'Now playing' })).toBeInTheDocument()
    await waitFor(() => expect(close).toHaveFocus())
    expect(bar).toBeVisible()

    fireEvent.keyDown(close, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Open Now Playing' })).toBeInTheDocument()
  })
})
