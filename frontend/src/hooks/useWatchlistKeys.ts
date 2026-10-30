import { useEffect, useState } from 'react'
import { getWatchlistKeys, type WatchlistKeys } from '../api/client'

/** The watchlist's keys, kept up to date as it changes; undefined while loading, null without a watchlist on the server. */
export function useWatchlistKeys() {
  const [keys, setKeys] = useState<WatchlistKeys | null>()
  useEffect(() => {
    let live = true
    const load = () => void getWatchlistKeys().then((k) => live && setKeys(k))
    load()
    window.addEventListener('flexarr:watchlist', load)
    return () => {
      live = false
      window.removeEventListener('flexarr:watchlist', load)
    }
  }, [])
  return keys
}
