import { describe, expect, it } from 'vitest'
import type { ItemSummary } from '../api/types'
import { summary } from '../test/mockApi'
import { emptyQueue, queueReducer, readStoredQueue, rebuildQueue, serializeQueue, upcoming, type QueueAction, type QueueState } from './queue'

const track = (id: string, album = 'album-1') => summary({ id, kind: 'Track', title: id, parentId: album, seriesTitle: 'Artist' }) as unknown as ItemSummary
const [a, b, c, d, e] = ['a', 'b', 'c', 'd', 'e'].map((id) => track(id))

const ids = (state: QueueState) => state.tracks.map((t) => t.id).join('')
const run = (...actions: QueueAction[]) => actions.reduce(queueReducer, emptyQueue)
const playing = (...tracks: ItemSummary[]) => run({ type: 'play', tracks, index: 0 })

describe('queueReducer', () => {
  it('play replaces the queue, starts at the asked-for track and asks for playback', () => {
    const state = run({ type: 'play', tracks: [a, b, c], index: 1 })
    expect(ids(state)).toBe('abc')
    expect(state.index).toBe(1)
    expect(state.epoch).toBe(1)
  })

  it('advance moves to the next track without asking for playback, and stays put at the end', () => {
    const state = playing(a, b)
    const advanced = queueReducer(state, { type: 'advance' })
    expect(advanced.index).toBe(1)
    expect(advanced.epoch).toBe(state.epoch)
    expect(queueReducer(advanced, { type: 'advance' })).toBe(advanced)
  })

  it('play clamps an index past the end', () => {
    expect(run({ type: 'play', tracks: [a, b], index: 7 }).index).toBe(1)
  })

  it('playing the same tracks again restarts them', () => {
    const once = playing(a, b)
    const twice = queueReducer(once, { type: 'play', tracks: [a, b], index: 0 })
    expect(twice.epoch).toBe(once.epoch + 1)
  })

  it('enqueue adds to the end without disturbing what is playing', () => {
    const before = playing(a, b)
    const state = queueReducer(before, { type: 'enqueue', tracks: [c, d] })
    expect(ids(state)).toBe('abcd')
    expect(state.index).toBe(0)
    expect(state.epoch).toBe(before.epoch)
    expect(state.tracks[0]).toBe(before.tracks[0])
  })

  it('play next slots in right after the current track', () => {
    const state = queueReducer(run({ type: 'play', tracks: [a, b, c], index: 1 }), { type: 'playNext', tracks: [d, e] })
    expect(ids(state)).toBe('abdec')
    expect(state.index).toBe(1)
  })

  it('enqueue and play next start playing when nothing is queued', () => {
    for (const type of ['enqueue', 'playNext'] as const) {
      const state = run({ type, tracks: [a, b] })
      expect(ids(state)).toBe('ab')
      expect(state.index).toBe(0)
      expect(state.epoch).toBe(1)
    }
  })

  it('queues the same track twice as two separate rows', () => {
    const state = run({ type: 'enqueue', tracks: [a] }, { type: 'enqueue', tracks: [a] })
    expect(state.tracks).toHaveLength(2)
    expect(state.tracks[0]).not.toBe(state.tracks[1])
    expect(state.tracks[1].id).toBe('a')
  })

  it('ignores empty additions', () => {
    const before = playing(a)
    expect(queueReducer(before, { type: 'enqueue', tracks: [] })).toBe(before)
    expect(queueReducer(before, { type: 'playNext', tracks: [] })).toBe(before)
  })

  describe('remove', () => {
    const base = run({ type: 'play', tracks: [a, b, c, d], index: 2 })

    it('before the current track shifts the index back so the same track keeps playing', () => {
      const state = queueReducer(base, { type: 'remove', index: 0 })
      expect(ids(state)).toBe('bcd')
      expect(state.index).toBe(1)
      expect(state.tracks[state.index].id).toBe('c')
      expect(state.epoch).toBe(base.epoch)
    })

    it('after the current track leaves the index alone', () => {
      const state = queueReducer(base, { type: 'remove', index: 3 })
      expect(ids(state)).toBe('abc')
      expect(state.index).toBe(2)
      expect(state.epoch).toBe(base.epoch)
    })

    it('of the current track plays the one that followed it', () => {
      const state = queueReducer(base, { type: 'remove', index: 2 })
      expect(ids(state)).toBe('abd')
      expect(state.index).toBe(2)
      expect(state.epoch).toBe(base.epoch + 1)
    })

    it('of the last track while it plays falls back to the one before', () => {
      const state = queueReducer(queueReducer(base, { type: 'skipTo', index: 3 }), { type: 'remove', index: 3 })
      expect(ids(state)).toBe('abc')
      expect(state.index).toBe(2)
    })

    it('of the only track empties the queue', () => {
      const state = queueReducer(playing(a), { type: 'remove', index: 0 })
      expect(state.tracks).toEqual([])
      expect(state.index).toBe(0)
    })

    it('out of range changes nothing', () => {
      expect(queueReducer(base, { type: 'remove', index: 9 })).toBe(base)
      expect(queueReducer(base, { type: 'remove', index: -1 })).toBe(base)
    })
  })

  describe('move', () => {
    const base = run({ type: 'play', tracks: [a, b, c, d, e], index: 2 })

    it('reorders upcoming tracks', () => {
      const state = queueReducer(base, { type: 'move', from: 4, to: 3 })
      expect(ids(state)).toBe('abced')
      expect(state.index).toBe(2)
    })

    it('follows the current track when it is the one moved', () => {
      const state = queueReducer(base, { type: 'move', from: 2, to: 0 })
      expect(ids(state)).toBe('cabde')
      expect(state.index).toBe(0)
      expect(state.epoch).toBe(base.epoch)
    })

    it('keeps the index on the current track when something moves across it', () => {
      const forward = queueReducer(base, { type: 'move', from: 0, to: 4 })
      expect(ids(forward)).toBe('bcdea')
      expect(forward.tracks[forward.index].id).toBe('c')
      const back = queueReducer(base, { type: 'move', from: 4, to: 1 })
      expect(ids(back)).toBe('aebcd')
      expect(back.tracks[back.index].id).toBe('c')
    })

    it('to the same place or out of range changes nothing', () => {
      expect(queueReducer(base, { type: 'move', from: 1, to: 1 })).toBe(base)
      expect(queueReducer(base, { type: 'move', from: 1, to: 5 })).toBe(base)
      expect(queueReducer(base, { type: 'move', from: -1, to: 1 })).toBe(base)
    })
  })

  it('clear drops what is to come and keeps the current track and its history', () => {
    const state = queueReducer(run({ type: 'play', tracks: [a, b, c, d], index: 1 }), { type: 'clear' })
    expect(ids(state)).toBe('ab')
    expect(state.index).toBe(1)
    expect(upcoming(state)).toEqual([])
  })

  it('next and previous move within the queue and restart playback, stopping at the ends', () => {
    const start = playing(a, b)
    const second = queueReducer(start, { type: 'next' })
    expect(second.index).toBe(1)
    expect(second.epoch).toBe(start.epoch + 1)
    expect(queueReducer(second, { type: 'next' })).toBe(second)
    const first = queueReducer(second, { type: 'previous' })
    expect(first.index).toBe(0)
    // At the first track, previous starts it again.
    expect(queueReducer(first, { type: 'previous' })).toEqual({ ...first, epoch: first.epoch + 1 })
    expect(queueReducer(emptyQueue, { type: 'previous' })).toBe(emptyQueue)
  })

  it('skipTo jumps to a queued track', () => {
    const state = queueReducer(playing(a, b, c), { type: 'skipTo', index: 2 })
    expect(state.index).toBe(2)
    expect(state.epoch).toBe(2)
    expect(queueReducer(state, { type: 'skipTo', index: 3 })).toBe(state)
  })

  it('restore fills an untouched queue without asking for playback, and is ignored once something has played', () => {
    const restored = queueReducer(emptyQueue, { type: 'restore', tracks: [a, b, c], index: 1 })
    expect(ids(restored)).toBe('abc')
    expect(restored.index).toBe(1)
    expect(restored.epoch).toBe(0)
    const played = playing(d)
    expect(queueReducer(played, { type: 'restore', tracks: [a, b], index: 0 })).toBe(played)
    expect(queueReducer(emptyQueue, { type: 'restore', tracks: [], index: 0 })).toBe(emptyQueue)
  })

  it('a restored queue plays from its current track when asked', () => {
    const restored = queueReducer(emptyQueue, { type: 'restore', tracks: [a, b, c], index: 1 })
    const state = queueReducer(restored, { type: 'enqueue', tracks: [d] })
    expect(ids(state)).toBe('abcd')
    expect(state.epoch).toBe(0)
    expect(queueReducer(restored, { type: 'next' })).toMatchObject({ index: 2, epoch: 1 })
  })

  it('stop empties the queue', () => {
    const state = queueReducer(playing(a, b), { type: 'stop' })
    expect(state.tracks).toEqual([])
    expect(state.index).toBe(0)
  })

  it('upcoming lists the tracks after the current one with their places in the queue', () => {
    const state = run({ type: 'play', tracks: [a, b, c], index: 1 })
    expect(upcoming(state).map((u) => [u.track.id, u.index])).toEqual([['c', 2]])
  })
})

