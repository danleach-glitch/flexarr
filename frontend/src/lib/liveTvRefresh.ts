import type { LiveTvStatus } from '../api/client'

/** A refresh asked for. */
export interface AskedRefresh {
  /** How the status looked just before. */
  before: LiveTvStatus
  /** The look on screen then: older than that, so it says nothing about this refresh. */
  stale?: LiveTvStatus
  /** Whether it has been seen under way since. */
  seen: boolean
  at: number
  /** Waited as long as the server should need, and it's still going. */
  gaveUp?: boolean
}

/** Kept outside the Live TV page, which can be left and come back to while a refresh runs. */
export const liveTvRefresh: {
  asked?: AskedRefresh
  /** When a look last found the one asked for still to finish, the closest to its end there is if it failed. */
  going?: number
  /** Which refresh the page's lists last knew of. */
  listed?: number | string | null
} = {}

export function forgetLiveTvRefresh() {
  liveTvRefresh.asked = undefined
  liveTvRefresh.going = undefined
  liveTvRefresh.listed = undefined
}
