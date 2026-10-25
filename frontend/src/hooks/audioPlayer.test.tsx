import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { ItemSummary } from '../api/types'
import { detail, mockApi, summary } from '../test/mockApi'
import { AudioPlayerProvider } from './audioPlayer'
import { useAudioPlayer } from './audioPlayerContext'
import { queueStorageKey } from './queue'

beforeAll(() => {
  // jsdom has no media playback.
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined)
  window.HTMLMediaElement.prototype.pause = vi.fn()
})

afterEach(() => vi.unstubAllGlobals())

const track = (id: string, title: string) => summary({ id, kind: 'Track', title, parentId: 'album-1', seriesTitle: 'Artist', seriesId: 'artist-1', durationSeconds: 240 })
const one = track('t1', 'One')
const two = track('t2', 'Two')
const album = detail(summary({ id: 'album-1', kind: 'Album', title: 'Album' }), { children: [one, two] })

/** Answers playback requests and remembers what was asked for. */
function playbackApi(extra: Record<string, unknown> = {}) {
  const requests: { itemId: string; startPositionSeconds?: number }[] = []
  mockApi({
    ...extra,
    'POST /playback': (init) => {
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
    'DELETE /playback/session-t1': undefined,
    'DELETE /playback/session-t2': undefined,
  })
  return requests
}

const stored = () => JSON.parse(sessionStorage.getItem(queueStorageKey) ?? 'null') as { ids: string[]; albums: string[]; index: number; position: number } | null

describe('AudioPlayerProvider', () => {
  it('plays a queue, asks the server for the track and remembers the queue for a reload', async () => {
    const requests = playbackApi()
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

  it('brings a stored queue back paused after a reload, and Play picks up where it was', async () => {
    sessionStorage.setItem(queueStorageKey, JSON.stringify({ ids: ['t1', 't2'], albums: ['album-1'], index: 1, position: 42 }))
    const requests = playbackApi({ '/items/album-1': album })
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
})
