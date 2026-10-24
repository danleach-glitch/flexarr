import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { clearApiCache, getLibraries, type Library } from '../api/client'
import { LibrariesContext } from './librariesContext'
import { usePolling } from './usePolling'

const SCAN_POLL_MS = 2000
const IDLE_POLL_MS = 30_000
const MAX_IDLE_POLL_MS = 120_000

/** Libraries whose titles may have changed between two answers: their count or last scan moved, or they came or went. */
function changedLibraries(before: Library[], after: Library[]) {
  const was = new Map(before.map((l) => [l.id, `${l.itemCount} ${l.lastScannedAt}`]))
  const now = new Map(after.map((l) => [l.id, `${l.itemCount} ${l.lastScannedAt}`]))
  return [...new Set([...was.keys(), ...now.keys()])].filter((id) => was.get(id) !== now.get(id))
}

/** Cached answers a change in those libraries can touch: their own pages, and those that gather across libraries. */
const ACROSS_LIBRARIES = ['/home', '/genres', '/collections', '/libraries?', '/items/', '/people/']
const touches = (ids: string[]) => (path: string) =>
  ids.some((id) => path.startsWith(`/libraries/${id}/`)) || ACROSS_LIBRARIES.some((p) => path.startsWith(p))

/**
 * Shares the library list (and live scan progress) with the sidebar and pages. It checks often during a scan, less
 * and less often while nothing changes, and not at all from a background tab. An unchanged answer keeps the same
 * array, so nothing that reads it re-renders.
 */
export function LibrariesProvider({ children }: { children: ReactNode }) {
  const [libraries, setLibraries] = useState<Library[]>([])
  const [idleMs, setIdleMs] = useState(IDLE_POLL_MS)
  const last = useRef<string | undefined>(undefined)
  const previous = useRef<Library[] | undefined>(undefined)
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
        // Titles came and went: cached pages showing them may be out of date (scan progress alone changes none).
        const changed = changedLibraries(previous.current ?? next, next)
        if (changed.length) clearApiCache(touches(changed))
        last.current = json
        previous.current = next
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
