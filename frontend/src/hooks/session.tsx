import { useEffect, useMemo, type ReactNode } from 'react'
import { clearApiCache, getMe, getProfiles, PROFILES_CHANGED } from '../api/client'
import { SessionContext } from './sessionContext'
import { useApi } from './useApi'

/** Loads who's signed in and the household's profiles once, for every page that needs them (again when one changes). */
export function SessionProvider({ children }: { children: ReactNode }) {
  const me = useApi(getMe, []).data
  const loaded = useApi(getProfiles, [])
  const profiles = loaded.data
  const reload = loaded.reload
  useEffect(() => {
    const changed = () => {
      clearApiCache((path) => path.startsWith('/profiles'))
      reload()
    }
    window.addEventListener(PROFILES_CHANGED, changed)
    return () => window.removeEventListener(PROFILES_CHANGED, changed)
  }, [reload])
  const value = useMemo(() => ({ me, profiles }), [me, profiles])
  return <SessionContext value={value}>{children}</SessionContext>
}
