import type { ItemSummary, PodcastEpisode } from '../api/types'
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
