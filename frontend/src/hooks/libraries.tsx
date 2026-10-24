import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { clearApiCache, getLibraries, type Library } from '../api/client'
import { LibrariesContext } from './librariesContext'
import { usePolling } from './usePolling'

const SCAN_POLL_MS = 2000
const IDLE_POLL_MS = 30_000
const MAX_IDLE_POLL_MS = 120_000

/**
 * Shares the library list (and live scan progress) with the sidebar and pages. It checks often during a scan, less
 * and less often while nothing changes, and not at all from a background tab. An unchanged answer keeps the same
 * array, so nothing that reads it re-renders.
 */
export function LibrariesProvider({ children }: { children: ReactNode }) {
  const [libraries, setLibraries] = useState<Library[]>([])
  const [idleMs, setIdleMs] = useState(IDLE_POLL_MS)
  const last = useRef<string | undefined>(undefined)
  const inFlight = useRef<AbortController | undefined>(undefined)

  const reload = useCallback(() => {
    inFlight.current?.abort()
    const controller = new AbortController()
    inFlight.current = controller
    getLibraries(controller.signal)
      .then((next) => {
        const json = JSON.stringify(next)
        if (json === last.current) {
          setIdleMs((ms) => Math.min(ms * 2, MAX_IDLE_POLL_MS))
          return
        }
        // A scan moved on or titles came and went: cached pages may be out of date.
        if (last.current !== undefined) clearApiCache()
        last.current = json
        setLibraries(next)
        setIdleMs(IDLE_POLL_MS)
      })
      .catch(() => undefined)
  }, [])

  useEffect(() => {
    reload()
    return () => inFlight.current?.abort()
  }, [reload])

  const scanning = libraries.some((l) => l.scan)
  usePolling(reload, scanning ? SCAN_POLL_MS : idleMs)

  const value = useMemo(() => ({ libraries, reload }), [libraries, reload])
  return <LibrariesContext value={value}>{children}</LibrariesContext>
}
