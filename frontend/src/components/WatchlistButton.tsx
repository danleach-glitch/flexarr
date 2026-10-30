import { useState } from 'react'
import { addToWatchlist, ApiError, removeFromWatchlist, type WatchlistKey } from '../api/client'
import { AsyncButton } from './AsyncButton'
import { Bookmark } from './icons'
import { showNotice } from '../lib/notice'

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
  // What was pressed here, until the page says otherwise (it reloads after Mark as watched, say).
  const [pressed, setPressed] = useState<{ from: boolean; to: boolean }>()
  const onList = pressed && pressed.from === on ? pressed.to : on
  const [message, setMessage] = useState<string>()

  async function toggle() {
    setMessage(undefined)
    try {
      if (onList) {
        await removeFromWatchlist(titleKey).catch((e: unknown) => {
          // Already off (it left when it was watched): that's what was asked.
          if (!(e instanceof ApiError && e.status === 404)) throw e
        })
        setPressed({ from: on, to: false })
      } else {
        const { requested } = await addToWatchlist(titleKey)
        setPressed({ from: on, to: true })
        if (requested) onRequested?.()
      }
    } catch (e) {
      const text = e instanceof ApiError && e.messages[0] ? e.messages[0] : 'That didn’t work. Try again.'
      if (compact) showNotice(text)
      else setMessage(text)
    }
  }

  const label = onList ? `Remove ${title} from your watchlist` : `Add ${title} to your watchlist`
  return (
    <>
      <AsyncButton
        className={compact ? 'icon-button watchlist-toggle' : 'button quiet'}
        aria-pressed={onList}
        aria-label={compact ? label : undefined}
        title={compact ? label : undefined}
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
