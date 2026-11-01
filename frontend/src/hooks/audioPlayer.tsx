import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'
import {
  addBookmark as saveBookmark,
  BOOK_LISTENING_CHANGED,
  deleteBookmark,
  getBookListening,
  getBookmarks,
  getItem,
  getNextEpisode,
  getPodcastPreferences,
  imageUrl,
  savePodcastPreferences,
  setPlaybackSpeed,
  type BookListening,
  type BookManifest,
  type ItemSummary,
} from '../api/client'
import { authorsLine, chapterEnd, chapterIndexAt, clampSpeed, fadeLevel, isBook, nextChapterStart, previousChapterStart, sleepReached } from '../lib/books'
import { episodeAsTrack, PODCAST_PREFERENCES_CHANGED } from '../lib/podcasts'
import { useMusicSettings } from '../lib/musicSettings'
import { FADE_SECONDS } from '../lib/sleepTimer'
import { createEngine, isPodcast, type AudioEngine } from './audioEngine'
import { announceBookmarks, useBookmarkChanges } from './bookmarks'
import { AudioPlayerContext, AudioTimeContext, MusicPagesContext, type AudioPlayer, type SleepTimer } from './audioPlayerContext'
import { emptyQueue, queueReducer, queueStorageKey, readStoredQueue, rebuildQueue, serializeQueue, type StoredQueue } from './queue'

const speedKey = 'flexarr.podcastSpeed'

/** How long the bar says a track couldn't be played, in milliseconds. */
const FAILURE_SHOWN_FOR = 8000

