import { useEffect, useRef, useState } from 'react'

/**
 * A seek slider that follows the pointer while it's dragged and seeks once, when it's let go (or a key is let up).
 * Seeking on every move asks for the sound from each place passed over, which on a podcast's stream (fetched from its
 * host as it plays) piles up requests until playback stalls. `shown` is where the slider is: the drag while there is
 * one, then where it was let go until the track gets there (rather than springing back to where it was), else the
 * track's time.
 */
export function useScrub(time: number, seek: (seconds: number) => void) {
  const [scrubbing, setScrubbing] = useState<number | null>(null)
  const [landing, setLanding] = useState<number | null>(null)
  const latest = useRef<number | null>(null)

  // Let go: held there until the track's time catches up (checked as it renders, React's way of following a value),
  // or for a few seconds if it never does.
  if (landing !== null && Math.abs(time - landing) < 2) setLanding(null)
  useEffect(() => {
    if (landing === null) return
    const giveUp = window.setTimeout(() => setLanding(null), 4000)
    return () => window.clearTimeout(giveUp)
  }, [landing])

  const commit = () => {
    if (latest.current === null) return
    seek(latest.current)
    setLanding(latest.current)
    latest.current = null
    setScrubbing(null)
  }
  return {
    shown: scrubbing ?? landing ?? time,
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
