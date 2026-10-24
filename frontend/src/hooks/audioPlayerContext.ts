import { createContext, useContext } from 'react'
import type { ItemSummary } from '../api/types'

export interface AudioPlayer {
  queue: ItemSummary[]
  index: number
  current: ItemSummary | undefined
  playing: boolean
  time: number
  duration: number
  /** Replaces the queue and starts playing at `start`. */
  play: (tracks: ItemSummary[], start?: number) => void
  toggle: () => void
  next: () => void
  previous: () => void
  seek: (seconds: number) => void
  stop: () => void
}

export const AudioPlayerContext = createContext<AudioPlayer | null>(null)

export function useAudioPlayer(): AudioPlayer {
  const player = useContext(AudioPlayerContext)
  if (!player) throw new Error('useAudioPlayer must be used inside AudioPlayerProvider')
  return player
}
