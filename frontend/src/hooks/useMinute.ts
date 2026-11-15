import { useSyncExternalStore } from 'react'

/** One clock for every subscriber, ticking each minute only while something listens. */
const listeners = new Set<() => void>()
let minute = Date.now()
let timer: ReturnType<typeof setInterval> | undefined

function subscribe(listener: () => void) {
  listeners.add(listener)
  if (!timer) {
    minute = Date.now()
    timer = setInterval(() => {
      minute = Date.now()
      listeners.forEach((l) => l())
    }, 60_000)
  }
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0 && timer) {
      clearInterval(timer)
      timer = undefined
    }
  }
}

/**
 * The time, updated once a minute: for a progress bar that moves while nothing else on the page changes, without
 * redrawing what's around it.
 */
export function useMinute() {
  return useSyncExternalStore(subscribe, () => minute)
}
