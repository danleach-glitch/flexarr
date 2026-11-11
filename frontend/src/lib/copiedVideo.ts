import { prepareStream } from '../api/client'
import type { DeviceProfile, PlaybackInfo, PlaybackRequest } from '../api/types'
import { detectProfile } from './profile'

/**
 * A film or episode the browser can't open as it is (an MKV, say) used to have its picture converted to H.264 even
 * when the browser plays the file's own H.264: only the Apple and Android apps, which take streams in fragmented MP4,
 * got it with the picture copied. The web player now asks for fragmented MP4 too, so an H.264 picture is copied as it
 * is, with these limits:
 *
 * - HEVC (and Dolby Vision, which rides on it) stays converted for now: a stream that comes back with its HEVC copied
 *   is asked for again the way it was before.
 * - The sound stays as it was: with fragmented MP4 the server would copy FLAC and ALAC and turn surround into FLAC, so
 *   those two are left out of what the browser says it plays.
 * - A title whose copied stream fails to play is asked for the old way for a week, and every title is once two
 *   different titles have failed (`rememberCopyingFailed`).
 */

/**
 * The copied streams that failed to play in this browser, as JSON `{ [itemId]: time }`. (Before, it held a lone time:
 * read as one failure on an unknown title.)
 */
export const COPY_FAILED_KEY = 'flexarr.copiedVideoFailed'
/** How long a failure counts: a browser update or a newer server may well play the stream later. */
export const COPY_FAILED_FOR_MS = 7 * 24 * 60 * 60 * 1000
/** Failures on this many different titles turn copying off for every title; one failure only for its own title. */
const TITLES_TO_TURN_OFF = 2

/** Failures this visit, kept apart from the browser's storage (which may keep nothing). */
const failedHere = new Map<string, number>()
/** Titles whose stream would copy HEVC: asked for the old way straight away next time (another audio track, a recovery). */
const hevcTitles = new Set<string>()
/** The streams asked for in fragmented MP4. */
const sentFmp4 = new WeakSet<PlaybackInfo>()

function storedFailures(): [string, number][] {
  try {
    const raw = localStorage.getItem(COPY_FAILED_KEY)
    if (raw === null) return []
    const parsed: unknown = JSON.parse(raw)
    if (typeof parsed === 'number') return [['', parsed]]
    if (parsed && typeof parsed === 'object') {
      return Object.entries(parsed).filter((entry): entry is [string, number] => typeof entry[1] === 'number')
    }
  } catch {
    // Unreadable or blocked: nothing remembered.
  }
  return []
}

/** The titles whose copied stream failed within the last week, with when. */
function recentFailures(now = Date.now()): Map<string, number> {
  const all = new Map(storedFailures())
  for (const [id, at] of failedHere) all.set(id, Math.max(at, all.get(id) ?? 0))
  for (const [id, at] of all) if (now - at >= COPY_FAILED_FOR_MS) all.delete(id)
  return all
}

/** Whether copying is off here: a copied stream failed within the week on `itemId` itself, or on two different titles. */
export function copyingFailed(itemId?: string): boolean {
  const recent = recentFailures()
  return recent.size >= TITLES_TO_TURN_OFF || (itemId !== undefined && recent.has(itemId))
}

/** A copied stream of `itemId` failed to play here: that title is asked for the old way for a week, and all of them once two have. */
export function rememberCopyingFailed(itemId = '') {
  const now = Date.now()
  failedHere.set(itemId, now)
  try {
    localStorage.setItem(COPY_FAILED_KEY, JSON.stringify(Object.fromEntries(recentFailures(now))))
  } catch {
    // Kept for this visit only.
  }
}

/** What this browser plays, for a film or episode: with fragmented MP4 when `copy`, keeping the sound as before. */
export function videoProfile(copy: boolean): DeviceProfile {
  const profile = detectProfile()
  if (!copy) return profile
  return { ...profile, fmp4: true, audioCodecs: profile.audioCodecs.filter((codec) => codec !== 'flac' && codec !== 'alac') }
}

/** Whether `info`'s stream was asked for in fragmented MP4 (a failure to play it is then the copying's, maybe). */
export function wasCopyAsked(info: PlaybackInfo): boolean {
  return sentFmp4.has(info)
}

/** The server's summary of a repackaged stream starts "Original HEVC" or "Original H.264". */
function copiesHevc(info: PlaybackInfo): boolean {
  return info.method === 'Transcode' && /^Original HEVC\b/.test(info.transcodeSummary ?? '')
}

/**
 * Starts a film or episode's stream with `start` (startPlayback, or prepareStream to get it ready): with its picture
 * copied when it's H.264 and this browser hasn't failed to play that before, and as before otherwise. `copy: false`
 * asks the old way (a copied stream that just failed). A profile in `request` is replaced.
 */
export async function startVideo(
  request: Omit<PlaybackRequest, 'profile'> & { profile?: DeviceProfile },
  start: (request: PlaybackRequest) => Promise<PlaybackInfo>,
  copy = true,
): Promise<PlaybackInfo> {
  const asking = copy && !copyingFailed(request.itemId) && !hevcTitles.has(request.itemId)
  const info = await start({ ...request, profile: videoProfile(asking) })
  if (!asking) return info
  if (copiesHevc(info)) {
    // The same stream as before, in place of this one.
    hevcTitles.add(info.item.id)
    return start({ ...request, profile: videoProfile(false), replacesSessionId: info.sessionId })
  }
  sentFmp4.add(info)
  return info
}

/** Gets a film or episode ready ahead of Play (lib/preparedPlayback's `start`), as `startVideo` would start it. */
export const prepareVideo = (request: PlaybackRequest) => startVideo(request, prepareStream)
