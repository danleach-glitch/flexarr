import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { mockApi, summary } from '../test/mockApi'
import { NowPlayingView } from './NowPlayingView'

afterEach(() => vi.unstubAllGlobals())

const track = summary({ id: 'cp', kind: 'Track', title: 'Common People', parentId: 'dc', seriesTitle: 'Pulp', seriesId: 'pulp', durationSeconds: 300 })

describe('NowPlayingView', () => {
  it('keeps Tab inside the view, and closes on Escape', () => {
    mockApi({})
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
      play: vi.fn(),
      toggle: vi.fn(),
      seek: vi.fn(),
      next: vi.fn(),
      previous: vi.fn(),
      skipBy: vi.fn(),
    } as unknown as AudioPlayer
    const onClose = vi.fn()
    render(
      <MemoryRouter>
        <button>On the page</button>
        <AudioPlayerContext value={player}>
          <NowPlayingView onClose={onClose} />
        </AudioPlayerContext>
      </MemoryRouter>,
    )
    const view = screen.getByRole('dialog', { name: 'Now playing' })
    const close = screen.getByRole('button', { name: 'Close Now Playing' })
    expect(close).toHaveFocus()

    // Shift-Tab from the first control goes round to the view's last, not out to the page.
    fireEvent.keyDown(close, { key: 'Tab', shiftKey: true })
    expect(view).toContainElement(document.activeElement as HTMLElement)
    expect(document.activeElement).not.toBe(close)
    fireEvent.keyDown(document.activeElement!, { key: 'Tab' })
    expect(close).toHaveFocus()

    fireEvent.keyDown(close, { key: 'Escape' })
    expect(onClose).toHaveBeenCalled()
  })
})
