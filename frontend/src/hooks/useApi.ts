import { useCallback, useEffect, useRef, useState } from 'react'
import { freshly } from '../api/client'

export interface ApiState<T> {
  data: T | undefined
  error: Error | undefined
  loading: boolean
  reload: () => void
}

/**
 * Runs `load` when `deps` change (aborting the previous request) and exposes the result. Cached GETs may answer from
 * the cache; `reload()` always fetches afresh.
 */
export function useApi<T>(load: (signal: AbortSignal) => Promise<T>, deps: readonly unknown[]): ApiState<T> {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<Error>()
  const [loading, setLoading] = useState(true)
  const [version, setVersion] = useState(0)
  const forced = useRef(false)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    const run = () => load(controller.signal)
    const pending = forced.current ? freshly(run) : run()
    forced.current = false
    pending
      .then((result) => {
        if (controller.signal.aborted) return
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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- callers pass the inputs `load` closes over
  }, [...deps, version])

  const reload = useCallback(() => {
    forced.current = true
    setVersion((v) => v + 1)
  }, [])
  return { data, error, loading, reload }
}
