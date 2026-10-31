import { useEffect, useSyncExternalStore } from 'react'
import { ApiError, getWaitingPairings, type PairingDetails } from '../api/client'
import { usePolling } from './usePolling'

const POLL_MS = 10_000

interface Waiting {
  /** Devices showing a code right now, as the server last listed them. */
  list: PairingDetails[]
  /** The server has answered at least once. */
  loaded: boolean
  /** Not an admin after all, or an older server without the list: stop asking. */
  unavailable: boolean
  /** How many Waiting to connect lists are on screen (the app-wide prompt keeps out of their way). */
  listsShowing: number
}

// One list for the app-wide prompt and Settings › Devices › Connect a device, so they look together and an answer in one
// takes the device out of both.
let state: Waiting = { list: [], loaded: false, unavailable: false, listsShowing: 0 }
let users = 0
let pending: Promise<void> | undefined
let lastLook = 0
let again = false
/** Moves on when nobody's looking any more, so an answer that arrives after that is ignored. */
let generation = 0
const listeners = new Set<() => void>()

function set(next: Partial<Waiting>) {
  state = { ...state, ...next }
  listeners.forEach((notify) => notify())
}

/**
 * Asks the server again. The server's list is the truth: a code still on a device's screen keeps going (its expiry
 * moves on), so nothing is dropped here at an expiry the next look might have moved. `fresh`: an answer to a question
 * asked before now won't do (after allowing or declining), so one already on its way is followed by another.
 */
function look(fresh = false) {
  if (document.hidden || state.unavailable) return
  if (pending) {
    if (fresh) again = true
    return
  }
  const asked = generation
  pending = getWaitingPairings()
    .then(
      (list) => {
        if (asked === generation && !again) set({ list, loaded: true })
      },
      (e: unknown) => {
        if (asked === generation && e instanceof ApiError && (e.status === 403 || e.status === 404)) set({ unavailable: true })
        // Anything else (the server restarting, a dropped connection): the next look tries again.
      },
    )
    .finally(() => {
      if (asked !== generation) return
      pending = undefined
      lastLook = Date.now()
      if (again) {
        again = false
        look()
      }
    })
}

/** Two pollers on one screen share one look every POLL_MS rather than one each. */
function tick() {
  if (Date.now() - lastLook >= POLL_MS / 2) look()
}

/** Allowed or declined here: out of every list straight away, then the server says what's left. */
export function answeredPairing(code: string) {
  set({ list: state.list.filter((p) => p.code !== code) })
  look(true)
}

function subscribe(notify: () => void) {
  listeners.add(notify)
  users++
  return () => {
    listeners.delete(notify)
    // Nobody's looking: start afresh next time (and between tests).
    if (--users > 0) return
    state = { list: [], loaded: false, unavailable: false, listsShowing: 0 }
    pending = undefined
    again = false
    lastLook = 0
    generation++
  }
}

/**
 * The devices waiting to connect, looked at every 10 seconds while the tab is visible and `enabled` (an admin). `list`
 * says this is a Waiting to connect list on screen.
 */
export function useWaitingPairings(enabled: boolean, { list = false }: { list?: boolean } = {}) {
  const waiting = useSyncExternalStore(subscribe, () => state)
  const polling = enabled && !waiting.unavailable

  useEffect(() => {
    if (polling) look()
  }, [polling])
  usePolling(tick, polling ? POLL_MS : null)

  useEffect(() => {
    if (!list || !enabled) return
    set({ listsShowing: state.listsShowing + 1 })
    return () => set({ listsShowing: Math.max(0, state.listsShowing - 1) })
  }, [list, enabled])

  return { ...waiting, list: polling ? waiting.list : [], available: enabled && !waiting.unavailable }
}
