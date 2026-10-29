import { useRef, useState } from 'react'

/**
 * A seek slider that follows the pointer while it's dragged and seeks once, when it's let go (or a key is let up).
 * Seeking on every move asks for the sound from each place passed over, which on a podcast's stream (fetched from its
 * host as it plays) piles up requests until playback stalls. `shown` is where the slider is: the drag while there is
 * one, else the track's time.
 */
export function useScrub(time: number, seek: (seconds: number) => void) {
  const [scrubbing, setScrubbing] = useState<number | null>(null)
  const latest = useRef<number | null>(null)
  const commit = () => {
    if (latest.current === null) return
    seek(latest.current)
    latest.current = null
    setScrubbing(null)
  }
  return {
    shown: scrubbing ?? time,
    scrubbing: scrubbing !== null,
    inputProps: {
      onChange: (e: { target: { value: string } }) => {
        latest.current = Number(e.target.value)
        setScrubbing(latest.current)
      },
      onPointerUp: commit,
      onPointerCancel: commit,
      onKeyUp: commit,
      onBlur: commit,
    },
  }
}
