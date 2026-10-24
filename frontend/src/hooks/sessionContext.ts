import { createContext, useContext } from 'react'
import type { Me, Profile } from '../api/client'

export interface SessionContextValue {
  /** Who this is; undefined while loading, or on an older server without /me. */
  me: Me | undefined
  /** The household's profiles; undefined while loading. */
  profiles: Profile[] | undefined
}

export const SessionContext = createContext<SessionContextValue>({ me: undefined, profiles: undefined })

export const useSession = () => useContext(SessionContext)
