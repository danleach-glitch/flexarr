import type { MusicRequested, TitleRequested } from '../api/client'
import { locale, t, Words } from '../i18n'

/** Where an album Lidarr (or a film or show Seerr) is waiting for stands, in a few words. */
export function requestedState(r: Pick<MusicRequested | TitleRequested, 'state' | 'progress' | 'releaseDate'>, now = new Date()): string {
  switch (r.state) {
    case 'downloading':
      return r.progress != null && r.progress > 0 ? t('Downloading, {percent}%', { percent: Math.round(r.progress * 100) }) : t('Downloading')
    case 'importing':
      return t('Importing')
    case 'upcoming': {
      if (!r.releaseDate) return t(Words.comingSoon)
      const date = new Date(`${r.releaseDate}T00:00:00`)
      const sameYear = date.getFullYear() === now.getFullYear()
      return t('Out {date}', { date: date.toLocaleDateString(locale(), { day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) }) })
    }
    default:
      return t('Searching')
  }
}
