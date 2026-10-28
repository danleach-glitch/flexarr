import { useEffect, useRef, type RefObject } from 'react'

/** The furthest a file's sound can be held back, as far as the server lets it be moved. */
export const MAX_AUDIO_DELAY_SECONDS = 2

/**
 * Plays a video's sound `seconds` later than its picture, through Web Audio, for a file whose sound runs ahead. The
 * graph is made the first time it's needed and kept: an element's sound can't be taken back out of Web Audio, so at 0
 * it just passes straight through.
 */
export function useDelayedSound(video: RefObject<HTMLVideoElement | null>, seconds: number) {
  const graph = useRef<{ context: AudioContext; delay: DelayNode } | null>(null)

  useEffect(() => {
    const el = video.current
    if (!el || (seconds <= 0 && !graph.current)) return
    if (!graph.current) {
      try {
        const context = new AudioContext()
        const delay = context.createDelay(MAX_AUDIO_DELAY_SECONDS)
        context.createMediaElementSource(el).connect(delay).connect(context.destination)
        graph.current = { context, delay }
      } catch {
        return // No Web Audio here: the sound plays as it is.
      }
    }
    const { context, delay } = graph.current
    delay.delayTime.setTargetAtTime(Math.max(0, seconds), context.currentTime, 0.05)
    if (context.state !== 'suspended') return
    // Browsers only start sound after a click or a key: the next one starts it, should this run before any.
    const resume = () => void context.resume()
    resume()
    window.addEventListener('pointerdown', resume, { once: true })
    window.addEventListener('keydown', resume, { once: true })
    return () => {
      window.removeEventListener('pointerdown', resume)
      window.removeEventListener('keydown', resume)
    }
  }, [video, seconds])

  useEffect(() => () => void graph.current?.context.close(), [])
}
