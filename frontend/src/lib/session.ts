/**
 * sessionStorage, for small things this tab remembers (a profile chosen, a queue to bring back). Where the browser
 * blocks storage (some private windows and locked-down browsers) reads come back empty and writes do nothing, instead
 * of throwing and stopping the app from starting.
 */
export const session = {
  get(key: string): string | null {
    try {
      return sessionStorage.getItem(key)
    } catch {
      return null
    }
  },
  set(key: string, value: string) {
    try {
      sessionStorage.setItem(key, value)
    } catch {
      // Not remembered; the app works without it.
    }
  },
  remove(key: string) {
    try {
      sessionStorage.removeItem(key)
    } catch {
      // Nothing was kept.
    }
  },
}
