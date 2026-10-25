import { createContext, useContext, useEffect } from 'react'
import type { ItemSummary } from '../api/types'

export interface AudioPlayer {
  queue: ItemSummary[]
  index: number
  current: ItemSummary | undefined
  playing: boolean
  duration: number
  /** True while a music page is on screen, when the bar stays put even with nothing queued. */
  onMusicPage: boolean
  /** Replaces the queue and starts playing at `start`. */
  play: (tracks: ItemSummary[], start?: number) => void
  /** "Add to queue": after everything already queued. Plays at once when nothing is. */
  enqueue: (tracks: ItemSummary[]) => void
  /** "Play next": right after the current track. */
  playNext: (tracks: ItemSummary[]) => void
  removeFromQueue: (index: number) => void
  moveInQueue: (from: number, to: number) => void
  /** Drops the upcoming tracks; the current one plays on. */
  clearQueue: () => void
  /** Jumps to a track in the queue. */
  skipTo: (index: number) => void
  toggle: () => void
  next: () => void
  previous: () => void
  seek: (seconds: number) => void
  stop: () => void
}

export const AudioPlayerContext = createContext<AudioPlayer | null>(null)

/** Where the track is, in whole seconds: its own context, so only the seek bar re-renders as music plays. */
export const AudioTimeContext = createContext(0)

export const useAudioTime = () => useContext(AudioTimeContext)

export function useAudioPlayer(): AudioPlayer {
  const player = useContext(AudioPlayerContext)
  if (!player) throw new Error('useAudioPlayer must be used inside AudioPlayerProvider')
  return player
}

/** Counts the music pages on screen (the provider keeps the tally). */
export const MusicPagesContext = createContext<(delta: 1 | -1) => void>(() => undefined)

/** Declares the page a music page while it's mounted (and `active`), so the now-playing bar is always there. */
export function useMusicPage(active = true) {
  const count = useContext(MusicPagesContext)
  useEffect(() => {
    if (!active) return
    count(1)
    return () => count(-1)
  }, [active, count])
}
