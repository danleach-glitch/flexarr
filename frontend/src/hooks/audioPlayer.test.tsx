import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { detail, mockApi, summary } from '../test/mockApi'
import { AudioPlayerProvider } from './audioPlayer'
import { useAudioPlayer } from './audioPlayerContext'
import { queueStorageKey } from './queue'

beforeAll(() => {
  // jsdom has no media playback: play/pause keep `paused` honest and fire their events, and nothing else.
  Object.defineProperty(window.HTMLMediaElement.prototype, 'paused', {
    configurable: true,
    get(this: HTMLMediaElement) {
      return this.dataset.playing !== '1'
    },
  })
  window.HTMLMediaElement.prototype.play = vi.fn(function (this: HTMLMediaElement) {
    if (this.paused) {
      this.dataset.playing = '1'
      this.dispatchEvent(new Event('play'))
    }
    return Promise.resolve()
  })
  window.HTMLMediaElement.prototype.pause = vi.fn(function (this: HTMLMediaElement) {
    if (!this.paused) {
      delete this.dataset.playing
      this.dispatchEvent(new Event('pause'))
    }
  })
})

beforeEach(() => localStorage.clear())
afterEach(() => vi.unstubAllGlobals())

const track = (id: string, title: string, album = 'album-1') => summary({ id, kind: 'Track', title, parentId: album, seriesTitle: 'Artist', seriesId: 'artist-1', durationSeconds: 240 })
const one = track('t1', 'One')
const two = track('t2', 'Two')
const three = track('t3', 'Three', 'album-2')
const album = detail(summary({ id: 'album-1', kind: 'Album', title: 'Album' }), { children: [one, two] })

/** Answers playback requests and remembers what was asked for, and which sessions were ended. */
function playbackApi(extra: Record<string, unknown> = {}) {
  const requests: { itemId: string; startPositionSeconds?: number }[] = []
  const stopped: string[] = []
  const ended: string[] = []
  mockApi({
    ...extra,
    'POST /playback': (init?: RequestInit) => {
      const body = JSON.parse(String(init?.body)) as { itemId: string; startPositionSeconds?: number }
      requests.push(body)
      return {
        sessionId: `session-${body.itemId}`,
        method: 'DirectPlay',
        url: `/stream/${body.itemId}`,
        reasons: [],
        durationSeconds: 251,
        startPositionSeconds: body.startPositionSeconds ?? 0,
        fileId: 'f1',
        audioTracks: [],
        selectedAudioIndex: null,
        subtitles: [],
        selectedSubtitleKey: null,
        subtitleBurnedIn: false,
      }
    },
    ...Object.fromEntries(
      ['t1', 't2', 't3'].flatMap((id) => [
        [`DELETE /playback/session-${id}`, () => void stopped.push(`session-${id}`)],
        [`POST /playback/session-${id}/progress`, (init?: RequestInit) => void ((JSON.parse(String(init?.body)) as { ended: boolean }).ended && ended.push(`session-${id}`))],
      ]),
    ),
  })
  return { requests, stopped, ended }
}

const stored = () => JSON.parse(sessionStorage.getItem(queueStorageKey) ?? 'null') as { ids: string[]; albums: string[]; index: number; position: number } | null

/** The two elements, as the provider renders them. */
const elements = () => Array.from(document.querySelectorAll('audio'))

/** Moves an element to `seconds` of a track the server said is 251 s long and lets the player notice. */
function tick(el: HTMLAudioElement, seconds: number) {
  act(() => {
    el.currentTime = seconds
    el.dispatchEvent(new Event('timeupdate'))
  })
}

/** The element reaches the end of its track: browsers pause it and say so, then say it ended. */
const ends = (el: HTMLAudioElement) =>
  act(() => {
    el.pause()
    el.dispatchEvent(new Event('ended'))
  })

