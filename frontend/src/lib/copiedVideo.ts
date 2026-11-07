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
 * - A browser that fails to play such a stream is asked the old way from then on (`rememberCopyingFailed`).
 */

/** Set in this browser once a copied stream failed to play in it. */
export const COPY_FAILED_KEY = 'flexarr.copiedVideoFailed'

let failedHere = false
/** Titles whose stream would copy HEVC: asked for the old way straight away next time (another audio track, a recovery). */
const hevcTitles = new Set<string>()
/** The streams asked for in fragmented MP4. */
const sentFmp4 = new WeakSet<PlaybackInfo>()

/** Whether a copied stream has failed to play in this browser before. */
export function copyingFailed(): boolean {
  if (failedHere) return true
  try {
    return localStorage.getItem(COPY_FAILED_KEY) !== null
  } catch {
    return false
  }
}

/** A copied stream failed to play here: films and episodes are asked for the old way from now on. */
export function rememberCopyingFailed() {
  failedHere = true
  try {
    localStorage.setItem(COPY_FAILED_KEY, String(Date.now()))
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
  const asking = copy && !copyingFailed() && !hevcTitles.has(request.itemId)
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
