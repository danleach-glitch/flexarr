import { useMemo, type ReactNode } from 'react'
import { getMe, getProfiles } from '../api/client'
import { SessionContext } from './sessionContext'
import { useApi } from './useApi'

/** Loads who's signed in and the household's profiles once, for every page that needs them. */
export function SessionProvider({ children }: { children: ReactNode }) {
  const me = useApi(getMe, []).data
  const profiles = useApi(getProfiles, []).data
  const value = useMemo(() => ({ me, profiles }), [me, profiles])
  return <SessionContext value={value}>{children}</SessionContext>
}
