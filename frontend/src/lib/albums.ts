import type { AlbumType } from '../api/types'
import { t } from '../i18n'

/** What an album's page calls its kind of release: nothing for a plain album, or one nobody has typed. */
export function albumTypeLabel(type: AlbumType | null | undefined): string | null {
  switch (type) {
    case 'ep':
      return 'EP'
    case 'single':
      return t('Single')
    case 'live':
      return t('Live', { ctx: 'album-type' })
    case 'compilation':
      return t('Compilation')
    case 'soundtrack':
      return t('Soundtrack')
    default:
      return null
  }
}