describe('queue storage', () => {
  it('serializes ids, the albums they came from, the index and a whole-second position', () => {
    const state = run({ type: 'play', tracks: [a, b, track('x', 'album-2')], index: 1 })
    expect(serializeQueue(state, 83.7)).toEqual({ ids: ['a', 'b', 'x'], albums: ['album-1', 'album-2'], index: 1, position: 83 })
  })

  it('reads back what it wrote and rejects anything else', () => {
    const stored = serializeQueue(playing(a, b), 10)
    expect(readStoredQueue(JSON.stringify(stored))).toEqual(stored)
    expect(readStoredQueue(null)).toBeNull()
    expect(readStoredQueue('not json')).toBeNull()
    expect(readStoredQueue(JSON.stringify({ ids: [] }))).toBeNull()
    expect(readStoredQueue(JSON.stringify({ ids: [1, 2] }))).toBeNull()
    expect(readStoredQueue(JSON.stringify({ ids: ['a'] }))).toEqual({ ids: ['a'], albums: [], index: 0, position: 0 })
  })

  it('rebuilds the queue in order, dropping tracks that are gone and keeping the index on the same track', () => {
    const byId = new Map([a, c, d].map((t) => [t.id, t]))
    const rebuilt = rebuildQueue({ ids: ['a', 'b', 'c', 'd'], albums: [], index: 2, position: 0 }, byId)
    expect(rebuilt.tracks.map((t) => t.id)).toEqual(['a', 'c', 'd'])
    expect(rebuilt.tracks[rebuilt.index].id).toBe('c')
  })

  it('moves to the following track when the current one is gone', () => {
    const byId = new Map([a, c].map((t) => [t.id, t]))
    const rebuilt = rebuildQueue({ ids: ['a', 'b', 'c'], albums: [], index: 1, position: 0 }, byId)
    expect(rebuilt.tracks[rebuilt.index].id).toBe('c')
    const last = rebuildQueue({ ids: ['a', 'b'], albums: [], index: 1, position: 0 }, byId)
    expect(last.tracks[last.index].id).toBe('a')
  })
})
