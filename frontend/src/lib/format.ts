import type { ItemSummary, LibraryType, MediaFile, VideoRange } from '../api/types'

export function formatDuration(seconds: number | null | undefined): string | null {
  if (!seconds) return null
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

/** "1:02:05" or "4:07" for the player. */
export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

export function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`
}

export function formatBitrate(bitsPerSecond: number | null): string | null {
  if (!bitsPerSecond) return null
  return bitsPerSecond >= 1_000_000 ? `${(bitsPerSecond / 1_000_000).toFixed(1)} Mbps` : `${Math.round(bitsPerSecond / 1000)} kbps`
}

export function formatChannels(channels: number | null): string | null {
  if (!channels) return null
  return { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }[channels] ?? `${channels} channels`
}


type EpisodeNumbers = Pick<ItemSummary, 'seasonNumber' | 'episodeNumber' | 'episodeNumberEnd' | 'airDate'> & { homeVideo?: boolean; title?: string }

/** "S1E6", "S1E6–E7", or the air date for daily shows. Nothing for a home video, which goes by its name. */
export function episodeCode(item: EpisodeNumbers): string {
  if (item.homeVideo) return ''
  if (item.airDate) return item.airDate
  const code = `S${item.seasonNumber ?? 0}E${item.episodeNumber ?? 0}`
  return item.episodeNumberEnd ? `${code}–E${item.episodeNumberEnd}` : code
}

/** "Season 1, Episode 10" for buttons and headings on a show's pages (dated shows give the date; a home video its name). */
export function episodeHeading(item: EpisodeNumbers): string {
  if (item.homeVideo) return item.title ?? ''
  if (item.airDate) return new Date(item.airDate).toLocaleDateString(undefined, { dateStyle: 'long' })
  const season = item.seasonNumber === 0 ? 'Specials' : `Season ${item.seasonNumber ?? 0}`
  const episode = item.episodeNumberEnd ? `episodes ${item.episodeNumber ?? 0}–${item.episodeNumberEnd}` : `Episode ${item.episodeNumber ?? 0}`
  return `${season}, ${episode.replace(/^episodes/, 'Episodes')}`
}

const rangeLabel: Record<VideoRange, string | null> = {
  Sdr: null,
  Hdr10: 'HDR10',
  Hlg: 'HLG',
  DolbyVision: 'Dolby Vision',
}

/** "HDR10+", "Dolby Vision, HDR10+"; null for SDR. Servers before HDR10+ was looked for leave it out. */
export function pictureLabel(file: Pick<MediaFile, 'videoRange' | 'hdr10Plus'>): string | null {
  if (!file.hdr10Plus) return rangeLabel[file.videoRange] ?? null
  return file.videoRange === 'DolbyVision' ? 'Dolby Vision, HDR10+' : 'HDR10+'
}

export function relativeTime(iso: string | null): string {
  if (!iso) return 'never'
  const seconds = (Date.now() - new Date(iso).getTime()) / 1000
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`
  return new Date(iso).toLocaleDateString()
}

export function year(item: Pick<ItemSummary, 'year' | 'releaseDate'>): number | null {
  return item.year ?? (item.releaseDate ? new Date(item.releaseDate).getFullYear() : null)
}

/** Progress through an item from 0 to 1, or null when it hasn't been started. */
export function progressOf(item: ItemSummary): number | null {
  const duration = item.durationSeconds ?? (item.runtimeMinutes ? item.runtimeMinutes * 60 : null)
  if (!duration || item.userData.positionSeconds <= 0) return null
  return Math.min(1, item.userData.positionSeconds / duration)
}

export const libraryTypeLabel: Record<LibraryType, string> = {
  Movies: 'Movies',
  TvShows: 'TV Shows',
  Music: 'Music',
  Podcasts: 'Podcasts',
  HomeVideos: 'Home Videos',
  Audiobooks: 'Audiobooks',
}

/** What an episode is called where it is: a home video's is just its name. */
export const episodeName = (item: ItemSummary) => (item.homeVideo || !item.seriesTitle ? item.title : `${item.seriesTitle} ${episodeCode(item)}`)

/**
 * Why a file can't be read, to follow "Can’t be read: ": the last part of the error, in its own case, with the first
 * letter lowercased only when it starts an ordinary word ("Invalid data…" but not "EBML…"), so names such as Audible
 * keep their capital further on.
 */
export function unreadableReason(error: string): string {
  const reason = error.split(': ').at(-1) ?? error
  return /^[A-Z][a-z]/.test(reason) ? reason[0].toLowerCase() + reason.slice(1) : reason
}
