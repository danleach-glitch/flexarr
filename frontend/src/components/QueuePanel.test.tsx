import { render } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { summary } from '../test/mockApi'
import { QueuePanel } from './QueuePanel'

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
    for (const cover of covers) expect(cover).toHaveAttribute('loading', 'lazy')
  })
})
