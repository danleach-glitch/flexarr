import type { ItemSummary, Loudness } from '../api/types'

/**
 * The linear factor to play a track at so it sits at the ReplayGain reference level: its album gain when it's being
 * heard as part of its album (so the quiet tracks stay quiet relative to the loud ones), otherwise its own. A track whose
 * peak would clip after the gain is held back so peak × factor stays at or below full scale. No tags: unity.
 */
export function replayGainScale(loudness: Loudness | null | undefined, album: boolean, preampDb = 0): number {
  if (!loudness) return 1
  const gain = album ? (loudness.albumGain ?? loudness.trackGain) : (loudness.trackGain ?? loudness.albumGain)
  if (gain === null || gain === undefined || !Number.isFinite(gain)) return 1
  const peak = album ? (loudness.albumPeak ?? loudness.trackPeak) : (loudness.trackPeak ?? loudness.albumPeak)
  const scale = 10 ** ((gain + preampDb) / 20)
  return peak && peak > 0 && scale * peak > 1 ? 1 / peak : scale
}

/**
 * Whether the track at `index` is being heard as part of its album: the one before or after it in the queue is from the
 * same album. Album gain only makes sense then; in a mix of albums each track is levelled on its own.
 */
export function heardAsAlbum(tracks: ItemSummary[], index: number): boolean {
  const track = tracks[index]
  if (!track?.parentId) return false
  return tracks[index - 1]?.parentId === track.parentId || tracks[index + 1]?.parentId === track.parentId
}
