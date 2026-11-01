import { useState } from 'react'
import { setBookFinished, setWatched, type ItemSummary } from '../api/client'
import { isBook } from '../lib/books'
import { episodeName } from '../lib/format'
import { Check } from './icons'
import { announceWatched } from '../hooks/watched'

/**
 * The watched tick on a film's or episode's artwork, as a switch: solid when watched (click to unmark), a faint ring on
 * hover when not (click to mark). Flips at once, then tells pages showing it (Continue Watching) to reload. A book's
 * says finished instead.
 * Give it `key={`${id}-${watched}`}` so a reloaded item resets it.
 */
export function WatchedToggle({ item, onChanged }: { item: ItemSummary; onChanged?: () => void }) {
  const [watched, setLocal] = useState(item.userData.watched)
  const name = item.kind === 'Episode' ? episodeName(item) : item.title
  const book = isBook(item)

  async function toggle() {
    const next = !watched
    setLocal(next)
    try {
      await (book ? setBookFinished(item.id, next) : setWatched(item.id, next))
      announceWatched(item)
      onChanged?.()
    } catch {
      setLocal(!next)
    }
  }

  return (
    <button
      type="button"
      className={watched ? 'watched-toggle on' : 'watched-toggle'}
      aria-pressed={watched}
      aria-label={book ? (watched ? `Finished: mark ${name} as unread` : `Mark ${name} finished`) : watched ? `Watched: mark ${name} as unwatched` : `Mark ${name} as watched`}
      title={book ? (watched ? 'Finished. Click to mark as unread' : 'Mark finished') : watched ? 'Watched. Click to mark unwatched' : 'Mark as watched'}
      onClick={toggle}
    >
      <Check />
    </button>
  )
}
