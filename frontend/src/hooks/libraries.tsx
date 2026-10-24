import { useEffect, type ReactNode } from 'react'
import { getLibraries } from '../api/client'
import { LibrariesContext } from './librariesContext'
import { useApi } from './useApi'

/** Shares the library list (and live scan progress) with the sidebar and pages. */
export function LibrariesProvider({ children }: { children: ReactNode }) {
  const { data, reload } = useApi(getLibraries, [])
  const scanning = data?.some((l) => l.scan) ?? false

  useEffect(() => {
    const timer = setInterval(reload, scanning ? 2000 : 30000)
    return () => clearInterval(timer)
  }, [reload, scanning])

  return <LibrariesContext value={{ libraries: data ?? [], reload }}>{children}</LibrariesContext>
}
