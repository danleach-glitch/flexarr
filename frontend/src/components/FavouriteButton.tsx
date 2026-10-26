import { Star } from './icons'

/** Stars a channel for this profile, or unstars it. */
export function FavouriteButton({ name, favourite, onToggle, className }: { name: string; favourite: boolean; onToggle: () => void; className?: string }) {
  return (
    <button
      className={['livetv-star', favourite && 'on', className].filter(Boolean).join(' ')}
      aria-pressed={favourite}
      aria-label={favourite ? `Remove ${name} from favourites` : `Add ${name} to favourites`}
      title={favourite ? 'Remove from favourites' : 'Add to favourites'}
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
