import type { ItemSummary, PodcastChapter, PodcastEpisode } from '../api/types'
import { formatDuration } from './format'

/** An episode as the player's queue holds it: the same shape as a track, its show standing in for the album. */
export function episodeAsTrack(episode: PodcastEpisode): ItemSummary {
  return {
    id: episode.id,
    libraryId: '',
    parentId: episode.podcastId,
    kind: 'PodcastEpisode',
    title: episode.title,
    year: episode.publishedAt ? new Date(episode.publishedAt).getFullYear() : null,
    seasonNumber: episode.season,
    episodeNumber: episode.episode,
    episodeNumberEnd: null,
    airDate: episode.publishedAt,
    childCount: 0,
    durationSeconds: episode.durationSeconds,
    resolution: null,
    addedAt: episode.publishedAt ?? '',
    artwork: episode.artwork,
    accentColor: episode.accentColor,
    communityRating: null,
    criticRating: null,
    contentRating: episode.explicit ? 'Explicit' : null,
    runtimeMinutes: episode.durationSeconds ? Math.round(episode.durationSeconds / 60) : null,
    releaseDate: null,
    userData: episode.userData,
    seriesTitle: episode.podcastTitle,
    discNumber: null,
    trackNumber: null,
    seriesId: episode.podcastId,
  }
}

/** "Today", "Yesterday", "Tue", or "3 Sep" (with the year when it isn't this one), as podcast apps date episodes. */
export function episodeDate(iso: string | null, now = new Date()): string {
  if (!iso) return ''
  const date = new Date(iso)
  const startOf = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const days = Math.round((startOf(now) - startOf(date)) / 86_400_000)
  if (days === 0) return 'Today'
  if (days === 1) return 'Yesterday'
  if (days > 1 && days < 7) return date.toLocaleDateString(undefined, { weekday: 'long' })
  return date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric' })
}

/** "52m", "1h 4m", or how much is left once it's started ("23m left"); "Played" once it's done. */
export function episodeLength(episode: Pick<PodcastEpisode, 'durationSeconds' | 'userData'>): string {
  if (episode.userData.watched) return 'Played'
  const total = episode.durationSeconds ?? 0
  const at = episode.userData.positionSeconds
  if (total > 0 && at > 0) return `${formatDuration(Math.max(60, total - at))} left`
  return formatDuration(total) ?? ''
}

/** How far through an episode is, 0 to 1, or null when it hasn't been started (or has been finished). */
export function episodeProgress(episode: Pick<PodcastEpisode, 'durationSeconds' | 'userData'>): number | null {
  if (episode.userData.watched || !episode.durationSeconds || episode.userData.positionSeconds <= 0) return null
  return Math.min(1, episode.userData.positionSeconds / episode.durationSeconds)
}

/** Whether what was typed into search is a feed's address rather than words to look for. */
export const looksLikeFeedUrl = (text: string) => /^https?:\/\/\S+\.\S+/i.test(text.trim())

/** "1.5×", "1×". */
export const speedLabel = (speed: number) => `${Number(speed.toFixed(2))}×`

/** Fired on window when podcast settings are saved, so the sidebar can follow. */
export const PODCAST_PREFERENCES_CHANGED = 'flexarr:podcast-preferences'

/** A piece of an episode's notes: plain text, a web address, or a time in the episode ("12:34", "1:02:55"). */
export type NotesPart = { text: string } | { url: string } | { time: number; text: string }

/**
 * An episode's notes in pieces, so web addresses can be links and times can jump there. Times past `duration` (when
 * it's known) stay plain text.
 */
export function notesParts(notes: string, duration?: number | null): NotesPart[] {
  const parts: NotesPart[] = []
  const pattern = /(https?:\/\/[^\s<>"')\]]+)|(?<![\d:])((?:\d{1,2}:)?\d{1,2}:[0-5]\d)(?![\d:])/g
  let last = 0
  for (const match of notes.matchAll(pattern)) {
    const at = match.index
    const time = match[2] ? clockSeconds(match[2]) : null
    if (match[2] && (time === null || (duration && time >= duration))) continue
    if (at > last) parts.push({ text: notes.slice(last, at) })
    parts.push(match[1] ? { url: match[1] } : { time: time!, text: match[2] })
    last = at + match[0].length
  }
  if (last < notes.length) parts.push({ text: notes.slice(last) })
  return parts
}

/** "52:50" or "1:02:55" in seconds; null when it isn't a time. */
export function clockSeconds(text: string): number | null {
  const parts = text.split(':').map(Number)
  if (parts.length < 2 || parts.length > 3 || parts.some((p) => !Number.isInteger(p)) || parts.slice(1).some((p) => p > 59)) return null
  return parts.reduce((total, p) => total * 60 + p, 0)
}

/** Which chapter is playing at `position`: its place in the list, or null before the first starts. */
export function chapterAt(chapters: PodcastChapter[], position: number): number | null {
  let found: number | null = null
  chapters.forEach((c, i) => {
    if (c.startSeconds <= position + 0.5) found = i
  })
  return found
}

/** Where "previous chapter" goes: the start of this chapter, or (near its start already) the one before. */
export function previousChapterStart(chapters: PodcastChapter[], position: number): number {
  const current = chapterAt(chapters, position)
  if (current === null) return 0
  const start = chapters[current].startSeconds
  return position - start > 3 || current === 0 ? start : chapters[current - 1].startSeconds
}