describe('AudioPlayerProvider', () => {
  it('tries a track whose stream fails once more from where it was, then skips it and says so', async () => {
    const { requests } = playbackApi()
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
    act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
    await waitFor(() => expect(requests.map((r) => r.itemId)).toEqual(['t1']))
    const [el] = elements()

    tick(el, 30)
    act(() => void el.dispatchEvent(new Event('error')))
    await waitFor(() => expect(requests.map((r) => [r.itemId, r.startPositionSeconds])).toEqual([['t1', 0], ['t1', 30]]))
    act(() => void el.dispatchEvent(new Event('error')))

    await waitFor(() => expect(result.current.current?.id).toBe('t2'))
    expect(result.current.failure).toBe('Couldn\'t play “One”')
    await waitFor(() => expect(requests.map((r) => r.itemId)).toEqual(['t1', 't1', 't2']))
  })

  it('skips a track the server won\'t start, after asking twice', async () => {
    const requests: { itemId: string }[] = []
    mockApi({
      'POST /playback': (init?: RequestInit) => {
        const body = JSON.parse(String(init?.body)) as { itemId: string }
        requests.push(body)
        return body.itemId === 't1'
          ? new Response('', { status: 503 })
          : { sessionId: 'session-t2', method: 'DirectPlay', url: '/stream/t2', reasons: [], durationSeconds: 251, startPositionSeconds: 0, fileId: 'f1',
              audioTracks: [], selectedAudioIndex: null, subtitles: [], selectedSubtitleKey: null, subtitleBurnedIn: false }
      },
    })
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))

    await waitFor(() => expect(result.current.current?.id).toBe('t2'))
    expect(requests.map((r) => r.itemId)).toEqual(['t1', 't1', 't2'])
    expect(result.current.failure).toBe('Couldn\'t play “One”')
  })

  it('plays a queue, asks the server for the track and remembers the queue for a reload', async () => {
    const { requests } = playbackApi()
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
    await waitFor(() => expect(requests).toEqual([{ itemId: 't1', profile: expect.anything(), startPositionSeconds: 0 }]))
    expect(result.current.current?.id).toBe('t1')
    await waitFor(() => expect(result.current.duration).toBe(251))
    expect(stored()).toEqual({ ids: ['t1', 't2'], albums: ['album-1'], index: 0, position: 0 })

    // Queue changes are remembered too, without touching playback.
    act(() => result.current.enqueue([one] as unknown as ItemSummary[]))
    expect(stored()?.ids).toEqual(['t1', 't2', 't1'])
    expect(requests).toHaveLength(1)

    act(() => result.current.next())
    await waitFor(() => expect(requests[1]).toMatchObject({ itemId: 't2' }))
    expect(stored()?.index).toBe(1)

    // Stopping forgets it.
    act(() => result.current.stop())
    expect(result.current.current).toBeUndefined()
    expect(sessionStorage.getItem(queueStorageKey)).toBeNull()
  })

  it('opens Now Playing for Play and Shuffle, not for a track picked from a list', async () => {
    playbackApi()
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    act(() => result.current.play([one, two] as unknown as ItemSummary[], 1))
    expect(result.current.nowPlayingOpen).toBe(false)
    act(() => result.current.play([one, two] as unknown as ItemSummary[], 0, { openNowPlaying: true }))
    expect(result.current.nowPlayingOpen).toBe(true)
    act(() => result.current.setNowPlayingOpen(false))
    // Nothing to play: nothing to show.
    act(() => result.current.play([], 0, { openNowPlaying: true }))
    expect(result.current.nowPlayingOpen).toBe(false)
  })

  it('brings a stored queue back paused after a reload, and Play picks up where it was', async () => {
    sessionStorage.setItem(queueStorageKey, JSON.stringify({ ids: ['t1', 't2'], albums: ['album-1'], index: 1, position: 42 }))
    const { requests } = playbackApi({ '/items/album-1': album })
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    await waitFor(() => expect(result.current.queue.map((t) => t.id)).toEqual(['t1', 't2']))
    expect(result.current.index).toBe(1)
    expect(result.current.current?.title).toBe('Two')
    expect(result.current.playing).toBe(false)
    // Nothing is asked of the server until someone presses Play: browsers block sound nobody asked for.
    expect(requests).toEqual([])
    expect(result.current.duration).toBe(240)

    act(() => result.current.toggle())
    await waitFor(() => expect(requests).toEqual([{ itemId: 't2', profile: expect.anything(), startPositionSeconds: 42 }]))
  })

  it('drops stored tracks that no longer exist and forgets a queue with none left', async () => {
    sessionStorage.setItem(queueStorageKey, JSON.stringify({ ids: ['gone', 't2'], albums: ['album-1'], index: 0, position: 5 }))
    playbackApi({ '/items/album-1': album, '/items/gone': new Response('', { status: 404 }) })
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    await waitFor(() => expect(result.current.current?.id).toBe('t2'))
    expect(result.current.queue).toHaveLength(1)
    expect(stored()).toEqual({ ids: ['t2'], albums: ['album-1'], index: 0, position: 5 })
  })

  it('a queue played before the restore finishes wins', async () => {
    sessionStorage.setItem(queueStorageKey, JSON.stringify({ ids: ['t1'], albums: ['album-1'], index: 0, position: 5 }))
    let answer: (value: unknown) => void = () => undefined
    playbackApi({ '/items/album-1': () => new Promise((resolve) => (answer = resolve)) })
    const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

    act(() => result.current.play([two] as unknown as ItemSummary[], 0))
    await act(async () => {
      answer(album)
      await Promise.resolve()
    })
    expect(result.current.queue.map((t) => t.id)).toEqual(['t2'])
    expect(stored()?.ids).toEqual(['t2'])
  })

  describe('gapless', () => {
    it('gets the next track ready in the other element near the end, and switches to it the moment the track ends', async () => {
      const { requests, stopped, ended } = playbackApi()
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))

      // Well before the end nothing is asked for; within ten seconds of it, the next track's session starts.
      tick(a, 200)
      expect(requests).toHaveLength(1)
      tick(a, 242)
      await waitFor(() => expect(requests[1]).toMatchObject({ itemId: 't2', startPositionSeconds: 0 }))
      await waitFor(() => expect(b.getAttribute('src')).toBe('/stream/t2'))
      expect(b.paused).toBe(true)
      expect(result.current.index).toBe(0)
      expect(stopped).toEqual([])

      // The end: the other element plays at once, with no further request; the old session is reported ended and stopped.
      ends(a)
      expect(result.current.index).toBe(1)
      expect(result.current.current?.id).toBe('t2')
      expect(b.paused).toBe(false)
      expect(requests).toHaveLength(2)
      await waitFor(() => expect(stopped).toEqual(['session-t1']))
      await waitFor(() => expect(ended).toEqual(['session-t1']))
      expect(a.getAttribute('src')).toBeNull()
      expect(result.current.playing).toBe(true)
      expect(result.current.duration).toBe(251)

      // Now the first element is the spare one, and gets what follows: the elements take turns.
      act(() => result.current.enqueue([three] as unknown as ItemSummary[]))
      tick(b, 245)
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t3'))
    })

    it('leaves a book queued next until it is reached, so its session is started with the device name', async () => {
      const { requests } = playbackApi()
      const book = summary({ id: 'b1', kind: 'Audiobook', title: 'The Book', durationSeconds: 200 }) as unknown as ItemSummary
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, book] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))
      tick(a, 245)
      tick(a, 246)
      expect(b.getAttribute('src')).toBeNull()
      expect(requests.map((r) => r.itemId)).toEqual(['t1'])
    })

    it('lets a prepared next track go when the queue changes so it is no longer next', async () => {
      const { requests, stopped } = playbackApi()
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))
      tick(a, 245)
      await waitFor(() => expect(b.getAttribute('src')).toBe('/stream/t2'))

      act(() => result.current.playNext([three] as unknown as ItemSummary[]))
      await waitFor(() => expect(stopped).toEqual(['session-t2']))
      expect(b.getAttribute('src')).toBeNull()

      // The track that is next now takes its place at the next tick.
      tick(a, 246)
      await waitFor(() => expect(b.getAttribute('src')).toBe('/stream/t3'))
      expect(requests.map((r) => r.itemId)).toEqual(['t1', 't2', 't3'])
    })

    it('Next while the next track is ready switches to it without asking the server again', async () => {
      const { requests, stopped } = playbackApi()
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))
      tick(a, 245)
      await waitFor(() => expect(b.getAttribute('src')).toBe('/stream/t2'))

      act(() => result.current.next())
      expect(result.current.index).toBe(1)
      expect(b.paused).toBe(false)
      await waitFor(() => expect(stopped).toEqual(['session-t1']))
      expect(requests).toHaveLength(2)
    })

    it('a load that overtakes a half-prepared next track ends the session it was given', async () => {
      const held: (() => void)[] = []
      const { stopped } = playbackApi()
      // The second request (for t2) is held back until the test lets it through.
      const { requests } = { requests: [] as string[] }
      mockApi({
        'POST /playback': (init?: RequestInit) => {
          const body = JSON.parse(String(init?.body)) as { itemId: string }
          requests.push(body.itemId)
          const answer = {
            sessionId: `session-${body.itemId}`,
            method: 'DirectPlay',
            url: `/stream/${body.itemId}`,
            reasons: [],
            durationSeconds: 251,
            startPositionSeconds: 0,
            fileId: 'f1',
            audioTracks: [],
            selectedAudioIndex: null,
            subtitles: [],
            selectedSubtitleKey: null,
            subtitleBurnedIn: false,
          }
          return body.itemId === 't2' ? new Promise((resolve) => held.push(() => resolve(answer))) : answer
        },
        'DELETE /playback/session-t1': () => void stopped.push('session-t1'),
        'DELETE /playback/session-t2': () => void stopped.push('session-t2'),
        'DELETE /playback/session-t3': () => void stopped.push('session-t3'),
      })
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))
      tick(a, 245)
      await waitFor(() => expect(requests).toEqual(['t1', 't2']))

      // The listener skips elsewhere while t2's session is still being made.
      act(() => result.current.play([three] as unknown as ItemSummary[], 0))
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t3'))
      await act(async () => {
        held.forEach((release) => release())
        await Promise.resolve()
      })
      await waitFor(() => expect(stopped).toContain('session-t2'))
      expect(b.getAttribute('src')).toBeNull()
    })
  })

  describe('crossfade and loudness', () => {
    /** A stand-in Web Audio graph that records what each element's gain was told to do. */
    function stubWebAudio() {
      const gains: { el: HTMLMediaElement | undefined; calls: string[] }[] = []
      class FakeContext {
        currentTime = 100
        state = 'running'
        destination = {}
        resume = vi.fn().mockResolvedValue(undefined)
        createGain() {
          const record = { el: undefined as HTMLMediaElement | undefined, calls: [] as string[] }
          gains.push(record)
          const param = {
            value: 1,
            cancelScheduledValues: () => undefined,
            setValueAtTime: (v: number) => void record.calls.push(`set ${+v.toFixed(3)}`),
            linearRampToValueAtTime: (v: number, at: number) => void record.calls.push(`ramp ${+v.toFixed(3)} over ${+(at - 100).toFixed(1)}`),
          }
          return { gain: param, connect: () => undefined, record }
        }
        createMediaElementSource(el: HTMLMediaElement) {
          return { connect: (gain: { record: { el: HTMLMediaElement | undefined } }) => void (gain.record.el = el) }
        }
      }
      vi.stubGlobal('AudioContext', FakeContext)
      return (el: HTMLMediaElement) => gains.find((g) => g.el === el)?.calls ?? []
    }

    it('starts the next track early and fades the two across each other, moving the queue on at the start of the fade', async () => {
      localStorage.setItem('flexarr.crossfade', '4')
      const gainOf = stubWebAudio()
      const { stopped, ended } = playbackApi()
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([one, two] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/t1'))
      tick(a, 242)
      await waitFor(() => expect(b.getAttribute('src')).toBe('/stream/t2'))

      // Six seconds left: not yet. Three: the fade begins.
      tick(a, 245)
      expect(result.current.index).toBe(0)
      tick(a, 248)
      expect(result.current.index).toBe(1)
      expect(b.paused).toBe(false)
      expect(a.paused).toBe(false)
      expect(gainOf(b).slice(-2)).toEqual(['set 0', 'ramp 1 over 4'])
      expect(gainOf(a).slice(-1)).toEqual(['ramp 0 over 4'])
      expect(stopped).toEqual([])

      // Pausing holds both; the old track's end finishes it off.
      act(() => result.current.toggle())
      expect(a.paused).toBe(true)
      expect(b.paused).toBe(true)
      act(() => result.current.toggle())
      expect(a.paused).toBe(false)
      ends(a)
      await waitFor(() => expect(stopped).toEqual(['session-t1']))
      expect(ended).toEqual(['session-t1'])
      expect(result.current.index).toBe(1)
      expect(result.current.playing).toBe(true)
    })

    it('levels each track by its ReplayGain, album gain within an album, unless normalising is off', async () => {
      const gainOf = stubWebAudio()
      playbackApi({
        '/items/t1/loudness': { trackGain: -6.0206, albumGain: -12.0412, trackPeak: 0.5, albumPeak: 0.5 },
        '/items/t3/loudness': { trackGain: 6.0206, albumGain: null, trackPeak: 0.8, albumPeak: null },
      })
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      // One and Two are from the same album: One gets the album gain (-12 dB, a quarter).
      act(() => result.current.play([one, two, three] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(gainOf(a)).toEqual(['set 0.25']))

      // Three stands alone and has no album gain: its own, +6 dB, held back by its peak to 1/0.8.
      act(() => result.current.skipTo(2))
      await waitFor(() => expect(gainOf(a).slice(-1)).toEqual(['set 1.25']))

      // Turning normalising off brings the track back to unity straight away.
      act(() => {
        localStorage.setItem('flexarr.normaliseVolume', '0')
        window.dispatchEvent(new Event('flexarr:music-settings'))
      })
      expect(gainOf(a).slice(-1)).toEqual(['set 1'])
      expect(gainOf(b)).toEqual([])
    })
  })

  describe('podcast episodes', () => {
    const episode = (id: string, title: string) => summary({ id, kind: 'PodcastEpisode', title, parentId: 'pod-1', seriesTitle: 'The Show', seriesId: 'pod-1', durationSeconds: 3600 })
    const first = episode('e1', 'First')
    const second = episode('e2', 'Second')

    /**
     * The server resumes e1 at 300 s, remembers progress reports and speed changes, keeps the profile's podcast speed
     * (`speed`, or none for an older server) and after e1 auto-plays e2.
     */
    function podcastApi(speed: number | null) {
      const requests: { itemId: string; startPositionSeconds?: number }[] = []
      const progress: { session: string; positionSeconds: number; paused: boolean; ended: boolean }[] = []
      const saved: unknown[] = []
      mockApi({
        'POST /playback': (init?: RequestInit) => {
          const body = JSON.parse(String(init?.body)) as { itemId: string; startPositionSeconds?: number }
          requests.push(body)
          return {
            sessionId: `session-${body.itemId}`,
            method: 'DirectPlay',
            url: `/stream/${body.itemId}`,
            reasons: [],
            durationSeconds: 3600,
            startPositionSeconds: body.startPositionSeconds ?? (body.itemId === 'e1' ? 300 : 0),
            fileId: '',
            audioTracks: [],
            selectedAudioIndex: null,
            subtitles: [],
            selectedSubtitleKey: null,
            subtitleBurnedIn: false,
          }
        },
        ...(speed ? { 'GET /podcasts/preferences': { autoDownload: true, keepEpisodes: 3, speed } } : {}),
        'PUT /podcasts/preferences': (init?: RequestInit) => {
          saved.push(JSON.parse(String(init?.body)))
          return { autoDownload: true, keepEpisodes: 3, speed: 2 }
        },
        '/podcasts/episodes/e1/next': second,
        ...Object.fromEntries(
          ['e1', 'e2'].flatMap((id) => [
            [`DELETE /playback/session-${id}`, () => undefined],
            [`POST /playback/session-${id}/progress`, (init?: RequestInit) => void progress.push({ session: `session-${id}`, ...JSON.parse(String(init?.body)) })],
          ]),
        ),
      })
      return { requests, progress, saved }
    }

    it('resume where the server says, without levels, at the speed last chosen', async () => {
      localStorage.setItem('flexarr.podcastSpeed', '1.5')
      const fetchCalls = podcastApi(null)
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })

      act(() => result.current.play([first] as unknown as ItemSummary[], 0))
      // No start given: the server knows where this profile got to.
      await waitFor(() => expect(fetchCalls.requests).toEqual([{ itemId: 'e1', profile: expect.anything() }]))
      const [a] = elements()
      await waitFor(() => expect(a.playbackRate).toBe(1.5))
      expect(result.current.speed).toBe(1.5)
      act(() => {
        a.dispatchEvent(new Event('loadedmetadata'))
      })
      expect(a.currentTime).toBe(300)

      // 15 back, 30 on.
      act(() => result.current.skipBy(30))
      expect(a.currentTime).toBe(330)
      act(() => result.current.skipBy(-15))
      expect(a.currentTime).toBe(315)

      // Pausing says where, so another device picks it up there.
      act(() => a.pause())
      await waitFor(() => expect(fetchCalls.progress).toContainEqual({ session: 'session-e1', positionSeconds: 315, paused: true, ended: false }))

      // A new speed is every show's, kept on the server for the profile's other devices.
      act(() => result.current.setSpeed(2))
      expect(a.playbackRate).toBe(2)
      await waitFor(() => expect(fetchCalls.saved).toEqual([{ speed: 2 }]))
    })

    it('play at the profile’s speed, go on to the next episode by themselves, and a sleep timer can stop at the end', async () => {
      const { requests, progress } = podcastApi(1.25)
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([first] as unknown as ItemSummary[], 0))
      const [a, b] = elements()
      await waitFor(() => expect(a.playbackRate).toBe(1.25))

      // Nothing queued after it: auto-play asks the server what's next.
      ends(a)
      await waitFor(() => expect(result.current.current?.id).toBe('e2'))
      await waitFor(() => expect(requests.map((r) => r.itemId)).toEqual(['e1', 'e2']))
      const playing = [a, b].find((el) => el.src.endsWith('/stream/e2'))!

      act(() => result.current.setSleep({ endOfEpisode: true }))
      ends(playing)
      await waitFor(() => expect(progress).toContainEqual(expect.objectContaining({ session: 'session-e2', ended: true })))
      expect(result.current.current?.id).toBe('e2')
      expect(result.current.playing).toBe(false)
      expect(result.current.sleep).toBeNull()
      expect(requests.map((r) => r.itemId)).toEqual(['e1', 'e2'])
    })

    it('stop without saying where when taken out of Continue, and leave another episode playing', async () => {
      const { progress } = podcastApi(null)
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([first] as unknown as ItemSummary[], 0))
      const [a] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/stream/e1'))
      act(() => void a.dispatchEvent(new Event('loadedmetadata')))

      act(() => result.current.forget(second as unknown as ItemSummary))
      expect(result.current.current?.id).toBe('e1')

      act(() => result.current.forget(first as unknown as ItemSummary))
      expect(result.current.current).toBeUndefined()
      expect(result.current.playing).toBe(false)
      act(() => void window.dispatchEvent(new Event('pagehide')))
      await new Promise((r) => setTimeout(r, 20))
      expect(progress).toEqual([])
    })
  })

  describe('books', () => {
    const book = summary({ id: 'b1', kind: 'Audiobook', title: 'The Book', durationSeconds: 200 }) as unknown as ItemSummary
    const file = (index: number) => ({ index, fileId: `f${index}`, offsetSeconds: index * 100, durationSeconds: 100, container: 'mp3', audioCodec: 'mp3', size: 1, url: `/book/${index}` })
    const manifest = {
      itemId: 'b1',
      durationSeconds: 200,
      files: [file(0), file(1)],
      chapters: [
        { index: 0, title: 'One', startSeconds: 0, endSeconds: 50 },
        { index: 1, title: 'Two', startSeconds: 50, endSeconds: 100 },
        { index: 2, title: 'Three', startSeconds: 100, endSeconds: 200 },
      ],
      bookmarks: [],
      progress: { positionSeconds: 0, finished: false, finishedAt: null, updatedAt: null, device: null },
      speed: 1,
      listening: { speed: 1, skipBackSeconds: 30, skipForwardSeconds: 30, smartRewind: true },
      nextInSeries: null,
    }

    /** Starts the book's session; `expired` makes the session's progress reports answer 404, as after a long pause. */
    function bookApi({ expired = false } = {}) {
      const progress: { positionSeconds: number; paused: boolean; ended: boolean }[] = []
      const direct: { positionSeconds: number; ended: boolean }[] = []
      mockApi({
        'POST /playback': () => ({
          sessionId: 'session-b1',
          method: 'DirectPlay',
          url: '',
          reasons: [],
          durationSeconds: 200,
          startPositionSeconds: 0,
          fileId: '',
          audioTracks: [],
          selectedAudioIndex: null,
          subtitles: [],
          selectedSubtitleKey: null,
          subtitleBurnedIn: false,
          book: manifest,
        }),
        'DELETE /playback/session-b1': () => undefined,
        'POST /playback/session-b1/progress': (init?: RequestInit) =>
          expired ? new Response('', { status: 404 }) : void progress.push(JSON.parse(String(init?.body))),
        'PUT /items/b1/progress': (init?: RequestInit) => void direct.push(JSON.parse(String(init?.body))),
      })
      return { progress, direct }
    }

    /** The element has read the file's header: it knows its length, and can go to a place in it. */
    const metadata = (el: HTMLAudioElement) => act(() => void el.dispatchEvent(new Event('loadedmetadata')))

    async function start() {
      const { result } = renderHook(useAudioPlayer, { wrapper: AudioPlayerProvider })
      act(() => result.current.play([book]))
      const [a, b] = elements()
      await waitFor(() => expect(a.getAttribute('src')).toBe('/book/0'))
      await waitFor(() => expect(result.current.book?.itemId).toBe('b1'))
      metadata(a)
      return { result, a, b }
    }

    it('keep the place chosen in another file while that file loads, and smart rewind leaves a chosen place alone', async () => {
      const { progress } = bookApi()
      const { result, a } = await start()
      tick(a, 30)
      // Paused a long while: smart rewind would step back on Play...
      act(() => result.current.toggle())
      const now = Date.now()
      vi.spyOn(Date, 'now').mockReturnValue(now + 3_600_000)

      // ...but a chapter tapped in the meantime is where it plays from.
      act(() => result.current.seek(150))
      expect(a.getAttribute('src')).toBe('/book/1')
      // Until the new file says how long it is, the book's place is the one chosen, not that file's start.
      act(() => a.pause())
      act(() => result.current.toggle())
      act(() => a.pause())
      await waitFor(() => expect(progress.at(-1)).toMatchObject({ positionSeconds: 150, paused: true }))
      metadata(a)
      expect(a.currentTime).toBe(50)
      expect(a.getAttribute('src')).toBe('/book/1')
      vi.restoreAllMocks()
    })

    it('leave a next file that failed to load ahead until the one playing ends', async () => {
      bookApi()
      const { a, b } = await start()
      tick(a, 92)
      expect(b.getAttribute('src')).toBe('/book/1')
      act(() => void b.dispatchEvent(new Event('error')))
      expect(b.getAttribute('src')).toBeNull()
      tick(a, 94)
      tick(a, 96)
      expect(b.getAttribute('src')).toBeNull()

      ends(a)
      expect(a.getAttribute('src')).toBe('/book/1')
    })

    it('move "end of chapter" to the chapter a seek lands in', async () => {
      bookApi()
      const { result, a } = await start()
      tick(a, 10)
      act(() => result.current.setSleep({ endOfChapter: true, at: 50 }))

      // Next chapter lands exactly where the timer was: it now waits for the end of that chapter instead.
      act(() => result.current.next())
      expect(result.current.sleep).toEqual({ endOfChapter: true, at: 100 })
      act(() => result.current.seek(120))
      expect(result.current.sleep).toEqual({ endOfChapter: true, at: 200 })
      expect(result.current.playing).toBe(true)
    })

    it('stop without sending the place when taken out of Continue, so neither the stop nor the page closing puts it back', async () => {
      const { progress, direct } = bookApi()
      const beacon = vi.fn(() => true)
      vi.stubGlobal('navigator', { ...navigator, sendBeacon: beacon })
      const { result, a } = await start()
      tick(a, 40)

      act(() => result.current.forget(book))
      expect(result.current.current).toBeUndefined()
      expect(result.current.book).toBeNull()
      act(() => void window.dispatchEvent(new Event('pagehide')))
      await new Promise((r) => setTimeout(r, 20))
      expect(progress).toEqual([])
      expect(direct).toEqual([])
      expect(beacon).not.toHaveBeenCalled()
      // This browser's own place is forgotten too, so the next start doesn't bring it back.
      expect(JSON.parse(localStorage.getItem('flexarr.bookPlace.b1') ?? 'null')).toMatchObject({ position: 0 })
    })

    it('send the place to the book itself once the session has expired', async () => {
      const { direct } = bookApi({ expired: true })
      const { result, a } = await start()
      tick(a, 40)
      act(() => result.current.toggle())
      await waitFor(() => expect(direct).toEqual([expect.objectContaining({ positionSeconds: 40, ended: false })]))
    })

    it('send the place to the book itself as the page closes after a pause longer than a session lasts', async () => {
      const { progress, direct } = bookApi()
      const beacon = vi.fn(() => true)
      vi.stubGlobal('navigator', { ...navigator, sendBeacon: beacon })
      const { result, a } = await start()
      tick(a, 40)
      act(() => result.current.toggle())
      await waitFor(() => expect(progress.at(-1)).toMatchObject({ positionSeconds: 40, paused: true }))

      // Paused overnight: the server has ended the session, though nothing has asked it yet.
      const now = Date.now()
      vi.spyOn(Date, 'now').mockReturnValue(now + 11.5 * 3_600_000)
      act(() => void window.dispatchEvent(new Event('pagehide')))
      expect(beacon).not.toHaveBeenCalled()
      await waitFor(() => expect(direct).toEqual([expect.objectContaining({ positionSeconds: 40, ended: false })]))
      vi.restoreAllMocks()
    })
  })
})
