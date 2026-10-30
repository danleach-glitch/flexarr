import { useState } from 'react'
import { addToWatchlist, ApiError, removeFromWatchlist, type WatchlistKey } from '../api/client'
import { AsyncButton } from './AsyncButton'
import { Bookmark } from './icons'

/**
 * Puts a title on the profile's watchlist, or takes it off. `compact` is the poster card's icon-only button.
 * `onRequested` hears when adding it also requested it, so the page can show it's on its way.
 */
export function WatchlistButton({ titleKey, title, on, compact = false, onRequested }: {
  titleKey: WatchlistKey
  title: string
  on: boolean
  compact?: boolean
  onRequested?: () => void
}) {
  const [onList, setOnList] = useState(on)
  const [message, setMessage] = useState<string>()

  async function toggle() {
    setMessage(undefined)
    try {
      if (onList) {
        await removeFromWatchlist(titleKey)
        setOnList(false)
      } else {
        const { requested } = await addToWatchlist(titleKey)
        setOnList(true)
        if (requested) onRequested?.()
      }
    } catch (e) {
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'That didn’t work. Try again.')
    }
  }

  const label = onList ? `Remove ${title} from your watchlist` : `Add ${title} to your watchlist`
  return (
    <>
      <AsyncButton
        className={compact ? 'icon-button watchlist-toggle' : 'button quiet'}
        aria-pressed={onList}
        aria-label={compact ? label : undefined}
        title={message ?? (compact ? label : undefined)}
        onClick={toggle}
      >
        <Bookmark filled={onList} />
        {!compact && (onList ? ' On watchlist' : ' Watchlist')}
      </AsyncButton>
      {!compact && message && (
        <span className="error" role="alert">
          {message}
        </span>
      )}
    </>
  )
}
