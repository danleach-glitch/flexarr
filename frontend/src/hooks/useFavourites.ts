import { useCallback, useState } from 'react'
import { setFavouriteChannel, type LiveChannel } from '../api/client'

/**
 * The profile's favourites, starred and unstarred at once on screen while the server is told; put back if it refuses.
 */
export function useFavourites() {
  const [changed, setChanged] = useState<Record<string, boolean>>({})
  const isFavourite = useCallback((c: Pick<LiveChannel, 'id' | 'isFavourite'>) => changed[c.id] ?? c.isFavourite, [changed])
  const toggle = useCallback(
    (c: Pick<LiveChannel, 'id' | 'isFavourite'>) => {
      const next = !(changed[c.id] ?? c.isFavourite)
      setChanged((all) => ({ ...all, [c.id]: next }))
      setFavouriteChannel(c.id, next).catch(() => setChanged((all) => ({ ...all, [c.id]: !next })))
    },
    [changed],
  )
  return { isFavourite, toggle }
}
