import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import { getItem, getNextEpisode, getPodcastPreferences, imageUrl, savePodcastPreferences, type ItemSummary } from '../api/client'
import { episodeAsTrack, PODCAST_PREFERENCES_CHANGED } from '../lib/podcasts'
import { useMusicSettings } from '../lib/musicSettings'
import { createEngine, isPodcast, type AudioEngine } from './audioEngine'
import { AudioPlayerContext, AudioTimeContext, MusicPagesContext, type AudioPlayer, type SleepTimer } from './audioPlayerContext'
import { emptyQueue, queueReducer, queueStorageKey, readStoredQueue, rebuildQueue, serializeQueue, type StoredQueue } from './queue'

const speedKey = 'flexarr.podcastSpeed'

/** How long the bar says a track couldn't be played, in milliseconds. */
const FAILURE_SHOWN_FOR = 8000

function storedSpeed(): number {
  try {
    const speed = Number(localStorage.getItem(speedKey))
    return speed >= 0.5 && speed <= 4 ? speed : 1
  } catch {
    return 1
  }
}

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
  const [failure, setFailure] = useState<string | null>(null)
  const failureTimer = useRef<number | undefined>(undefined)
  const [musicPages, setMusicPages] = useState(0)
  // Where a restored queue was: Play starts from there rather than the beginning.
  const resumeAt = useRef(0)
  // The position worth remembering, kept current from the engine so saving never has to ask an element.
  const position = useRef(0)
  const lastSaved = useRef(-1)

  const { tracks: queue, index } = state
  const current = queue[index]

  // Podcasts' speed, one for every show: the profile's (kept on the server, so the phone plays at it too), with the
  // last one seen here to start with.
  const [usualSpeed, setUsualSpeed] = useState(storedSpeed)
  const speedFor = useCallback((track: ItemSummary | undefined) => (isPodcast(track) ? usualSpeed : 1), [usualSpeed])
  const speedForRef = useRef(speedFor)
  const [sleep, setSleep] = useState<SleepTimer | null>(null)
  const sleepRef = useRef(sleep)

  useEffect(() => {
    stateRef.current = state
  }, [state])
  useEffect(() => {
    settingsRef.current = settings
  }, [settings])
  useEffect(() => {
    sleepRef.current = sleep
  }, [sleep])

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
        ended: () => {
          const { tracks, index, epoch } = stateRef.current
          const finished = tracks[index]
          if (!isPodcast(finished) || index + 1 < tracks.length) {
            dispatch({ type: 'next' })
            return
          }
          // Auto-play: the show's next episode, else the newest unplayed of the shows followed. Unless something else
          // was started meanwhile.
          void getNextEpisode(finished!.id).then(
            (episode) => {
              if (episode && stateRef.current.epoch === epoch) dispatch({ type: 'play', tracks: [episodeAsTrack(episode)] })
            },
            () => undefined,
          )
        },
        rate: (track) => speedForRef.current(track),
        stopAtEnd: () => sleepRef.current !== null && 'endOfEpisode' in sleepRef.current,
        stoppedAtEnd: () => setSleep(null),
        failed: (track) => {
          // Said for a few seconds, over whatever plays next.
          setFailure(`Couldn't play “${track.title}”`)
          window.clearTimeout(failureTimer.current)
          failureTimer.current = window.setTimeout(() => setFailure(null), FAILURE_SHOWN_FOR)
          // On to the next one, if there is one; the bar says what was skipped.
          const { tracks, index } = stateRef.current
          if (index + 1 < tracks.length) dispatch({ type: 'next' })
        },
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
    speedForRef.current = speedFor
    engine().rateChanged()
  }, [speedFor, engine])

  // The profile's podcast speed, once a podcast plays, and again when Settings changes it.
  const podcastPlaying = isPodcast(current)
  useEffect(() => {
    if (!podcastPlaying) return
    const controller = new AbortController()
    const load = () => getPodcastPreferences(controller.signal).then((p) => setUsualSpeed(p.speed), () => undefined)
    void load()
    window.addEventListener(PODCAST_PREFERENCES_CHANGED, load)
    return () => {
      controller.abort()
      window.removeEventListener(PODCAST_PREFERENCES_CHANGED, load)
    }
  }, [podcastPlaying])

  // The sleep timer, checked every few seconds while it's set.
  useEffect(() => {
    if (!sleep || !('until' in sleep)) return
    const timer = window.setInterval(() => {
      if (Date.now() < sleep.until) return
      engine().pause()
      setSleep(null)
    }, 1000)
    return () => window.clearInterval(timer)
  }, [sleep, engine])

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

  const [nowPlayingOpen, setNowPlayingOpen] = useState(false)
  const play = useCallback((tracks: ItemSummary[], start = 0, options?: { openNowPlaying?: boolean }) => {
    dispatch({ type: 'play', tracks, index: start })
    if (options?.openNowPlaying && tracks.length > 0) setNowPlayingOpen(true)
  }, [])
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
  const skipBy = useCallback((seconds: number) => {
    const at = engine().loaded() ? engine().currentTime() : position.current
    seek(Math.max(0, at + seconds))
  }, [engine, seek])
  const stop = useCallback(() => {
    engine().stop()
    dispatch({ type: 'stop' })
    setSleep(null)
  }, [engine])
  const speed = usualSpeed
  const setSpeed = useCallback((next: number) => {
    setUsualSpeed(next)
    void savePodcastPreferences({ speed: next }).catch(() => undefined)
    try {
      localStorage.setItem(speedKey, String(next))
    } catch {
      // Not remembered here, which is fine: the server has it.
    }
  }, [])
  const countMusicPage = useCallback((delta: 1 | -1) => setMusicPages((n) => n + delta), [])

  // Operating-system media controls.
  useEffect(() => {
    if (!('mediaSession' in navigator) || !current) return
    navigator.mediaSession.metadata = new MediaMetadata({
      title: current.title,
      artist: current.trackArtist ?? current.seriesTitle ?? undefined,
      artwork: current.artwork.poster ? [{ src: imageUrl(current.artwork.poster, 500)!, sizes: '500x500', type: 'image/jpeg' }] : [],
    })
    navigator.mediaSession.setActionHandler('play', toggle)
    navigator.mediaSession.setActionHandler('pause', toggle)
    navigator.mediaSession.setActionHandler('nexttrack', next)
    navigator.mediaSession.setActionHandler('previoustrack', previous)
    navigator.mediaSession.setActionHandler('seekto', (details) => {
      if (details.seekTime !== undefined) seek(details.seekTime)
    })
    // Podcasts: the system's skip buttons go back 15 seconds and on 30, as in every podcast app.
    const podcast = isPodcast(current)
    try {
      navigator.mediaSession.setActionHandler('seekbackward', podcast ? (d) => skipBy(-(d.seekOffset ?? 15)) : null)
      navigator.mediaSession.setActionHandler('seekforward', podcast ? (d) => skipBy(d.seekOffset ?? 30) : null)
    } catch {
      // A browser without these actions.
    }
  }, [current, toggle, next, previous, seek, skipBy])

  const analyser = useCallback(() => engineRef.current?.analyser() ?? null, [])

  const value = useMemo<AudioPlayer>(
    () => ({
      queue,
      index,
      current,
      playing,
      duration,
      failure,
      onMusicPage: musicPages > 0,
      play,
      nowPlayingOpen,
      setNowPlayingOpen,
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
      skipBy,
      stop,
      speed,
      setSpeed,
      sleep,
      setSleep,
      analyser,
    }),
    [
      queue,
      index,
      current,
      playing,
      duration,
      failure,
      musicPages,
      play,
      nowPlayingOpen,
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
      skipBy,
      stop,
      speed,
      setSpeed,
      sleep,
      analyser,
    ],
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
