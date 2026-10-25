import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import { getItem, imageUrl, type ItemSummary } from '../api/client'
import { useMusicSettings } from '../lib/musicSettings'
import { createEngine, type AudioEngine } from './audioEngine'
import { AudioPlayerContext, AudioTimeContext, MusicPagesContext, type AudioPlayer } from './audioPlayerContext'
import { emptyQueue, queueReducer, queueStorageKey, readStoredQueue, rebuildQueue, serializeQueue, type StoredQueue } from './queue'

/**
 * Music playback that survives navigation: two <audio> elements for the whole app (one plays while the next track gets
 * ready in the other, so tracks follow each other without a gap, or crossfade), a queue, and Media Session integration
 * so keyboard media keys and the OS "now playing" controls work.
 *
 * The queue is also kept in sessionStorage (as ids), so a reload brings it back paused where it was.
 */
export function AudioPlayerProvider({ children }: { children: ReactNode }) {
  // The elements live for the app's lifetime, rendered below and only touched by the engine.
  const audioA = useRef<HTMLAudioElement>(null)
  const audioB = useRef<HTMLAudioElement>(null)
  const [state, dispatch] = useReducer(queueReducer, emptyQueue)
  const stateRef = useRef(state)
  const settings = useMusicSettings()
  const settingsRef = useRef(settings)
  const [playing, setPlaying] = useState(false)
  const [time, setTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [musicPages, setMusicPages] = useState(0)
  // Where a restored queue was: Play starts from there rather than the beginning.
  const resumeAt = useRef(0)
  // The position worth remembering, kept current from the engine so saving never has to ask an element.
  const position = useRef(0)
  const lastSaved = useRef(-1)

  const { tracks: queue, index } = state
  const current = queue[index]

  useEffect(() => {
    stateRef.current = state
  }, [state])
  useEffect(() => {
    settingsRef.current = settings
  }, [settings])

  const persist = useCallback((at: number) => {
    const s = stateRef.current
    try {
      // An untouched queue (epoch 0, nothing in it) is the state before a restore: leave what's stored for it to read.
      if (s.tracks.length === 0) {
        if (s.epoch > 0) sessionStorage.removeItem(queueStorageKey)
      } else sessionStorage.setItem(queueStorageKey, JSON.stringify(serializeQueue(s, at)))
    } catch {
      // Storage blocked: the queue just won't survive a reload.
    }
  }, [])

  // The engine drives the elements and reports back. Made on first use, from effects and handlers only.
  const engineRef = useRef<AudioEngine>(null)
  const engine = useCallback(
    () =>
      (engineRef.current ??= createEngine({
        queue: () => stateRef.current,
        settings: () => settingsRef.current,
        time: (seconds) => {
          position.current = seconds
          const whole = Math.floor(seconds)
          setTime(whole)
          if (whole % 5 === 0 && whole !== lastSaved.current) {
            lastSaved.current = whole
            persist(whole)
          }
        },
        playing: setPlaying,
        duration: setDuration,
        advanced: () => dispatch({ type: 'advance' }),
        ended: () => dispatch({ type: 'next' }),
      })),
    [persist],
  )

  useEffect(() => engine().attach([audioA.current, audioB.current].filter((el): el is HTMLAudioElement => el !== null)), [engine])

  // Start the current track whenever the queue asks for it (epoch); a restored queue (epoch 0) waits for Play.
  // The reducer bumps epoch exactly when playback should (re)start; other changes to the queue leave the track alone.
  useEffect(() => {
    if (state.epoch === 0) return
    resumeAt.current = 0
    const { tracks, index } = stateRef.current
    const track = tracks[index]
    if (track) void engine().load(track, index)
    else engine().stop()
  }, [state.epoch, engine])

  // Remember the queue as it changes, and the position now and then while it plays and when the page goes.
  // A changed queue may also mean the track got ready as "next" no longer is.
  useEffect(() => {
    persist(position.current)
    engine().queueChanged()
  }, [queue, index, persist, engine])

  useEffect(() => engine().settingsChanged(), [settings, engine])

  useEffect(() => {
    const save = () => persist(position.current)
    window.addEventListener('pagehide', save)
    return () => window.removeEventListener('pagehide', save)
  }, [persist])

  // Bring back the queue a reload interrupted, paused: tracks are fetched by album (one request each).
  useEffect(() => {
    let stored: StoredQueue | null
    try {
      stored = readStoredQueue(sessionStorage.getItem(queueStorageKey))
    } catch {
      stored = null
    }
    if (!stored) return
    let cancelled = false
    const byId = new Map<string, ItemSummary>()
    const keep = (track: ItemSummary) => byId.set(track.id, track)
    void (async () => {
      await Promise.all(stored.albums.map((id) => getItem(id).then((album) => album.children.forEach(keep), () => undefined)))
      const missing = [...new Set(stored.ids.filter((id) => !byId.has(id)))]
      await Promise.all(missing.map((id) => getItem(id).then((detail) => keep(detail.item), () => undefined)))
      // Something was played in the meantime: that queue wins.
      if (cancelled || stateRef.current.epoch > 0) return
      const { tracks, index } = rebuildQueue(stored, byId)
      if (tracks.length === 0) return
      resumeAt.current = stored.position
      position.current = stored.position
      setTime(Math.floor(stored.position))
      setDuration(tracks[index].durationSeconds ?? 0)
      dispatch({ type: 'restore', tracks, index })
    })()
    return () => {
      cancelled = true
    }
  }, [])

  const play = useCallback((tracks: ItemSummary[], start = 0) => dispatch({ type: 'play', tracks, index: start }), [])
  const enqueue = useCallback((tracks: ItemSummary[]) => dispatch({ type: 'enqueue', tracks }), [])
  const playNext = useCallback((tracks: ItemSummary[]) => dispatch({ type: 'playNext', tracks }), [])
  const removeFromQueue = useCallback((at: number) => dispatch({ type: 'remove', index: at }), [])
  const moveInQueue = useCallback((from: number, to: number) => dispatch({ type: 'move', from, to }), [])
  const clearQueue = useCallback(() => dispatch({ type: 'clear' }), [])
  const skipTo = useCallback((at: number) => dispatch({ type: 'skipTo', index: at }), [])
  const toggle = useCallback(() => {
    if (!engine().loaded()) {
      // Restored after a reload and not yet started: load it from where it was.
      if (current) void engine().load(current, index, resumeAt.current)
      return
    }
    if (engine().paused()) engine().play()
    else engine().pause()
  }, [current, index, engine])
  const next = useCallback(() => dispatch({ type: 'next' }), [])
  const previous = useCallback(() => {
    // Like every music player: back restarts the track unless you're at its very start.
    if (engine().loaded() && engine().currentTime() > 3) engine().seek(0)
    else dispatch({ type: 'previous' })
  }, [engine])
  const seek = useCallback((seconds: number) => {
    if (engine().loaded()) engine().seek(seconds)
    else {
      resumeAt.current = seconds
      position.current = seconds
      setTime(Math.floor(seconds))
    }
  }, [engine])
  const stop = useCallback(() => {
    engine().stop()
    dispatch({ type: 'stop' })
  }, [engine])
  const countMusicPage = useCallback((delta: 1 | -1) => setMusicPages((n) => n + delta), [])

  // Operating-system media controls.
  useEffect(() => {
    if (!('mediaSession' in navigator) || !current) return
    navigator.mediaSession.metadata = new MediaMetadata({
      title: current.title,
      artist: current.seriesTitle ?? undefined,
      artwork: current.artwork.poster ? [{ src: imageUrl(current.artwork.poster, 500)!, sizes: '500x500', type: 'image/jpeg' }] : [],
    })
    navigator.mediaSession.setActionHandler('play', toggle)
    navigator.mediaSession.setActionHandler('pause', toggle)
    navigator.mediaSession.setActionHandler('nexttrack', next)
    navigator.mediaSession.setActionHandler('previoustrack', previous)
    navigator.mediaSession.setActionHandler('seekto', (details) => {
      if (details.seekTime !== undefined) seek(details.seekTime)
    })
  }, [current, toggle, next, previous, seek])

  const value = useMemo<AudioPlayer>(
    () => ({
      queue,
      index,
      current,
      playing,
      duration,
      onMusicPage: musicPages > 0,
      play,
      enqueue,
      playNext,
      removeFromQueue,
      moveInQueue,
      clearQueue,
      skipTo,
      toggle,
      next,
      previous,
      seek,
      stop,
    }),
    [queue, index, current, playing, duration, musicPages, play, enqueue, playNext, removeFromQueue, moveInQueue, clearQueue, skipTo, toggle, next, previous, seek, stop],
  )
  return (
    <AudioPlayerContext value={value}>
      <MusicPagesContext value={countMusicPage}>
        <AudioTimeContext value={time}>
          {children}
          {/* preload="auto" so the next track buffers as soon as it has a source, before anyone presses anything. */}
          <audio ref={audioA} hidden preload="auto" />
          <audio ref={audioB} hidden preload="auto" />
        </AudioTimeContext>
      </MusicPagesContext>
    </AudioPlayerContext>
  )
}
