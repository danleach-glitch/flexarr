import { useEffect, useRef } from 'react'

/**
 * Calls `tick` every `ms` (null stops it), but only while the tab is visible: a background tab sends nothing, and
 * catches up with one tick as soon as it's shown again.
 */
export function usePolling(tick: () => void, ms: number | null) {
  const latest = useRef(tick)
  useEffect(() => {
    latest.current = tick
  })

  useEffect(() => {
    if (ms === null) return
    let timer: number | undefined
    const start = () => {
      window.clearInterval(timer)
      timer = window.setInterval(() => latest.current(), ms)
    }
    const onVisibility = () => {
      if (document.hidden) {
        window.clearInterval(timer)
      } else {
        latest.current()
        start()
      }
    }
    if (!document.hidden) start()
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [ms])
}
