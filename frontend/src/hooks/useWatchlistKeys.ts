import { useEffect, useState } from 'react'
import { forgetWatchlistKeys, getWatchlistKeys, type WatchlistKeys } from '../api/client'
import { WATCHED_CHANGED } from './watched'

/** The watchlist's keys, kept up to date as it changes; undefined while loading, null without a watchlist on the server. */
export function useWatchlistKeys(enabled = true) {
  const [keys, setKeys] = useState<WatchlistKeys | null>()
  useEffect(() => {
    // A kids profile has no watchlist to ask about.
    if (!enabled) return
    let live = true
    const load = () => void getWatchlistKeys().then((k) => live && setKeys(k))
    // Something marked watched may have left the list.
    const watched = () => {
      forgetWatchlistKeys()
      load()
    }
    load()
    window.addEventListener('flexarr:watchlist', load)
    window.addEventListener(WATCHED_CHANGED, watched)
    return () => {
      live = false
      window.removeEventListener('flexarr:watchlist', load)
      window.removeEventListener(WATCHED_CHANGED, watched)
    }
  }, [enabled])
  return enabled ? keys : undefined
}
