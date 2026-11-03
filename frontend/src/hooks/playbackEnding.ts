/**
 * How a sitting in the player ended, so the page Play was pressed on can move on: that page (`from`, its history
 * entry's key), the title it was started with, the one it stopped on, whether that one was watched, and the episode
 * after it.
 */
export interface PlaybackEnding {
  from: string | null
  startedWith: string
  stoppedOn: string
  watched: boolean
  next: string | null
}

/** What a page passes to the player with Play (`navigate('/play/…', { state })`), so it can move on afterwards. */
export interface PlayState {
  from?: string
  /** The player was closed from this history entry: coming back to it with Forward doesn't play again. */
  closed?: boolean
}

let last: PlaybackEnding | undefined

export function recordEnding(ending: PlaybackEnding) {
  last = ending
}

/** A new sitting: an ending no page used is old news. */
export function clearEnding() {
  last = undefined
}

/** The last ending, if it was started from the history entry `key`. Read, not taken: rendering can happen twice, and
 * the page that moves on replaces that entry, so nothing else can match it. */
export function endingFrom(key: string): PlaybackEnding | undefined {
  return last && last.from === key ? last : undefined
}

let onBrowserBack: (() => void) | undefined

/** What the open player does as the browser's Back (or a swipe) leaves it: record how the sitting ended. The player
 * passes undefined and its own handler as it closes, which clears it only if no other player has set one since. */
export function whenBrowserBack(handler: (() => void) | undefined, replacing?: () => void) {
  if (handler || onBrowserBack === replacing) onBrowserBack = handler
}

/** Runs on every `popstate`, ahead of React Router's own listener: it's added here as the app loads, before the router
 * mounts, and in the capture phase, which runs first at the window. React renders the new route in a microtask between
 * listeners, so an ending recorded any later lets the page Play was pressed on render without it. */
export function browserMoved() {
  onBrowserBack?.()
}

if (typeof window !== 'undefined') window.addEventListener('popstate', browserMoved, { capture: true })

/** Where a page showing `shown` goes now: past an episode watched to its end, or on to the one the player had moved on
 * to and stopped part way through. Undefined to stay (a show's page, the last episode, stopped part way). */
export function moveOn(ending: PlaybackEnding, shown: string): string | undefined {
  if (shown !== ending.startedWith) return undefined
  const target = ending.watched ? ending.next : ending.stoppedOn
  return target && target !== ending.startedWith ? target : undefined
}

/** Watched as the server counts it: into the credits or past 92%, whichever comes first. */
export function isWatched(position: number, duration: number, creditsStart: number | undefined): boolean {
  return position > 0 && position >= Math.min(creditsStart ?? Infinity, duration > 0 ? duration * 0.92 : Infinity)
}

let reports: Promise<unknown> = Promise.resolve()

/** The player's last report and stop, on their way as it closes. */
export function sendingFinalReport(sent: Promise<unknown>) {
  reports = sent
}

/** Waits (not long) for the player's last report, so a page loading as the player closes sees what was just watched:
 * a show's next episode, not the one that just ended. */
export function finalReportSent(within = 2000): Promise<unknown> {
  return Promise.race([reports, new Promise((resolve) => setTimeout(resolve, within))])
}

const scrolls = new Map<string, number>()

/** Where the page with history entry `key` was scrolled to as Play was pressed, to be there again on the way back. */
export function rememberScroll(key: string) {
  scrolls.set(key, window.scrollY)
}

export function takeScroll(key: string): number | undefined {
  const y = scrolls.get(key)
  scrolls.delete(key)
  return y
}
