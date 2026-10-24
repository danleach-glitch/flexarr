import type { ItemSummary, VideoRange } from '../api/types'

export function formatDuration(seconds: number | null | undefined): string | null {
  if (!seconds) return null
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
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
  return { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }[channels] ?? `${channels} ch`
}

const pad = (n: number) => String(n).padStart(2, '0')

/** "S01E06", "S01E06–E07", or the air date for daily shows. */
export function episodeCode(item: Pick<ItemSummary, 'seasonNumber' | 'episodeNumber' | 'episodeNumberEnd' | 'airDate'>): string {
  if (item.airDate) return item.airDate
  const code = `S${pad(item.seasonNumber ?? 0)}E${pad(item.episodeNumber ?? 0)}`
  return item.episodeNumberEnd ? `${code}–E${pad(item.episodeNumberEnd)}` : code
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

export const libraryTypeLabel = { Movies: 'Movies', TvShows: 'TV Shows', Music: 'Music' } as const
