import { fireEvent, render } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { summary } from '../test/mockApi'
import { QueuePanel } from './QueuePanel'

// Each row asks for its cover's address as it's drawn: counting the asks counts the rows drawn.
const imageUrl = vi.hoisted(() => vi.fn((path: string) => path))
vi.mock('../api/client', async (load) => ({ ...(await load<typeof import('../api/client')>()), imageUrl }))
/** How many upcoming tracks' rows were drawn (the first track is the one playing, at the top). */
const upcomingDrawn = () => imageUrl.mock.calls.filter(([path]) => path !== '/art/0.jpg').length

const tracks = Array.from({ length: 40 }, (_, i) =>
  summary({ id: `t${i}`, kind: 'Track', title: `Track ${i + 1}`, seriesTitle: 'Pulp', artwork: { poster: `/art/${i}.jpg`, backdrop: null, logo: null, thumb: null } }),
)

describe('the queue', () => {
  it('asks for an upcoming track’s cover only as it nears the screen', () => {
    const player = { current: tracks[0], queue: tracks, index: 0, playing: true, moveInQueue: vi.fn() } as unknown as AudioPlayer
    const { container } = render(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    const covers = container.querySelectorAll('.queue-list img')
    expect(covers).toHaveLength(39)
    for (const cover of covers) {
      expect(cover).toHaveAttribute('loading', 'lazy')
      expect(cover).toHaveAttribute('decoding', 'async')
    }
  })

  it('drags a track to a new place, redrawing only the rows the drag passes over', () => {
    const player = { current: tracks[0], queue: tracks, index: 0, playing: true, moveInQueue: vi.fn() } as unknown as AudioPlayer
    const { container } = render(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    const rows = container.querySelectorAll('.queue-list > li')
    const dataTransfer = { setData() {}, effectAllowed: '', dropEffect: '' }
    imageUrl.mockClear()

    fireEvent.dragStart(rows[1], { dataTransfer })
    for (const at of [2, 3, 4, 5]) fireEvent.dragOver(rows[at], { dataTransfer })
    expect(rows[1]).toHaveClass('dragging')
    expect(rows[5]).toHaveClass('drag-over')
    expect(rows[4]).not.toHaveClass('drag-over')
    // The dragged row, then each row as the drag reaches it and leaves it: not all 39 at every step.
    expect(upcomingDrawn()).toBe(1 + 1 + 2 + 2 + 2)

    fireEvent.drop(rows[5], { dataTransfer })
    expect(player.moveInQueue).toHaveBeenCalledWith(2, 6)
    expect(rows[1]).not.toHaveClass('dragging')
    expect(container.querySelector('.drag-over')).toBeNull()
  })

  it('redraws none of the rows after a track that goes, and hands focus to the next one', () => {
    const player = { current: tracks[0], queue: tracks, index: 0, playing: true, removeFromQueue: vi.fn() } as unknown as AudioPlayer
    const { container, rerender, getByRole } = render(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    const third = getByRole('button', { name: 'Play Track 3 now' })
    fireEvent.click(getByRole('button', { name: 'Remove Track 2 from the queue' }))
    expect(player.removeFromQueue).toHaveBeenCalledWith(1)
    imageUrl.mockClear()
    rerender(
      <AudioPlayerContext value={{ ...player, queue: tracks.filter((_, i) => i !== 1) }}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    // The same rows, kept: only Track 3, now first (its Move up goes), is drawn again, not the 37 after it.
    expect(upcomingDrawn()).toBe(1)
    expect(container.querySelectorAll('.queue-list > li')).toHaveLength(38)
    expect(getByRole('button', { name: 'Play Track 3 now' })).toBe(third)
    expect(document.activeElement).toBe(third)
  })

  it('hands focus to Close when the last track goes, and undoes a Clear', () => {
    const queue = tracks.slice(0, 2)
    const player = { current: tracks[0], queue, index: 0, playing: true, removeFromQueue: vi.fn(), clearQueue: vi.fn(), playNext: vi.fn() } as unknown as AudioPlayer
    const { rerender, getByRole } = render(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    const clear = getByRole('button', { name: 'Clear' })
    clear.focus()
    fireEvent.click(clear)
    expect(player.clearQueue).toHaveBeenCalled()
    const empty = { ...player, queue: [tracks[0]] }
    rerender(
      <AudioPlayerContext value={empty}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    // The same button, now Undo, keeps focus.
    expect(clear).toHaveTextContent('Undo')
    expect(clear).toBeEnabled()
    expect(document.activeElement).toBe(clear)
    fireEvent.click(clear)
    expect(player.playNext).toHaveBeenCalledWith([tracks[1]])
    rerender(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    expect(clear).toHaveTextContent('Clear')

    fireEvent.click(getByRole('button', { name: 'Remove Track 2 from the queue' }))
    rerender(
      <AudioPlayerContext value={empty}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    expect(document.activeElement).toBe(getByRole('button', { name: 'Close queue' }))
  })

  it('redraws none of the upcoming tracks when the player pauses', () => {
    const player = { current: tracks[0], queue: tracks, index: 0, playing: true, moveInQueue: vi.fn() } as unknown as AudioPlayer
    const { rerender } = render(
      <AudioPlayerContext value={player}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    imageUrl.mockClear()
    rerender(
      <AudioPlayerContext value={{ ...player, playing: false }}>
        <QueuePanel onClose={() => {}} />
      </AudioPlayerContext>,
    )
    expect(upcomingDrawn()).toBe(0)
  })
})

