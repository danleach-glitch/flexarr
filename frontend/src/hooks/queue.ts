import type { ItemSummary } from '../api/types'

/**
 * The play queue, kept as plain data so its rules can be tested without an <audio> element: the tracks in order,
 * which one is current, and `epoch`, which the provider watches to know when to (re)start playback.
 */
export interface QueueState {
  tracks: ItemSummary[]
  index: number
  /**
   * Bumped whenever the track at `index` should start from the beginning. Stays 0 for a queue restored after a
   * reload, which waits for a press of Play: browsers block sound that nobody asked for.
   */
  epoch: number
}

export type QueueAction =
  /** Replaces the queue and starts at `index`. */
  | { type: 'play'; tracks: ItemSummary[]; index?: number }
  /** Adds to the end. With nothing queued, plays. */
  | { type: 'enqueue'; tracks: ItemSummary[] }
  /** Slots in right after the current track. With nothing queued, plays. */
  | { type: 'playNext'; tracks: ItemSummary[] }
  | { type: 'remove'; index: number }
  | { type: 'move'; from: number; to: number }
  /** Drops what's still to come; the current track plays on. */
  | { type: 'clear' }
  | { type: 'skipTo'; index: number }
  | { type: 'next' }
  /** The player has already moved on to the next track by itself (gapless): the index follows, playback is left alone. */
  | { type: 'advance' }
  | { type: 'previous' }
  /** A queue read back from storage, shown paused. Ignored once something has been asked to play. */
  | { type: 'restore'; tracks: ItemSummary[]; index: number }
  | { type: 'stop' }

export const emptyQueue: QueueState = { tracks: [], index: 0, epoch: 0 }

const clamp = (index: number, length: number) => Math.max(0, Math.min(index, length - 1))

/** Every entry is its own object, so the same track queued twice is two distinct rows. */
const copies = (tracks: ItemSummary[]) => tracks.map((t) => ({ ...t }))

export function queueReducer(state: QueueState, action: QueueAction): QueueState {
  switch (action.type) {
    case 'play': {
      const tracks = copies(action.tracks)
      return { tracks, index: clamp(action.index ?? 0, tracks.length), epoch: state.epoch + 1 }
    }
    case 'enqueue':
      if (action.tracks.length === 0) return state
      if (state.tracks.length === 0) return queueReducer(state, { type: 'play', tracks: action.tracks })
      return { ...state, tracks: [...state.tracks, ...copies(action.tracks)] }
    case 'playNext': {
      if (action.tracks.length === 0) return state
      if (state.tracks.length === 0) return queueReducer(state, { type: 'play', tracks: action.tracks })
      const tracks = [...state.tracks]
      tracks.splice(state.index + 1, 0, ...copies(action.tracks))
      return { ...state, tracks }
    }
    case 'remove': {
      const { index } = action
      if (index < 0 || index >= state.tracks.length) return state
      const tracks = state.tracks.filter((_, i) => i !== index)
      if (index < state.index) return { ...state, tracks, index: state.index - 1 }
      if (index > state.index) return { ...state, tracks }
      // The current track went: whatever followed it takes over.
      return { tracks, index: clamp(index, tracks.length), epoch: state.epoch + 1 }
    }
    case 'move': {
      const { from, to } = action
      const { length } = state.tracks
      if (from === to || from < 0 || to < 0 || from >= length || to >= length) return state
      const tracks = [...state.tracks]
      const [moved] = tracks.splice(from, 1)
      tracks.splice(to, 0, moved)
      let index = state.index
      if (from === index) index = to
      else if (from < index && to >= index) index -= 1
      else if (from > index && to <= index) index += 1
      return { ...state, tracks, index }
    }
    case 'clear':
      if (state.tracks.length <= state.index + 1) return state
      return { ...state, tracks: state.tracks.slice(0, state.index + 1) }
    case 'skipTo':
      if (action.index < 0 || action.index >= state.tracks.length) return state
      return { ...state, index: action.index, epoch: state.epoch + 1 }
    case 'next':
      if (state.index + 1 >= state.tracks.length) return state
      return { ...state, index: state.index + 1, epoch: state.epoch + 1 }
    case 'advance':
      if (state.index + 1 >= state.tracks.length) return state
      return { ...state, index: state.index + 1 }
    case 'previous':
      if (state.tracks.length === 0) return state
      return { ...state, index: Math.max(0, state.index - 1), epoch: state.epoch + 1 }
    case 'restore':
      if (state.epoch > 0 || state.tracks.length > 0 || action.tracks.length === 0) return state
      return { tracks: copies(action.tracks), index: clamp(action.index, action.tracks.length), epoch: 0 }
    case 'stop':
      return { ...emptyQueue, epoch: state.epoch }
  }
}

/** What's still to come after the current track, with each entry's place in the queue. */
export function upcoming(state: QueueState): { track: ItemSummary; index: number }[] {
  return state.tracks.slice(state.index + 1).map((track, i) => ({ track, index: state.index + 1 + i }))
}

/** The shape kept in sessionStorage: ids, not whole tracks, which are fetched again on load. */
export interface StoredQueue {
  ids: string[]
  /** The albums the tracks belong to: one request each rebuilds the whole queue. */
  albums: string[]
  index: number
  position: number
}

export const queueStorageKey = 'flexarr.musicQueue'

export function serializeQueue(state: QueueState, position: number): StoredQueue {
  return {
    ids: state.tracks.map((t) => t.id),
    // Only tracks come back by album. A podcast's episodes are fetched one by one (the whole show would be hundreds),
    // and so are books, which have no album.
    albums: [...new Set(state.tracks.filter((t) => t.kind === 'Track').map((t) => t.parentId).filter((id): id is string => id !== null))],
    index: state.index,
    position: Math.max(0, Math.floor(position)),
  }
}

export function readStoredQueue(raw: string | null): StoredQueue | null {
  if (!raw) return null
  try {
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed !== 'object' || parsed === null) return null
    const { ids, albums, index, position } = parsed as Record<string, unknown>
    if (!Array.isArray(ids) || ids.length === 0 || !ids.every((id) => typeof id === 'string')) return null
    return {
      ids,
      albums: Array.isArray(albums) ? albums.filter((a): a is string => typeof a === 'string') : [],
      index: typeof index === 'number' ? index : 0,
      position: typeof position === 'number' ? position : 0,
    }
  } catch {
    return null
  }
}

/**
 * Puts fetched tracks back in their stored order. Ids that no longer exist are dropped and the index stays on the same
 * track, or moves to the one after it when that's the one that went.
 */
export function rebuildQueue(stored: StoredQueue, byId: Map<string, ItemSummary>): { tracks: ItemSummary[]; index: number } {
  const tracks: ItemSummary[] = []
  let index = 0
  stored.ids.forEach((id, i) => {
    if (i === stored.index) index = tracks.length
    const track = byId.get(id)
    if (track) tracks.push(track)
  })
  return { tracks, index: clamp(index, tracks.length) }
}
