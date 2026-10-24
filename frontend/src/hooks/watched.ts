import { useEffect } from 'react'
import type { ItemSummary } from '../api/types'

/** Fired after something is marked watched or unwatched, so pages showing it (Continue Watching) reload. */
export const WATCHED_CHANGED = 'flexarr:watched-changed'

/** When each id was last announced, for pages that weren't listening at the time (a grid restored on Back). */
const marked = new Map<string, number>()

/** Says `item` was marked: the event carries its id and its show's and parent's, the titles whose ticks may change. */
export function announceWatched(item: Pick<ItemSummary, 'id' | 'parentId' | 'seriesId'>) {
  const ids = [item.id, item.seriesId, item.parentId].filter((id): id is string => !!id)
  const now = Date.now()
  for (const id of ids) marked.set(id, now)
  window.dispatchEvent(new CustomEvent<string[]>(WATCHED_CHANGED, { detail: ids }))
}

/** The ids announced since `time` (a Date.now() value). */
export const watchedSince = (time: number) => [...marked].filter(([, at]) => at >= time).map(([id]) => id)

/** Calls `onChange` with the ids that may have changed (see announceWatched). */
export function useWatchedChanges(onChange: (ids: string[]) => void) {
  useEffect(() => {
    const listener = (e: Event) => onChange(e instanceof CustomEvent && Array.isArray(e.detail) ? (e.detail as string[]) : [])
    window.addEventListener(WATCHED_CHANGED, listener)
    return () => window.removeEventListener(WATCHED_CHANGED, listener)
  }, [onChange])
}