/** A podcast's skip buttons, as in every podcast app. */
const PODCAST_STEPS = { back: 15, forward: 30 }

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
 * so keyboard media keys and the OS "now playing" controls work. A book plays its files back to back the same way, on
 * one timeline, with chapters, its own speed, smart rewind and bookmarks.
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
  // The book playing: its manifest (bookmarks kept current), its own speed, and how this profile listens.
  const [book, setBook] = useState<BookManifest | null>(null)
  const [bookSpeed, setBookSpeed] = useState(1)
  const bookSpeedRef = useRef(1)
  const [listening, setListening] = useState<BookListening | null>(null)
  const listeningRef = useRef<BookListening | null>(null)
  const [bookOffer, setBookOffer] = useState<AudioPlayer['bookOffer']>(null)
  const [nextBook, setNextBook] = useState<ItemSummary | null>(null)
  const speedFor = useCallback(
    (track: ItemSummary | undefined) => (isBook(track) ? bookSpeed : isPodcast(track) ? usualSpeed : 1),
    [usualSpeed, bookSpeed],
  )
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
  useEffect(() => {
    listeningRef.current = listening
  }, [listening])

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
        rate: (track) => (isBook(track) ? bookSpeedRef.current : speedForRef.current(track)),
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
        book: (started) => {
          if (!started) {
            setBook(null)
            setBookOffer(null)
            return
          }
          const { manifest, decision } = started
          // The book's own speed, ready before its first file opens (the engine asks for it straight away).
          bookSpeedRef.current = clampSpeed(manifest.speed)
          setBookSpeed(bookSpeedRef.current)
          setListening(manifest.listening)
          listeningRef.current = manifest.listening
          setBook(manifest)
          setBookOffer(decision.offer)
          setNextBook(null)
        },
        bookEnded: (manifest) => {
          setSleep((s) => (s && 'endOfChapter' in s ? null : s))
          setNextBook(manifest.nextInSeries)
        },
        smartRewind: () => listeningRef.current?.smartRewind ?? true,
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

  // How this profile listens to books, again when Settings › Audiobooks changes it.
  const bookPlaying = isBook(current)
  useEffect(() => {
    if (!bookPlaying) return
    const controller = new AbortController()
    const load = () => getBookListening(controller.signal).then(setListening, () => undefined)
    window.addEventListener(BOOK_LISTENING_CHANGED, load)
    return () => {
      controller.abort()
      window.removeEventListener(BOOK_LISTENING_CHANGED, load)
    }
  }, [bookPlaying])

  // The sleep timer, checked every second while it's set: the sound fades over its last seconds, then stops.
  useEffect(() => {
    if (!sleep || 'endOfEpisode' in sleep) return
    const check = () => {
      // The timer as it is now: a seek moves "end of chapter" in the ref straight away, before the next render.
      const timer = sleepRef.current
      if (!timer || 'endOfEpisode' in timer) return
      const left =
        'until' in timer ? (timer.until - Date.now()) / 1000 : sleepReached(timer.at, position.current) ? 0 : (timer.at - position.current) / (bookSpeedRef.current || 1)
      if (left <= 0) {
        engine().pause()
        engine().volume(1)
        setSleep(null)
      } else engine().volume(fadeLevel(left, FADE_SECONDS))
    }
    const timer = window.setInterval(check, 250)
    return () => {
      window.clearInterval(timer)
      engine().volume(1)
    }
  }, [sleep, engine])

  useEffect(() => {
    const save = () => {
      persist(position.current)
      engine().beacon()
    }
    window.addEventListener('pagehide', save)
    return () => window.removeEventListener('pagehide', save)
  }, [persist, engine])

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
    setNextBook(null)
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
      // Restored after a reload (or a book heard to its end) and not yet started: load it from where it was.
      if (current) void engine().load(current, index, resumeAt.current)
      return
    }
    if (engine().paused()) engine().play()
    else engine().pause()
  }, [current, index, engine])
  const bookRef = useRef(book)
  useEffect(() => {
    bookRef.current = book
  }, [book])
  const seek = useCallback(
    (seconds: number) => {
      // "End of chapter" is the end of the chapter playing: a seek to another chapter moves it there.
      const timer = sleepRef.current
      const b = bookRef.current
      if (timer && 'endOfChapter' in timer && b) {
        const at = endOfChapterAt(b, seconds)
        if (at !== timer.at) {
          sleepRef.current = { endOfChapter: true, at }
          setSleep(sleepRef.current)
        }
      }
      if (engine().loaded()) engine().seek(seconds)
      else {
        resumeAt.current = seconds
        position.current = seconds
        setTime(Math.floor(seconds))
      }
    },
    [engine],
  )
  const chapters = book?.chapters
  // A book's Next and Previous go by chapter.
  const next = useCallback(() => {
    if (chapters) {
      const at = nextChapterStart(chapters, engine().currentTime())
      if (at !== null) seek(at)
      return
    }
    dispatch({ type: 'next' })
  }, [chapters, engine, seek])
  const previous = useCallback(() => {
    if (chapters) {
      seek(previousChapterStart(chapters, engine().currentTime()))
      return
    }
    // Like every music player: back restarts the track unless you're at its very start.
    if (engine().loaded() && engine().currentTime() > 3) engine().seek(0)
    else dispatch({ type: 'previous' })
  }, [chapters, engine, seek])
  const skipBy = useCallback(
    (seconds: number) => {
      const at = engine().loaded() ? engine().currentTime() : position.current
      seek(Math.max(0, at + seconds))
    },
    [engine, seek],
  )
  const stop = useCallback(() => {
    engine().stop()
    dispatch({ type: 'stop' })
    setSleep(null)
    setNextBook(null)
  }, [engine])
  const speed = bookPlaying ? bookSpeed : usualSpeed
  const currentId = current?.id
  const setSpeed = useCallback(
    (wanted: number) => {
      if (bookPlaying && currentId) {
        // The book's own, kept on the server for every device.
        const next = clampSpeed(wanted)
        bookSpeedRef.current = next
        setBookSpeed(next)
        void setPlaybackSpeed(currentId, next).catch(() => undefined)
        return
      }
      setUsualSpeed(wanted)
      void savePodcastPreferences({ speed: wanted }).catch(() => undefined)
      try {
        localStorage.setItem(speedKey, String(wanted))
      } catch {
        // Not remembered here, which is fine: the server has it.
      }
    },
    [bookPlaying, currentId],
  )
  const skipSteps = useMemo(
    () => (bookPlaying && listening ? { back: listening.skipBackSeconds, forward: listening.skipForwardSeconds } : PODCAST_STEPS),
    [bookPlaying, listening],
  )
  const acceptBookOffer = useCallback(() => {
    if (bookOffer) seek(bookOffer.position)
    setBookOffer(null)
  }, [bookOffer, seek])
  const dismissBookOffer = useCallback(() => setBookOffer(null), [])
  const playNextBook = useCallback(() => {
    if (nextBook) dispatch({ type: 'play', tracks: [nextBook] })
    setNextBook(null)
  }, [nextBook])
  const dismissNextBook = useCallback(() => setNextBook(null), [])
  const addBookmark = useCallback(
    async (note?: string) => {
      if (!book) return
      const added = await saveBookmark(book.itemId, Math.floor(engine().currentTime()), note?.trim() || undefined)
      setBook((b) => (b && b.itemId === book.itemId ? { ...b, bookmarks: [...b.bookmarks, added] } : b))
      announceBookmarks(book.itemId, true)
    },
    [book, engine],
  )
  const removeBookmark = useCallback(async (id: string) => {
    await deleteBookmark(id)
    const bookId = bookRef.current?.itemId
    setBook((b) => (b ? { ...b, bookmarks: b.bookmarks.filter((m) => m.id !== id) } : b))
    if (bookId) announceBookmarks(bookId, true)
  }, [])
  // Bookmarks changed elsewhere (the book's page): the playing book's are fetched again.
  const refreshBookmarks = useCallback((bookId: string) => {
    void getBookmarks(bookId).then(
      (bookmarks) => setBook((b) => (b && b.itemId === bookId ? { ...b, bookmarks } : b)),
      () => undefined,
    )
  }, [])
  useBookmarkChanges(book?.itemId, refreshBookmarks, true)
  const countMusicPage = useCallback((delta: 1 | -1) => setMusicPages((n) => n + delta), [])

  // Operating-system media controls.
  const chapterTitle = chapters ? chapters[chapterIndexAt(chapters, time)]?.title : undefined
  useEffect(() => {
    if (!('mediaSession' in navigator) || !current) return
    const bookTrack = isBook(current)
    navigator.mediaSession.metadata = new MediaMetadata({
      title: current.title,
      artist: (bookTrack ? authorsLine(current) : (current.trackArtist ?? current.seriesTitle)) ?? undefined,
      album: bookTrack ? chapterTitle : undefined,
      artwork: current.artwork.poster ? [{ src: imageUrl(current.artwork.poster, 500)!, sizes: '500x500', type: 'image/jpeg' }] : [],
    })
  }, [current, chapterTitle])
  useEffect(() => {
    if (!('mediaSession' in navigator) || !current) return
    navigator.mediaSession.setActionHandler('play', toggle)
    navigator.mediaSession.setActionHandler('pause', toggle)
    navigator.mediaSession.setActionHandler('nexttrack', next)
    navigator.mediaSession.setActionHandler('previoustrack', previous)
    navigator.mediaSession.setActionHandler('seekto', (details) => {
      if (details.seekTime !== undefined) seek(details.seekTime)
    })
    // Podcasts and books: the system's skip buttons go back and on by the steps the player shows.
    const skips = isPodcast(current) || isBook(current)
    try {
      navigator.mediaSession.setActionHandler('seekbackward', skips ? () => skipBy(-skipSteps.back) : null)
      navigator.mediaSession.setActionHandler('seekforward', skips ? () => skipBy(skipSteps.forward) : null)
    } catch {
      // A browser without these actions.
    }
  }, [current, toggle, next, previous, seek, skipBy, skipSteps])

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
      book: bookPlaying ? book : null,
      skipSteps,
      bookOffer,
      acceptBookOffer,
      dismissBookOffer,
      nextBook,
      playNextBook,
      dismissNextBook,
      addBookmark,
      removeBookmark,
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
      bookPlaying,
      book,
      skipSteps,
      bookOffer,
      acceptBookOffer,
      dismissBookOffer,
      nextBook,
      playNextBook,
      dismissNextBook,
      addBookmark,
      removeBookmark,
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

/** Where the chapter playing at `position` ends (before the first chapter, where it starts). */
function endOfChapterAt(book: BookManifest, position: number): number {
  const i = chapterIndexAt(book.chapters, position)
  return i >= 0 ? chapterEnd(book.chapters, i, book.durationSeconds) : (book.chapters[0]?.startSeconds ?? book.durationSeconds)
}
