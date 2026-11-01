import { createContext, useContext } from 'react'
import type { Me, Profile } from '../api/client'

export interface SessionContextValue {
  /** Who this is; undefined while loading, or on an older server without /me. */
  me: Me | undefined
  /** The household's profiles; undefined while loading. */
  profiles: Profile[] | undefined
  /** Reads who this is again (after the admin PIN is given, say). */
  reloadMe?: () => void
}

export const SessionContext = createContext<SessionContextValue>({ me: undefined, profiles: undefined })

export const useSession = () => useContext(SessionContext)

/** A child is watching (the kids app), and whether any age limit applies (no Discover, requests or recordings). */
export function useAgeLimits() {
  const { me } = useSession()
  return { kids: me?.isKids === true, limited: me?.maxRatingAge != null }
}
