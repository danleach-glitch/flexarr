/**
 * How the last video ended, so the page it was played from can move on: the title it was started with (what Play was
 * pressed on), the one it stopped on, whether that one was watched into its credits, and the episode after it.
 */
export interface PlaybackEnding {
  startedWith: string
  stoppedOn: string
  watched: boolean
  next: string | null
}

let last: (PlaybackEnding & { at: number }) | undefined

export function recordEnding(ending: PlaybackEnding) {
  last = { ...ending, at: Date.now() }
}

/** Where a page showing the episode it was started with goes now: on past what was finished, or to the episode it
 * stopped part way through. Undefined to stay. */
export function moveOn(ending: PlaybackEnding): string | undefined {
  const target = ending.watched ? ending.next : ending.stoppedOn
  return target && target !== ending.startedWith ? target : undefined
}

/** The last ending, if it was started with `id` a moment ago (the page coming back as the player closes, not opened
 * later); taken, so only one page moves on with it. */
export function takeEnding(id: string): PlaybackEnding | undefined {
  if (!last || last.startedWith !== id || Date.now() - last.at > 60_000) return undefined
  const ending = last
  last = undefined
  return ending
}
