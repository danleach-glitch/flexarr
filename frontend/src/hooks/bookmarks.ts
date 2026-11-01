import { useEffect } from 'react'

/** Fired after a book's bookmarks change, so the book's page and the player showing them catch up. */
export const BOOKMARKS_CHANGED = 'flexarr:bookmarks-changed'

interface BookmarksChange {
  bookId: string
  /** The player announces its own changes too, and already knows them. */
  fromPlayer: boolean
}

/** Says a bookmark in `bookId` was added, changed or removed. */
export function announceBookmarks(bookId: string, fromPlayer = false) {
  window.dispatchEvent(new CustomEvent<BookmarksChange>(BOOKMARKS_CHANGED, { detail: { bookId, fromPlayer } }))
}

/** Calls `onChange` when the bookmarks of `bookId` change (none: nothing); `skipPlayer` leaves out the player's own. */
export function useBookmarkChanges(bookId: string | undefined, onChange: (bookId: string) => void, skipPlayer = false) {
  useEffect(() => {
    if (!bookId) return
    const listener = (e: Event) => {
      if (!(e instanceof CustomEvent)) return
      const change = e.detail as BookmarksChange
      if (change.bookId === bookId && !(skipPlayer && change.fromPlayer)) onChange(change.bookId)
    }
    window.addEventListener(BOOKMARKS_CHANGED, listener)
    return () => window.removeEventListener(BOOKMARKS_CHANGED, listener)
  }, [bookId, onChange, skipPlayer])
}
