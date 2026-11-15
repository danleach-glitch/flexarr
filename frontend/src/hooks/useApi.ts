import { useCallback, useEffect, useRef, useState } from 'react'
import { freshly, keepAnswer, keptAnswer } from '../api/client'

export interface ApiState<T> {
  data: T | undefined
  error: Error | undefined
  loading: boolean
  reload: () => void
}

/**
 * Runs `load` when `deps` change (aborting the previous request) and exposes the result. Cached GETs may answer from
 * the cache; `reload()` always fetches afresh.
 *
 * With `keep` (a name for the answer, standing for `deps`), the last answer under that name is handed back at once
 * when the page is opened again, even after the cache has gone (back from playing something), while it's asked for
 * afresh in the background: `loading` is true meanwhile.
 */
export function useApi<T>(load: (signal: AbortSignal) => Promise<T>, deps: readonly unknown[], keep?: string): ApiState<T> {
  const [data, setData] = useState<T | undefined>(() => (keep ? keptAnswer<T>(keep) : undefined))
  const [error, setError] = useState<Error>()
  const [loading, setLoading] = useState(true)
  const [version, setVersion] = useState(0)
  const forced = useRef(false)
  // A different name (another library's Browse) shows its own last answer, if there is one, while it loads.
  const [keptFor, setKeptFor] = useState(keep)
  if (keep !== keptFor) {
    setKeptFor(keep)
    const last = keep ? keptAnswer<T>(keep) : undefined
    if (last !== undefined) setData(last)
  }

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    const run = () => load(controller.signal)
    const pending = forced.current ? freshly(run) : run()
    forced.current = false
    pending
      .then((result) => {
        if (controller.signal.aborted) return
        if (keep) keepAnswer(keep, result)
        setData(result)
        setError(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e : new Error(String(e)))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- callers pass the inputs `load` closes over, and `keep` stands for them
  }, [...deps, version])

  const reload = useCallback(() => {
    forced.current = true
    setVersion((v) => v + 1)
  }, [])
  return { data, error, loading, reload }
}
