import type { AlbumType } from '../api/types'

/** What an album's page calls its kind of release: nothing for a plain album, or one nobody has typed. */
export function albumTypeLabel(type: AlbumType | null | undefined): string | null {
  switch (type) {
    case 'ep':
      return 'EP'
    case 'single':
      return 'Single'
    case 'live':
      return 'Live'
    case 'compilation':
      return 'Compilation'
    case 'soundtrack':
      return 'Soundtrack'
    default:
      return null
  }
}
