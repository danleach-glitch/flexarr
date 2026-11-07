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
  const sessionId = (overrides.sessionId as string | undefined) ?? 's1'
  return {
    sessionId,
    method: 'Transcode',
    url: `/api/v1/playback/${sessionId}/master.m3u8`,
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

// Last: a copied stream's failure is remembered for the rest of the visit.
describe('Player with a copied picture', () => {
  it('recovers as before, without blaming copying, when the copied stream is gone from the server', async () => {
    localStorage.clear()
    const asked: { fmp4?: boolean; replacesSessionId?: string }[] = []
    mockApi({
      'POST /playback': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as { profile: { fmp4?: boolean }; replacesSessionId?: string }
        asked.push({ fmp4: body.profile.fmp4, replacesSessionId: body.replacesSessionId })
        return playback({ sessionId: asked.length === 1 ? 'copied' : 'again', transcodeSummary: 'Original H.264, repackaged' })
      },
      // No 'GET /playback/copied/main.m3u8': the server restarted and forgot it (404). Safari calls that unplayable.
    })
    const page = renderPlayer()
    await waitFor(() => expect(asked).toHaveLength(1))
    const video = page.container.querySelector('video')!
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/copied/'))

    Object.defineProperty(video, 'error', { configurable: true, get: () => ({ code: 4 }) })
    fireEvent.error(video)
    await waitFor(() => expect(asked).toHaveLength(2))
    expect(asked[1]).toEqual({ fmp4: true, replacesSessionId: 'copied' })
    expect(localStorage.getItem('flexarr.copiedVideoFailed')).toBeNull()
    await act(async () => page.unmount())
  })

  it('asks again the old way, from where it got to, when the browser can’t play the copied stream', async () => {
    localStorage.clear()
    const asked: { fmp4?: boolean; replacesSessionId?: string; startPositionSeconds?: number }[] = []
    mockApi({
      'POST /playback': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as { profile: { fmp4?: boolean }; replacesSessionId?: string; startPositionSeconds?: number }
        asked.push({ fmp4: body.profile.fmp4, replacesSessionId: body.replacesSessionId, startPositionSeconds: body.startPositionSeconds })
        return body.profile.fmp4
          ? playback({ sessionId: 'copied', transcodeSummary: 'Original H.264, repackaged' })
          : playback({ sessionId: 'converted', transcodeSummary: '720p H.264 at 3 Mbps' })
      },
      'DELETE /playback/copied': undefined,
      // Still served: the browser's the trouble, not the stream.
      'GET /playback/copied/main.m3u8': () => new Response('#EXTM3U\n'),
    })
    const page = renderPlayer()
    await waitFor(() => expect(asked).toHaveLength(1))
    expect(asked[0].fmp4).toBe(true)
    const video = page.container.querySelector('video')!
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/copied/'))

    Object.defineProperty(video, 'currentTime', { configurable: true, get: () => 42, set: () => undefined })
    Object.defineProperty(video, 'error', { configurable: true, get: () => ({ code: 3 }) })
    fireEvent.error(video)
    await waitFor(() => expect(asked).toHaveLength(2))
    expect(asked[1]).toEqual({ fmp4: undefined, replacesSessionId: 'copied', startPositionSeconds: 42 })
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/converted/'))
    expect(localStorage.getItem('flexarr.copiedVideoFailed')).not.toBeNull()

    // The converted one failing isn't the copying's: it recovers as before rather than asking the old way again.
    fireEvent.error(video)
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(asked.slice(2).every((a) => a.fmp4 === undefined)).toBe(true)
    await act(async () => page.unmount())
  })
})
