import { act, fireEvent, render, waitFor, within } from '@testing-library/react'
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

describe('Player when the tab closes', () => {
  it('ends the stream, unless the page is kept to come back to', async () => {
    const stopped: string[] = []
    mockApi({
      'POST /playback': () => playback(),
      'DELETE /playback/s1': () => {
        stopped.push('s1')
        return undefined
      },
    })
    const page = renderPlayer()
    await waitFor(() => expect(page.container.querySelector('video')?.getAttribute('src')).toContain('/playback/s1/'))

    window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }))
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(stopped).toEqual([])

    window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false }))
    await waitFor(() => expect(stopped).toEqual(['s1']))
    // Ended once: unmounting afterwards doesn't stop it again.
    await act(async () => page.unmount())
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(stopped).toEqual(['s1'])
  })

  it('calls a copied picture by its summary, not “Converting”', async () => {
    mockApi({ 'POST /playback': () => playback({ transcodeSummary: 'Original H.264, repackaged' }) })
    const page = renderPlayer()
    await waitFor(() => expect(page.getByText('Original H.264, repackaged')).toBeTruthy())
    expect(page.queryByText(/Converting/)).toBeNull()
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

describe('Player Quality menu', () => {
  it('names the choice, offers only caps below the file, and says what plays now and what the file is', async () => {
    const asked: Record<string, unknown>[] = []
    let sessions = 0
    mockApi({
      'POST /playback': (init?: RequestInit) => {
        asked.push(JSON.parse(String(init?.body)) as Record<string, unknown>)
        return playback({ sessionId: `s${++sessions}`, stream: { videoCopied: false, videoCodec: 'h264', dynamicRange: null, hdr10Plus: false, height: 720, videoBitrate: 4_000_000, toneMapped: true, losslessAudio: false, audioChannels: 2 } })
      },
      'GET /items/m1': () => ({
        ...summary({ id: 'm1', kind: 'Movie', title: 'Film' }),
        files: [{ id: 'f1', path: '/f.mkv', size: 1, width: 1920, height: 1080, videoRange: 'Hdr10', streams: [{ index: 0, type: 'Video' }] }],
      }),
    })
    const page = renderPlayer()
    const button = await waitFor(() => page.getByRole('button', { name: 'Quality, Automatic, 720p' }))
    expect(button).toHaveTextContent('Auto')
    // The file's details come from the title.
    fireEvent.click(button)
    await waitFor(() => expect(page.getByRole('menuitemradio', { name: /^Original/ })).toHaveTextContent('Original1080p HDR'))
    expect(page.getAllByRole('menuitemradio').slice(0, 4).map((r) => r.firstChild?.textContent)).toEqual(['Automatic', 'Original', '720p', '480p'])
    const automatic = page.getByRole('menuitemradio', { name: /^Automatic/ })
    expect(automatic).toHaveAttribute('aria-checked', 'true')
    expect(automatic).toHaveTextContent('Automatic720p')

    // The choice in use again only closes the menu.
    fireEvent.click(automatic)
    expect(page.queryByRole('menu')).toBeNull()
    expect(sessions).toBe(1)

    fireEvent.click(button)
    fireEvent.click(page.getByRole('menuitemradio', { name: /^480p/ }))
    expect(page.queryByRole('menu')).toBeNull()
    await waitFor(() => expect(sessions).toBe(2))
    expect(asked[1]).toMatchObject({ maxHeight: 480, audioStreamIndex: 1 })
    // Until the new stream plays.
    expect(page.getByRole('button', { name: /^Quality, 480p/ })).toBeDisabled()
    await act(async () => page.unmount())
  })
})

describe('Player on a phone', () => {
  it('has speed, quality and the sleep timer in one ⋯ menu as well', async () => {
    mockApi({ 'POST /playback': () => playback(), 'DELETE /playback/s1': undefined })
    const page = renderPlayer()
    // The phone's ⋯ (shown by the CSS below 680 px) and the separate buttons it stands in for.
    const more = await waitFor(() => page.getByRole('button', { name: 'More options' }))
    // Quality is only offered once the stream's details are in, which can come a moment after the controls.
    await waitFor(() => page.getByRole('button', { name: /^Quality/ }))
    expect(more.closest('.player-narrow-only')).not.toBeNull()
    for (const name of [/^Speed/, /^Quality/, 'Back 10 seconds']) expect(page.getByRole('button', { name }).closest('.player-wide-only')).not.toBeNull()

    fireEvent.click(more)
    const menu = page.getByRole('menu')
    for (const heading of ['Speed', 'Quality', 'While watching', 'Sleep timer']) expect(within(menu).getByRole('heading', { name: heading })).toBeInTheDocument()
    fireEvent.click(within(menu).getByRole('menuitemradio', { name: '1.5×' }))
    expect(page.getByRole('button', { name: /^Speed/ })).toHaveTextContent('1.5×')
    await act(async () => page.unmount())
  })
})

describe('Player Skip intro', () => {
  it('shows the pill without taking focus, so Space still pauses', async () => {
    mockApi({ 'POST /playback': () => playback({ markers: [{ type: 'Intro', start: 5, end: 90 }] }), 'DELETE /playback/s1': undefined })
    const page = renderPlayer()
    const video = page.container.querySelector('video')!
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/s1/'))
    Object.defineProperty(video, 'currentTime', { configurable: true, get: () => 10, set: () => undefined })
    Object.defineProperty(video, 'paused', { configurable: true, get: () => false })
    fireEvent.timeUpdate(video)
    const skip = await waitFor(() => page.getByRole('button', { name: /Skip intro/ }))
    expect(skip).not.toHaveFocus()

    const pause = vi.mocked(window.HTMLMediaElement.prototype.pause)
    pause.mockClear()
    fireEvent.keyDown(document.activeElement ?? document.body, { key: ' ' })
    expect(pause).toHaveBeenCalled()
    await act(async () => page.unmount())
  })
})

describe('Player menus from the keyboard', () => {
  it('opens on the chosen item, moves with the arrows, and gives the focus back to its button', async () => {
    mockApi({ 'POST /playback': () => playback(), 'DELETE /playback/s1': undefined })
    const page = renderPlayer()
    const audio = await waitFor(() => page.getByRole('button', { name: 'Audio' }))
    await waitFor(() => expect(page.container.querySelector('video')?.getAttribute('src')).toContain('/playback/s1/'))

    fireEvent.click(audio)
    const english = page.getByRole('menuitemradio', { name: /English/ })
    await waitFor(() => expect(english).toHaveFocus())
    fireEvent.keyDown(english, { key: 'ArrowDown' })
    expect(page.getByRole('menuitemradio', { name: /French/ })).toHaveFocus()

    fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
    await waitFor(() => expect(page.queryByRole('menu')).toBeNull())
    expect(audio).toHaveFocus()
    await act(async () => page.unmount())
  })
})

describe('Player taps and clicks on the picture', () => {
  it('only brings the controls back at a tap while they’re hidden, rather than pausing', async () => {
    mockApi({ 'POST /playback': () => playback(), 'DELETE /playback/s1': undefined })
    const page = renderPlayer()
    const video = page.container.querySelector('video')!
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/s1/'))
    Object.defineProperty(video, 'paused', { configurable: true, get: () => false })
    fireEvent.play(video)
    // The controls hide after a few seconds of playing untouched.
    await waitFor(() => expect(page.container.querySelector('.player.chrome-hidden')).not.toBeNull(), { timeout: 4000 })

    const pause = vi.mocked(window.HTMLMediaElement.prototype.pause)
    pause.mockClear()
    fireEvent.pointerDown(video, { pointerType: 'touch' })
    fireEvent.click(video, { detail: 1 })
    await new Promise((resolve) => setTimeout(resolve, 300))
    expect(pause).not.toHaveBeenCalled()
    expect(page.container.querySelector('.player.chrome-hidden')).toBeNull()
    await act(async () => page.unmount())
  }, 10_000)

  it('neither pauses nor resumes on the way into full screen at a double-click', async () => {
    mockApi({ 'POST /playback': () => playback(), 'DELETE /playback/s1': undefined })
    const page = renderPlayer()
    const video = page.container.querySelector('video')!
    await waitFor(() => expect(video.getAttribute('src')).toContain('/playback/s1/'))
    Object.defineProperty(video, 'paused', { configurable: true, get: () => false })
    fireEvent.play(video)

    const pause = vi.mocked(window.HTMLMediaElement.prototype.pause)
    pause.mockClear()
    fireEvent.pointerDown(video, { pointerType: 'mouse' })
    fireEvent.click(video, { detail: 1 })
    fireEvent.pointerDown(video, { pointerType: 'mouse' })
    fireEvent.click(video, { detail: 2 })
    await new Promise((resolve) => setTimeout(resolve, 300))
    expect(pause).not.toHaveBeenCalled()

    // A single click still pauses, a moment later.
    fireEvent.click(video, { detail: 1 })
    await waitFor(() => expect(pause).toHaveBeenCalledTimes(1))
    await act(async () => page.unmount())
  })
})

describe('Player with a title that isn’t there', () => {
  it('says so plainly and offers only Go back, since trying again won’t help', async () => {
    mockApi({ 'POST /playback': () => new Response('', { status: 404, statusText: 'Not Found' }) })
    const page = renderPlayer()
    const box = await waitFor(() => page.getByRole('alertdialog'))
    expect(box).toHaveTextContent('This isn’t in your library any more.')
    expect(box).not.toHaveTextContent('404')
    expect(within(box).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
    expect(within(box).getByRole('button', { name: 'Go back' })).toHaveFocus()
    await act(async () => page.unmount())
  })

  it('says the same for an address whose id the server can’t read', async () => {
    mockApi({ 'POST /playback': () => new Response('', { status: 400, statusText: 'Bad Request' }) })
    const page = renderPlayer()
    const box = await waitFor(() => page.getByRole('alertdialog'))
    expect(box).toHaveTextContent('This isn’t in your library any more.')
    expect(box).not.toHaveTextContent('400')
    expect(within(box).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument()
    await act(async () => page.unmount())
  })
})
