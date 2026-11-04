import type { ItemSummary, LibraryType, MediaFile, VideoRange } from '../api/types'
import { locale, t, Words } from '../i18n'

export function formatDuration(seconds: number | null | undefined): string | null {
  if (!seconds) return null
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return t('{minutes}m', { minutes })
  return t('{hours}h {minutes}m', { hours: Math.floor(minutes / 60), minutes: minutes % 60 })
}

/** "1:02:05" or "4:07" for the player. */
export function formatClock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

/** A number in the language shown, with a fixed number of decimals (1.5, or 1,5 in German). */
export function formatDecimal(value: number, digits: number): string {
  return value.toLocaleString(locale(), { minimumFractionDigits: digits, maximumFractionDigits: digits })
}

export function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return t('{value} {unit}', { value: formatDecimal(value, value >= 100 || unit === 0 ? 0 : 1), unit: units[unit] })
}

export function formatBitrate(bitsPerSecond: number | null): string | null {
  if (!bitsPerSecond) return null
  return bitsPerSecond >= 1_000_000
    ? t('{value} Mbps', { value: formatDecimal(bitsPerSecond / 1_000_000, 1) })
    : t('{value} kbps', { value: Math.round(bitsPerSecond / 1000) })
}

export function formatChannels(channels: number | null): string | null {
  if (!channels) return null
  if (channels === 1) return t('Mono')
  if (channels === 2) return t('Stereo')
  return { 6: '5.1', 8: '7.1' }[channels] ?? t('{channels} channels', { channels })
}


type EpisodeNumbers = Pick<ItemSummary, 'seasonNumber' | 'episodeNumber' | 'episodeNumberEnd' | 'airDate'> & { homeVideo?: boolean; title?: string }

/** "S1E6", "S1E6–E7", or the air date for daily shows. Nothing for a home video, which goes by its name. */
export function episodeCode(item: EpisodeNumbers): string {
  if (item.homeVideo) return ''
  if (item.airDate) return item.airDate
  const season = item.seasonNumber ?? 0
  const episode = item.episodeNumber ?? 0
  return item.episodeNumberEnd
    ? t('S{season}E{episode}–E{last}', { season: String(season), episode: String(episode), last: String(item.episodeNumberEnd) })
    : t(Words.seasonEpisodeShort, { season: String(season), episode: String(episode) })
}

/** "Season 1, Episode 10" for buttons and headings on a show's pages (dated shows give the date; a home video its name). */
export function episodeHeading(item: EpisodeNumbers): string {
  if (item.homeVideo) return item.title ?? ''
  if (item.airDate) return new Date(item.airDate).toLocaleDateString(locale(), { dateStyle: 'long' })
  const season = String(item.seasonNumber ?? 0)
  const episode = String(item.episodeNumber ?? 0)
  const last = String(item.episodeNumberEnd ?? '')
  if (item.seasonNumber === 0)
    return item.episodeNumberEnd ? t('Specials, Episodes {episode}–{last}', { episode, last }) : t('Specials, Episode {episode}', { episode })
  return item.episodeNumberEnd
    ? t('Season {season}, Episodes {episode}–{last}', { season, episode, last })
    : t(Words.seasonEpisodeHeading, { season, episode })
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
  if (!iso) return t('never')
  const seconds = (Date.now() - new Date(iso).getTime()) / 1000
  if (seconds < 60) return t('just now')
  if (seconds < 3600) return t('{minutes} min ago', { minutes: Math.floor(seconds / 60) })
  if (seconds < 86400) return t('{hours} h ago', { hours: Math.floor(seconds / 3600) })
  return new Date(iso).toLocaleDateString(locale())
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

/** Each kind of library's name, in the language shown when it's read. */
export const libraryTypeLabel: Record<LibraryType, string> = {
  get Movies() {
    return t(Words.movies)
  },
  get TvShows() {
    return t('TV Shows')
  },
  get Music() {
    return t(Words.music)
  },
  get Podcasts() {
    return t(Words.podcasts)
  },
  get HomeVideos() {
    return t('Home Videos')
  },
  get Audiobooks() {
    return t(Words.audiobooks)
  },
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

/** "12 Nov", or "12 Nov 2021" when it isn't this year. */
export function airDay(iso: string, now = new Date()): string {
  const date = new Date(iso)
  return date.toLocaleDateString(locale(), { day: 'numeric', month: 'short', year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric' })
}
