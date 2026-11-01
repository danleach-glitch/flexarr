import { useEffect, useMemo, type ReactNode } from 'react'
import { clearApiCache, getMe, getProfiles, onAdminLapsed, PROFILES_CHANGED } from '../api/client'
import { SessionContext } from './sessionContext'
import { useApi } from './useApi'

/**
 * Loads who's signed in and the household's profiles, for every page that needs them. The profiles are read again when
 * one changes. Who's signed in is read again when the admin PIN given here runs out, and when a read finds settings
 * locked after all (a restarted server forgets the PIN), so pages stop offering what only the admin may do.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const meState = useApi(getMe, [])
  const me = meState.data
  const reloadMe = meState.reload
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
  useEffect(() => onAdminLapsed(reloadMe), [reloadMe])
  const unlockedUntil = me?.unlockedUntil
  useEffect(() => {
    if (!unlockedUntil) return
    // At least a minute: a browser clock ahead of the server's would otherwise ask every second.
    const timer = window.setTimeout(reloadMe, Math.max(60_000, new Date(unlockedUntil).getTime() - Date.now() + 1000))
    return () => window.clearTimeout(timer)
  }, [unlockedUntil, reloadMe])
  const value = useMemo(() => ({ me, profiles }), [me, profiles])
  return <SessionContext value={value}>{children}</SessionContext>
}
