import { Profiler } from 'react'
import { act, fireEvent, render, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import LivePlayerPage from './LivePlayerPage'

beforeAll(() => {
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined)
  window.HTMLMediaElement.prototype.pause = vi.fn()
})

afterEach(() => vi.unstubAllGlobals())

describe('Live player', () => {
  it('lets a channel still starting go when the page is left', async () => {
    let answer: (value: unknown) => void = () => undefined
    const stopped: string[] = []
    mockApi({
      '/livetv/channels': [],
      'POST /livetv/channels/c1/watch': () => new Promise((resolve) => (answer = resolve)),
      'DELETE /livetv/watch/v1/': (_: RequestInit | undefined, url: URL) => {
        stopped.push(url.pathname)
        return undefined
      },
    })
    const page = render(
      <MemoryRouter initialEntries={['/live/c1']}>
        <Routes>
          <Route path="live/:id" element={<LivePlayerPage />} />
        </Routes>
      </MemoryRouter>,
    )
    // Back, before the server has answered.
    page.unmount()
    await act(async () => {
      answer({ viewer: 'v1', playlist: '/live.m3u8', channel: { id: 'c1', name: 'One', number: 1 } })
      await new Promise((resolve) => setTimeout(resolve, 10))
    })
    expect(stopped).toEqual(['/api/v1/livetv/watch/v1/'])
  })

  it('draws the page again as the picture plays only when a second passes, keeping the channel list', async () => {
    mockApi({
      '/livetv/channels': [
        { id: 'c1', name: 'One', number: 1, isFavourite: false, now: null },
        { id: 'c2', name: 'Two', number: 2, isFavourite: true, now: null },
      ],
      'POST /livetv/channels/c1/watch': { viewer: 'v1', playlist: '/live.m3u8', method: 'Original', channel: { id: 'c1', name: 'One', number: 1 } },
      'POST /livetv/watch/v1/heartbeat': undefined,
    })
    let commits = 0
    const page = render(
      <Profiler id="live" onRender={() => commits++}>
        <MemoryRouter initialEntries={['/live/c1']}>
          <Routes>
            <Route path="live/:id" element={<LivePlayerPage />} />
          </Routes>
        </MemoryRouter>
      </Profiler>,
    )
    const video = page.container.querySelector('video')!
    let time = 100
    Object.defineProperty(video, 'seekable', { value: { length: 1, start: () => 40.2, end: () => 130.7 } })
    Object.defineProperty(video, 'currentTime', { get: () => time, set: (v: number) => (time = v) })
    fireEvent.click(page.container.querySelector('.player-quality')!)
    await waitFor(() => expect(page.container.querySelectorAll('.live-channel-row')).toHaveLength(2))
    expect(Array.from(page.container.querySelectorAll('.live-channel-row strong'), (row) => row.textContent)).toEqual(['Two', 'One'])

    fireEvent.timeUpdate(video)
    const settled = commits
    // Reports a tenth of a second apart, within one second: nothing on screen moves.
    for (let i = 0; i < 3; i++) {
      time += 0.1
      fireEvent.timeUpdate(video)
      fireEvent.progress(video)
    }
    expect(commits).toBe(settled)
    const before = commits
    time += 1
    fireEvent.timeUpdate(video)
    expect(commits).toBe(before + 1)
    expect(page.container.querySelector('.scrubber')).toHaveAttribute('aria-valuenow', '60')
  })
})
