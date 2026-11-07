import { act, fireEvent, render, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { mockApi, summary } from '../test/mockApi'
import { PlayerPage } from './PlayerPage'

beforeAll(() => {
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined)
  window.HTMLMediaElement.prototype.pause = vi.fn()
  window.HTMLMediaElement.prototype.load = vi.fn()
})

afterEach(() => vi.unstubAllGlobals())

function playback(overrides: Record<string, unknown> = {}) {
  return {
    sessionId: 's1',
    method: 'Transcode',
    url: '/api/v1/playback/s1/master.m3u8',
    reasons: [],
    durationSeconds: 600,
    startPositionSeconds: 0,
    fileId: 'f1',
    audioTracks: [
      { index: 1, label: 'English', codec: 'aac', language: 'eng', channels: 2, isDefault: true },
      { index: 2, label: 'French', codec: 'aac', language: 'fre', channels: 2, isDefault: false },
    ],
    selectedAudioIndex: 1,
    subtitles: [],
    selectedSubtitleKey: null,
    subtitleBurnedIn: false,
    markers: [],
    item: summary({ id: 'm1', kind: 'Movie', title: 'Film' }),
    ancestors: [],
    next: null,
    transcodeSummary: '1080p H.264 at 10 Mbps',
    ...overrides,
  }
}

function renderPlayer() {
  return render(
    <MemoryRouter initialEntries={['/play/m1']}>
      <Routes>
        <Route path="play/:id" element={<PlayerPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('Player speed', () => {
  it('tells the server a converted film’s speed, and again for each new stream of it', async () => {
    const speeds: number[] = []
    let sessions = 0
    mockApi({
      'POST /playback': () => playback({ sessionId: `s${++sessions}` }),
      'PUT /items/m1/playback-speed': (init?: RequestInit) => {
        speeds.push((JSON.parse(String(init?.body)) as { speed: number }).speed)
        return undefined
      },
    })
    const page = renderPlayer()
    const speedButton = await waitFor(() => page.getByRole('button', { name: /^Speed/ }))
    await waitFor(() => expect(sessions).toBe(1))
    expect(speeds).toEqual([])

    fireEvent.click(speedButton)
    fireEvent.click(page.getByRole('menuitemradio', { name: '1.5×' }))
    await waitFor(() => expect(speeds).toEqual([1.5]))

    // Another audio track is a new stream, which starts at normal pace on the server until told.
    fireEvent.click(page.getByRole('button', { name: 'Audio' }))
    fireEvent.click(page.getByRole('menuitemradio', { name: /French/ }))
    await waitFor(() => expect(sessions).toBe(2))
    await waitFor(() => expect(speeds).toEqual([1.5, 1.5]))
    await act(async () => page.unmount())
  })

  it('says nothing for a film played as it is', async () => {
    const speeds: number[] = []
    mockApi({
      'POST /playback': () => playback({ method: 'DirectPlay', url: '/api/v1/playback/s1/stream', transcodeSummary: null }),
      'PUT /items/m1/playback-speed': () => {
        speeds.push(1)
        return undefined
      },
    })
    const page = renderPlayer()
    fireEvent.click(await waitFor(() => page.getByRole('button', { name: /^Speed/ })))
    fireEvent.click(page.getByRole('menuitemradio', { name: '2×' }))
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(speeds).toEqual([])
    await act(async () => page.unmount())
  })
})
