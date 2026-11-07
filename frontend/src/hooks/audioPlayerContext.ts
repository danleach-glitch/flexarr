import { bookSpeedPresets } from '../lib/books'
import { createContext, use, useContext } from 'react'
import type { BookManifest, ItemSummary } from '../api/types'
import type { SleepTimer } from '../lib/sleepTimer'

export interface AudioPlayer {
  queue: ItemSummary[]
  index: number
  current: ItemSummary | undefined
  playing: boolean
  duration: number
  /** A track that couldn't be played just now (and was skipped), said in the bar for a few seconds. */
  failure: string | null
  /**
   * Replaces the queue and starts playing at `start`; `openNowPlaying` (Play and Shuffle) puts Now Playing up too, and
   * `at` starts that track part way in (a music library's Resume).
   */
  play: (tracks: ItemSummary[], start?: number, options?: { openNowPlaying?: boolean; at?: number }) => void
  /** Now Playing is open over the whole window. */
  nowPlayingOpen: boolean
  setNowPlayingOpen: (open: boolean) => void
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
  /** Jumps back (negative) or forward from where it is: podcasts' 15 back, 30 on. */
  skipBy: (seconds: number) => void
  stop: () => void
  /**
   * Taken out of Continue watching or listening: if it's what's loaded it stops without sending its place (which would
   * put it back), and a book's place in this browser is forgotten too.
   */
  forget: (item: ItemSummary) => void
  /**
   * How fast what's playing plays (music always plays at 1×): the profile's podcast speed, or a book's own (its
   * default, the profile's speed for books).
   */
  speed: number
  /** Sets the speed: for every podcast, or for the book playing, here and on the profile's other devices. */
  setSpeed: (speed: number) => void
  /**
   * A sleep timer: playback pauses at `until` (a Date.now() time), when the episode ends, or at `at` on a book's
   * timeline (the end of its chapter), fading out first. Null when there's none.
   */
  sleep: SleepTimer | null
  setSleep: (timer: SleepTimer | null) => void
  /** The music as it sounds, for the visualiser: null before anything has played, or without Web Audio. */
  analyser: () => AnalyserNode | null
  /** The book playing: its files, chapters, bookmarks (kept current) and the next in its series. Null for anything else. */
  book: BookManifest | null
  /** How far the skip buttons go: a book's are the profile's; a podcast's are 15 back and 30 on. */
  skipSteps: { back: number; forward: number }
  /** Another device got further in the book: the place, offered rather than jumped to. */
  bookOffer: { position: number; device: string | null } | null
  acceptBookOffer: () => void
  dismissBookOffer: () => void
  /** The book ended and its series has another: offered with a Play button, never started by itself. */
  nextBook: ItemSummary | null
  playNextBook: () => void
  dismissNextBook: () => void
  /** Bookmarks the place playing in the book, with an optional note. */
  addBookmark: (note?: string) => Promise<void>
  removeBookmark: (id: string) => Promise<void>
}

export type { SleepTimer }

/** The speeds podcasts can play at: the same choices as books, as they share one speed. */
export const podcastSpeeds = bookSpeedPresets

export const AudioPlayerContext = createContext<AudioPlayer | null>(null)

/** Where the track is, in whole seconds: its own context, so only the seek bar re-renders as music plays. */
export const AudioTimeContext = createContext(0)

export const useAudioTime = () => useContext(AudioTimeContext)

export function useAudioPlayer(): AudioPlayer {
  const player = useContext(AudioPlayerContext)
  if (!player) throw new Error('useAudioPlayer must be used inside AudioPlayerProvider')
  return player
}

/** The ways to start something playing. They stay the same for as long as the player is there. */
export type AudioActions = Pick<AudioPlayer, 'play' | 'enqueue' | 'playNext'>

/**
 * The player's actions on their own, which never change: what a card needs for its play button. A grid can hold
 * thousands of cards, and one that read the whole player would re-render with every track, pause and length.
 */
export const AudioActionsContext = createContext<AudioActions | null>(null)

/** For something that plays but doesn't show what's playing (a card's play button): it doesn't follow the player. */
export function useAudioActions(): AudioActions {
  const actions = use(AudioActionsContext)
  if (actions) return actions
  // Given a player but no actions of their own (a stand-in player in a test): the player's.
  const player = use(AudioPlayerContext)
  if (!player) throw new Error('useAudioActions must be used inside AudioPlayerProvider')
  return player
}
