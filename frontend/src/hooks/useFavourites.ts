import { useCallback, useEffect, useRef, useState } from 'react'
import { setFavouriteChannel, type LiveChannel } from '../api/client'
import { noticeFailure } from '../lib/notice'

/**
 * The profile's favourites, starred and unstarred at once on screen while the server is told; put back, and said why, if it refuses.
 * `toggle` stays the same function throughout, so a list of memoised rows isn't redrawn by a star.
 */
export function useFavourites() {
  const [changed, setChanged] = useState<Record<string, boolean>>({})
  const latest = useRef(changed)
  useEffect(() => {
    latest.current = changed
  })
  const isFavourite = useCallback((c: Pick<LiveChannel, 'id' | 'isFavourite'>) => changed[c.id] ?? c.isFavourite, [changed])
  const toggle = useCallback((c: Pick<LiveChannel, 'id' | 'isFavourite'>) => {
    const next = !(latest.current[c.id] ?? c.isFavourite)
    latest.current = { ...latest.current, [c.id]: next }
    setChanged((all) => ({ ...all, [c.id]: next }))
    setFavouriteChannel(c.id, next).catch((e: unknown) => {
      latest.current = { ...latest.current, [c.id]: !next }
      setChanged((all) => ({ ...all, [c.id]: !next }))
      noticeFailure(e)
    })
  }, [])
  return { isFavourite, toggle }
}
