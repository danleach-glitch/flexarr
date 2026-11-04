import { Star } from './icons'
import { t } from '../i18n'

/** Stars a channel for this profile, or unstars it. */
export function FavouriteButton({ name, favourite, onToggle, className }: { name: string; favourite: boolean; onToggle: () => void; className?: string }) {
  return (
    <button
      className={['livetv-star', favourite && 'on', className].filter(Boolean).join(' ')}
      aria-pressed={favourite}
      aria-label={favourite ? t('Remove {name} from favourites', { name }) : t('Add {name} to favourites', { name })}
      title={favourite ? t('Remove from favourites') : t('Add to favourites')}
      onClick={(e) => {
        e.preventDefault()
        e.stopPropagation()
        onToggle()
      }}
    >
      <Star filled={favourite} />
    </button>
  )
}
