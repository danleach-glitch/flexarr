import type { MusicRequested } from '../api/client'

/** Where an album Lidarr is waiting for stands, in a few words. */
export function requestedState(r: MusicRequested, now = new Date()): string {
  switch (r.state) {
    case 'downloading':
      return r.progress != null && r.progress > 0 ? `Downloading ${Math.round(r.progress * 100)}%` : 'Downloading'
    case 'importing':
      return 'Importing'
    case 'upcoming': {
      if (!r.releaseDate) return 'Coming soon'
      const date = new Date(`${r.releaseDate}T00:00:00`)
      const sameYear = date.getFullYear() === now.getFullYear()
      return `Out ${date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) })}`
    }
    default:
      return 'Searching'
  }
}
