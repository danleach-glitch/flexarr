import bazarr from '../assets/apps/bazarr.png'
import dispatcharr from '../assets/apps/dispatcharr.png'
import lidarr from '../assets/apps/lidarr.svg'
import radarr from '../assets/apps/radarr.svg'
import seerr from '../assets/apps/seerr.svg'
import sonarr from '../assets/apps/sonarr.svg'
import type { ArrKind, MediaKind } from '../api/types'

/**
 * Each app's logo, from its own repository (GPL-3.0 for the *arr apps and Bazarr, MIT for Seerr, Dispatcharr's from its web app).
 * Radarr's is the one its dark theme uses.
 */
export const appLogos: Record<ArrKind | 'Seerr' | 'Bazarr' | 'Dispatcharr', string> = {
  Sonarr: sonarr,
  Radarr: radarr,
  Lidarr: lidarr,
  Seerr: seerr,
  Bazarr: bazarr,
  Dispatcharr: dispatcharr,
}

/** The app that manages a kind of title: films are Radarr's, music Lidarr's, and shows Sonarr's. No app manages books. */
export function arrFor(kind: MediaKind): ArrKind | undefined {
  switch (kind) {
    case 'Movie':
      return 'Radarr'
    case 'Audiobook':
      return undefined
    case 'Artist':
    case 'Album':
    case 'Track':
      return 'Lidarr'
    default:
      return 'Sonarr'
  }
}
