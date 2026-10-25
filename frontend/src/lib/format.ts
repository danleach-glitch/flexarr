import type { ItemSummary, VideoRange } from '../api/types'

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

const pad = (n: number) => String(n).padStart(2, '0')

/** "S01E06", "S01E06–E07", or the air date for daily shows. */
export function episodeCode(item: Pick<ItemSummary, 'seasonNumber' | 'episodeNumber' | 'episodeNumberEnd' | 'airDate'>): string {
  if (item.airDate) return item.airDate
  const code = `S${pad(item.seasonNumber ?? 0)}E${pad(item.episodeNumber ?? 0)}`
  return item.episodeNumberEnd ? `${code}–E${pad(item.episodeNumberEnd)}` : code
}

/** "Season 01 | Episode 10" for buttons and headings on a show's pages (dated shows give the date). */
export function episodeHeading(item: Pick<ItemSummary, 'seasonNumber' | 'episodeNumber' | 'episodeNumberEnd' | 'airDate'>): string {
  if (item.airDate) return new Date(item.airDate).toLocaleDateString(undefined, { dateStyle: 'long' })
  const season = item.seasonNumber === 0 ? 'Specials' : `Season ${pad(item.seasonNumber ?? 0)}`
  const episode = item.episodeNumberEnd ? `Episodes ${pad(item.episodeNumber ?? 0)}–${pad(item.episodeNumberEnd)}` : `Episode ${pad(item.episodeNumber ?? 0)}`
  return `${season} | ${episode}`
}

export const rangeLabel: Record<VideoRange, string | null> = {
  Sdr: null,
  Hdr10: 'HDR10',
  Hlg: 'HLG',
  DolbyVision: 'Dolby Vision',
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

export const libraryTypeLabel = { Movies: 'Movies', TvShows: 'TV Shows', Music: 'Music', Podcasts: 'Podcasts' } as const
