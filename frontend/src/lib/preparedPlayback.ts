import { dropStream, prepareStream, type PlaybackInfo, type PlaybackRequest } from '../api/client'

/**
 * A title got ready ahead of Play (docs/fast-start.md): the next episode during the credits, or a film or episode whose
 * page has been looked at for a moment. One at a time; the player takes it over when Play asks for exactly the same
 * stream, and otherwise it's let go.
 */

/** A warm-up older than this isn't used: its resume point may have moved on meanwhile. */
export const MAX_AGE_MS = 5 * 60_000

/** How long a page is looked at before its title is got ready. */
export const PAGE_DELAY_MS = 2000

/** What has to match for a warm-up to be the stream Play wants. */
export type Wanted = Pick<
  PlaybackRequest,
  'itemId' | 'fileId' | 'startPositionSeconds' | 'shuffle' | 'maxHeight' | 'maxBitrate' | 'forceTranscode' | 'audioStreamIndex' | 'subtitleKey'
>

export function matchKey(r: Wanted): string {
  return JSON.stringify([
    r.itemId,
    r.fileId ?? null,
    r.startPositionSeconds ?? null,
    r.shuffle ?? false,
    r.maxHeight ?? null,
    r.maxBitrate ?? null,
    r.forceTranscode ?? false,
    r.audioStreamIndex ?? null,
    r.subtitleKey ?? null,
  ])
}

interface Slot {
  key: string
  itemId: string
  /** The answer, once it's come. */
  info?: PlaybackInfo
  /** Until it has. */
  answer: Promise<PlaybackInfo | null>
  at: number
  /** The player took it over (perhaps before its answer came). */
  taken?: boolean
}

let slot: Slot | null = null
/** The title Play is opening: the page going away leaves its warm-up for the player. */
let handedTo: string | null = null

/** Starts getting `request` ready, unless that's already under way; anything else got ready is let go. */
export function getReady(request: PlaybackRequest, start: typeof prepareStream = prepareStream) {
  const key = matchKey(request)
  if (slot?.key === key && Date.now() - slot.at < MAX_AGE_MS) return
  drop()
  const mine: Slot = { key, itemId: request.itemId, at: Date.now(), answer: Promise.resolve(null) }
  mine.answer = start(request).then(
    (info) => {
      // No longer wanted (dropped or replaced meanwhile): let it go.
      if (slot !== mine && !mine.taken) {
        void dropStream(info.sessionId)
        return null
      }
      mine.info = info
      return info
    },
    () => {
      // A busy server, say: Play starts the usual way.
      if (slot === mine) slot = null
      return null
    },
  )
  slot = mine
}

/**
 * The warm-up for exactly `wanted`, taken over by the player (waiting for it if it's on its way), or null; any other
 * is let go.
 */
export async function take(wanted: Wanted): Promise<PlaybackInfo | null> {
  handedTo = null
  const mine = slot
  if (!mine || mine.key !== matchKey(wanted) || Date.now() - mine.at > MAX_AGE_MS) {
    drop()
    return null
  }
  slot = null
  mine.taken = true
  return mine.info ?? (await mine.answer)
}

/** Whether `itemId`'s warm-up is held (got ready or on its way). */
export function holds(itemId: string) {
  return slot?.itemId === itemId
}

/** Lets whatever was got ready go (best effort). */
export function drop() {
  const mine = slot
  slot = null
  if (mine?.info) void dropStream(mine.info.sessionId)
  // One still on its way is let go when it answers (see getReady).
}

/** Play is opening `itemId`: its warm-up stays for the player when the page it was got ready on goes. */
export function handOver(itemId: string) {
  handedTo = itemId
}

/** The page that got `itemId` ready is going: its warm-up goes too, unless Play is opening it. */
export function leave(itemId: string) {
  if (handedTo === itemId || slot?.itemId !== itemId) return
  drop()
}

/** For tests. */
export function reset() {
  slot = null
  handedTo = null
}
