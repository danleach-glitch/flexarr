import type { SubtitlePosition } from '../api/client'

/** How far above the picture's foot each position puts the bottom of a subtitle, as a share of the picture's height. */
const lifts: Record<Exclude<SubtitlePosition, 'Top'>, number> = { Bottom: 0.08, Raised: 0.16, Higher: 0.26 }

/** Gap between the picture and a subtitle in the black bar below it, and from the bar's foot. */
const BAR_GAP = 6

/**
 * Where the picture sits in a video element of this size (object-fit: contain): its top, its height, and the black
 * bar below it. A video whose size isn't known yet is taken to fill the element.
 */
export function pictureIn(width: number, height: number, videoWidth: number, videoHeight: number) {
  const picture = videoWidth > 0 && videoHeight > 0 ? Math.min(height, (width * videoHeight) / videoWidth) : height
  const bar = (height - picture) / 2
  return { top: bar, picture, bar }
}

/**
 * A cue's line: its top edge as a percentage of the element's height. At the bottom positions the cue's foot is
 * lifted by a share of the picture (and above the controls while they show); at the top it hangs from just inside the
 * picture's top. With black bars on, a cue goes in the bar below the picture whenever it fits there.
 */
export function cueLine(o: {
  height: number
  top: number
  picture: number
  bar: number
  cueHeight: number
  controlsLift: number
  position: SubtitlePosition
  blackBars: boolean
}): number {
  const { height, top, picture, bar, cueHeight, controlsLift, position, blackBars } = o
  let cueTop: number
  if (position === 'Top') {
    cueTop = top + picture * 0.06
  } else if (blackBars && bar >= cueHeight + BAR_GAP * 2 && controlsLift === 0) {
    cueTop = top + picture + BAR_GAP
  } else {
    const lift = Math.max(bar + picture * lifts[position], controlsLift)
    cueTop = height - lift - cueHeight
  }
  return Math.min(95, Math.max(2, (cueTop / height) * 100))
}
