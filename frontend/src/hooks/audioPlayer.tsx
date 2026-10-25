import type Hls from 'hls.js'
import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import { getItem, imageUrl, reportProgress, startPlayback, stopPlayback, type ItemSummary } from '../api/client'
import { detectProfile } from '../lib/profile'
import { AudioPlayerContext, AudioTimeContext, MusicPagesContext, type AudioPlayer } from './audioPlayerContext'
import { emptyQueue, queueReducer, queueStorageKey, readStoredQueue, rebuildQueue, serializeQueue, type StoredQueue } from './queue'

/**
 * Music playback that survives navigation: one <audio> element for the whole app, a queue, and
 * Media Session integration so keyboard media keys and the OS "now playing" controls work.
 *
 * The queue is also kept in sessionStorage (as ids), so a reload brings it back paused where it was.
 */
export function AudioPlayerProvider({ children }: { children: ReactNode }) {
  // One audio.current for the app's lifetime, rendered below and only touched from effects and handlers.
  const audio = useRef<HTMLAudioElement>(null)
  const hls = useRef<Hls | null>(null)
  const session = useRef<string | undefined>(undefined)
  const [state, dispatch] = useReducer(queueReducer, emptyQueue)
  const stateRef = useRef(state)
  const [playing, setPlaying] = useState(false)
  const [time, setTime] = useState(0)
  const [duration, setDuration] = useState(0)
  const [musicPages, setMusicPages] = useState(0)
  // Whether the <audio> holds the current track. A restored queue hasn't been loaded: Play does that, from `resumeAt`.
  const loaded = useRef(false)
  const resumeAt = useRef(0)
  // The position worth remembering, kept current from timeupdate so saving never has to ask the element.
  const position = useRef(0)
  const lastSaved = useRef(-1)
  // Skipping quickly starts loads that overtake each other: only the latest gets to touch the element.
  const loadCount = useRef(0)

  const { tracks: queue, index } = state
  const current = queue[index]

  useEffect(() => {
    stateRef.current = state
  }, [state])

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

  const unload = useCallback(() => {
    const el = audio.current
    loadCount.current += 1
    hls.current?.destroy()
    hls.current = null
    if (session.current) void stopPlayback(session.current)
    session.current = undefined
    loaded.current = false
    if (el) {
      el.pause()
      el.removeAttribute('src')
    }
    position.current = 0
    setTime(0)
    setDuration(0)
    setPlaying(false)
  }, [])

  const load = useCallback(async (track: ItemSummary, startAt = 0) => {
    const el = audio.current
    if (!el) return
    const mine = ++loadCount.current
    hls.current?.destroy()
    hls.current = null
    if (session.current) void stopPlayback(session.current)
    session.current = undefined
    loaded.current = true
    position.current = startAt
    setTime(Math.floor(startAt))
    setDuration(track.durationSeconds ?? 0)

    const info = await startPlayback({ itemId: track.id, profile: detectProfile(), startPositionSeconds: startAt })
    if (mine !== loadCount.current) {
      void stopPlayback(info.sessionId)
      return
    }
    session.current = info.sessionId
    setDuration(info.durationSeconds)

    if (info.method === 'Transcode' && !el.canPlayType('application/vnd.apple.mpegurl')) {
      const { default: HlsPlayer } = await import('hls.js/light')
      if (mine !== loadCount.current) return
      const player = new HlsPlayer({ startPosition: startAt > 0 ? startAt : -1 })
      player.loadSource(info.url)
      player.attachMedia(el)
      hls.current = player
    } else {
      el.src = info.url
      if (startAt > 0) {
        el.addEventListener(
          'loadedmetadata',
          () => {
            el.currentTime = startAt
          },
          { once: true },
        )
      }
    }
    await el.play().catch(() => setPlaying(false))
  }, [])

  // Start the current track whenever the queue asks for it (epoch); a restored queue (epoch 0) waits for Play.
  // The reducer bumps epoch exactly when playback should (re)start; other changes to the queue leave the track alone.
  useEffect(() => {
    if (state.epoch === 0) return
    resumeAt.current = 0
    const { tracks, index } = stateRef.current
    const track = tracks[index]
    if (track) void load(track)
    else unload()
  }, [state.epoch, load, unload])

  // Remember the queue as it changes, and the position now and then while it plays and when the page goes.
  useEffect(() => {
    persist(position.current)
  }, [queue, index, persist])

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

  useEffect(() => {
    const el = audio.current
    if (!el) return
    const onTime = () => {
      const seconds = Math.floor(el.currentTime)
      position.current = el.currentTime
      setTime(seconds)
      if (seconds % 5 === 0 && seconds !== lastSaved.current) {
        lastSaved.current = seconds
        persist(seconds)
      }
    }
    const onPlay = () => setPlaying(true)
    const onPause = () => setPlaying(false)
    const onEnded = () => {
      if (session.current) void reportProgress(session.current, el.currentTime, true, true).catch(() => undefined)
      dispatch({ type: 'next' })
    }
    el.addEventListener('timeupdate', onTime)
    el.addEventListener('play', onPlay)
    el.addEventListener('pause', onPause)
    el.addEventListener('ended', onEnded)
    return () => {
      el.removeEventListener('timeupdate', onTime)
      el.removeEventListener('play', onPlay)
      el.removeEventListener('pause', onPause)
      el.removeEventListener('ended', onEnded)
    }
  }, [persist])

  const play = useCallback((tracks: ItemSummary[], start = 0) => dispatch({ type: 'play', tracks, index: start }), [])
  const enqueue = useCallback((tracks: ItemSummary[]) => dispatch({ type: 'enqueue', tracks }), [])
  const playNext = useCallback((tracks: ItemSummary[]) => dispatch({ type: 'playNext', tracks }), [])
  const removeFromQueue = useCallback((at: number) => dispatch({ type: 'remove', index: at }), [])
  const moveInQueue = useCallback((from: number, to: number) => dispatch({ type: 'move', from, to }), [])
  const clearQueue = useCallback(() => dispatch({ type: 'clear' }), [])
  const skipTo = useCallback((at: number) => dispatch({ type: 'skipTo', index: at }), [])
  const toggle = useCallback(() => {
    const el = audio.current
    if (!el) return
    if (!loaded.current) {
      // Restored after a reload and not yet started: load it from where it was.
      if (current) void load(current, resumeAt.current)
      return
    }
    if (el.paused) void el.play()
    else el.pause()
  }, [current, load])
  const next = useCallback(() => dispatch({ type: 'next' }), [])
  const previous = useCallback(() => {
    // Like every music player: back restarts the track unless you're at its very start.
    const el = audio.current
    if (el && loaded.current && el.currentTime > 3) el.currentTime = 0
    else dispatch({ type: 'previous' })
  }, [])
  const seek = useCallback((seconds: number) => {
    if (loaded.current && audio.current) audio.current.currentTime = seconds
    else {
      resumeAt.current = seconds
      position.current = seconds
      setTime(Math.floor(seconds))
    }
  }, [])
  const stop = useCallback(() => {
    unload()
    dispatch({ type: 'stop' })
  }, [unload])
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
          <audio ref={audio} hidden />
        </AudioTimeContext>
      </MusicPagesContext>
    </AudioPlayerContext>
  )
}
