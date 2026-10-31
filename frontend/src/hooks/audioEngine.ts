import type Hls from 'hls.js'
import { beaconProgress, getLoudness, reportProgress, startPlayback, stopPlayback, type BookManifest, type ItemSummary } from '../api/client'
import { deviceName, fileAt, isBook, readLocalPlace, saveLocalPlace, smartRewindSeconds, startDecision, timelineAt, type StartDecision } from '../lib/books'
import { heardAsAlbum, replayGainScale } from '../lib/loudness'
import type { MusicSettings } from '../lib/musicSettings'
import { detectProfile } from '../lib/profile'
import { createMixer, type Channel } from './mixer'
import type { QueueState } from './queue'

/**
 * How far before the end the next track is got ready (its session started, its stream loading into the spare element),
 * so that the switch when the current one ends is a call to play() and nothing else. A crossfade needs it ready sooner.
 */
export const PRELOAD_SECONDS = 10

/** How often a podcast episode's place is sent to the server while it plays, so it resumes there anywhere. */
export const PODCAST_PROGRESS_SECONDS = 10

/** How often a book's place is sent to the server while it plays (and on every pause). */
export const BOOK_PROGRESS_SECONDS = 15

/** A podcast episode rather than a track: it resumes, reports where it got to, and plays at the chosen speed. */
export const isPodcast = (track: ItemSummary | undefined) => track?.kind === 'PodcastEpisode'

/** A book being played: its session (one for the whole book), its files on one timeline, and where it was paused. */
interface BookPlay {
  track: ItemSummary
  session: string
  manifest: BookManifest
  /** When it was last paused (a Date.now() time), for smart rewind; null while playing. */
  pausedAt: number | null
  /** When its place was last sent to the server, as a performance.now() time. */
  reportedAt: number
}

/** One <audio> element and what's in it. Two take turns: one plays while the other gets the next track ready. */
interface Slot {
  el: HTMLAudioElement
  /** Its volume control, made on first use (Web Audio wants a user gesture first). */
  channel: Channel | undefined
  hls: Hls | null
  session: string | undefined
  /** The queue entry it holds: the very object, so the same track queued twice is told apart. */
  track: ItemSummary | undefined
  /** True once it has a session and a source: it can start the moment it's asked. */
  ready: boolean
  /** Its length as the server has it, for the bar until the element knows better. */
  duration: number
  /** Its ReplayGain level, as a linear factor (1 without tags). */
  gain: number
  /** Fading out after a crossfade: still sounding, but no longer the current track, and not to be reused yet. */
  outgoing: boolean
  /** When its place was last sent to the server (podcast episodes only), as a performance.now() time. */
  reportedAt: number
  /** For a book: which of its files the element holds. The session is the book's, not the slot's. */
  file: number | undefined
  /** Counts the files opened in it, so a late `loadedmetadata` from an earlier one doesn't move the new one. */
  opened: number
}

/** What the engine needs from the provider: the live queue and settings, and somewhere to report what happened. */
export interface EngineHost {
  queue(): QueueState
  settings(): MusicSettings
  /** The current track's position, in (fractional) seconds. */
  time(seconds: number): void
  playing(playing: boolean): void
  duration(seconds: number): void
  /** The engine moved on to the next track by itself: the queue's index should follow. */
  advanced(): void
  /** The current track ended with nothing ready to take over: the queue decides what happens. */
  ended(): void
  /** How fast `track` plays: 1 for music, the chosen speed for podcasts. */
  rate(track: ItemSummary): number
  /** True when playback should stop once the current track ends (a sleep timer set to the end of the episode). */
  stopAtEnd(): boolean
  /** It stopped at the end, as `stopAtEnd` asked. */
  stoppedAtEnd(): void
  /** The current track couldn't be played, even after trying it again once: the queue decides what happens. */
  failed(track: ItemSummary): void
  /**
   * A book started (with where it started, and a place on another device to offer), or stopped (null). Optional, for
   * hosts that play no books.
   */
  book?(book: { manifest: BookManifest; session: string; decision: StartDecision } | null): void
  /** The book reached its end (and was reported finished). */
  bookEnded?(book: BookManifest): void
  /** Whether smart rewind is on: after a pause, a book starts a little earlier. */
  smartRewind?(): boolean
}

export interface AudioEngine {
  /** Takes charge of the two elements; the returned function lets go of them. */
  attach(elements: HTMLAudioElement[]): () => void
  /** Starts `track` (the queue entry at `index`) from `startAt`, replacing whatever is playing. */
  load(track: ItemSummary, index: number, startAt?: number): Promise<void>
  /** Whether an element holds the current track: a queue restored after a reload hasn't been loaded until Play. */
  loaded(): boolean
  paused(): boolean
  play(): void
  pause(): void
  seek(seconds: number): void
  currentTime(): number
  /** The queue changed: a track got ready as "next" that no longer is next is let go. */
  queueChanged(): void
  /** The level settings changed: the current track takes the new level. */
  settingsChanged(): void
  /** The speed changed: the current track takes it. */
  rateChanged(): void
  /** Stops everything and forgets it. */
  stop(): void
  /** The mix, for the visualiser: null before anything has played, or without Web Audio. */
  analyser(): AnalyserNode | null
  /** Sets how loud the current book plays, from 1 (as it is) to 0, for the sleep timer's fade. */
  volume(level: number): void
  /** Sends the book's place without waiting (the page is going). */
  beacon(): void
}

/**
 * Plays the queue through two <audio> elements for seamless transitions: while one plays, the next track's playback
 * session is started and its stream loaded into the other, so the switch on `ended` is immediate; with a crossfade set,
 * the next starts that many seconds early and the two overlap on ramped gains. ReplayGain is applied per element.
 */
export function createEngine(host: EngineHost): AudioEngine {
  const mixer = createMixer()
  let slots: Slot[] = []
  let active = 0
  let isLoaded = false
  // Skipping quickly starts loads that overtake each other: only the latest gets to touch an element.
  let loadCount = 0
  // The track last tried again after failing: a second failure gives up on it.
  let retried: ItemSummary | undefined

  const current = () => slots[active]
  const other = () => slots[1 - active]
  const channel = (s: Slot) => (s.channel ??= mixer.channel(s.el))
  // The sleep timer's fade, over whatever the level would be.
  let fade = 1
  const level = (s: Slot) => (host.settings().normalise ? s.gain : 1) * fade
  // A book plays its files back to back through both elements on one session; null when no book is playing.
  let book: BookPlay | null = null

  /** Seconds left in the slot's track, trusting the element once it knows its length; Infinity when nobody knows yet. */
  const remaining = (s: Slot) => {
    const length = Number.isFinite(s.el.duration) && s.el.duration > 0 ? s.el.duration : s.duration
    return length > 0 ? length - s.el.currentTime : Infinity
  }

  /** Sets the slot's element to play at its track's speed (setting a source resets it, so this follows each load). */
  const applyRate = (s: Slot) => {
    const rate = s.track ? host.rate(s.track) : 1
    s.el.defaultPlaybackRate = rate
    s.el.playbackRate = rate
  }

  /**
   * Forgets what's in a slot and ends its session; `ended` also tells the server the track was played to the end. A
   * podcast episode left part way through says where, so it picks up there next time.
   */
  function release(s: Slot, ended = false) {
    if (s.track && !s.ready) loadCount += 1 // A load still in flight for it is discarded.
    s.hls?.destroy()
    s.hls = null
    if (s.session) {
      if (ended) void reportProgress(s.session, s.el.currentTime, true, true).catch(() => undefined)
      else if (isPodcast(s.track) && s.el.currentTime > 1) void reportProgress(s.session, s.el.currentTime, true).catch(() => undefined)
      void stopPlayback(s.session)
    }
    s.session = undefined
    s.track = undefined
    s.file = undefined
    s.ready = false
    s.outgoing = false
    s.el.pause()
    s.el.removeAttribute('src')
  }

  // ---------- Books

  /** Where the book is on its whole timeline. */
  const bookTime = () => {
    const c = current()
    return book && c?.file !== undefined ? timelineAt(book.manifest.files, c.file, c.el.currentTime) : 0
  }

  /** Sends the book's place (or that it ended), and keeps it here too, for "Jump to … from …" on the next start. */
  function reportBook(paused: boolean, ended = false) {
    if (!book) return
    const at = ended ? book.manifest.durationSeconds : bookTime()
    book.reportedAt = performance.now()
    saveLocalPlace(book.track.id, ended ? 0 : at)
    void reportProgress(book.session, at, paused, ended).catch(() => undefined)
  }

  /** Lets go of the book: its place is sent (unless it ended, which said so already) and its session ended. */
  function endBook(report = true) {
    if (!book) return
    if (report && current()?.file !== undefined) reportBook(true)
    const session = book.session
    book = null
    void stopPlayback(session)
    host.book?.(null)
  }

  /** Points a slot at one of the book's files, `offset` seconds in. */
  function openFile(s: Slot, index: number, offset: number) {
    const b = book!
    const file = b.manifest.files[index]
    s.hls?.destroy()
    s.hls = null
    s.track = b.track
    s.session = undefined
    s.file = index
    s.ready = true
    s.outgoing = false
    s.duration = file.durationSeconds
    s.gain = 1
    const opened = ++s.opened
    s.el.src = file.url
    if (offset > 0) {
      s.el.addEventListener(
        'loadedmetadata',
        () => {
          if (s.opened === opened) s.el.currentTime = offset
        },
        { once: true },
      )
    }
    applyRate(s)
  }

  /**
   * Starts a book's session and opens the file its first place is in: a place asked for (a chapter, or where a reload
   * left it), else where the server says, unless another device got somewhere else since this browser last played it
   * (that place is offered instead). False when something overtook it.
   */
  async function loadBook(c: Slot, track: ItemSummary, startAt: number): Promise<boolean> {
    const mine = ++loadCount
    c.track = track
    c.ready = false
    const info = await startPlayback({ itemId: track.id, profile: detectProfile(), deviceName: deviceName() })
    if (mine !== loadCount) {
      void stopPlayback(info.sessionId)
      return false
    }
    const manifest = info.book
    if (!manifest || manifest.files.length === 0) {
      void stopPlayback(info.sessionId)
      throw new Error('Not a book this player can play')
    }
    const decision = startDecision(manifest.progress, readLocalPlace(track.id), startAt > 0 ? startAt : track.playFrom)
    book = { track, session: info.sessionId, manifest, pausedAt: null, reportedAt: performance.now() }
    host.book?.({ manifest, session: info.sessionId, decision })
    host.duration(manifest.durationSeconds)
    const place = fileAt(manifest.files, decision.start)
    openFile(c, place.index, place.offset)
    host.time(decision.start)
    return true
  }

  /** Moves to a place on the book's timeline: in the file playing, the one waiting in the spare element, or another. */
  function seekBook(position: number) {
    if (!book) return
    const c = current()
    const playing = !c.el.paused
    const place = fileAt(book.manifest.files, Math.min(Math.max(0, position), book.manifest.durationSeconds))
    host.time(timelineAt(book.manifest.files, place.index, place.offset))
    if (c.file === place.index) {
      c.el.currentTime = place.offset
      return
    }
    const o = other()
    if (o.file === place.index && o.ready && !o.outgoing) {
      active = 1 - active
      channel(o).set(level(o))
      o.el.currentTime = place.offset
      if (playing) void o.el.play().catch(() => host.playing(false))
      release(c)
      return
    }
    openFile(c, place.index, place.offset)
    if (playing) void c.el.play().catch(() => host.playing(false))
  }

  /** Near the end of a file, the next one loads into the spare element, so the switch has no gap. */
  function prepareNextFile(s: Slot) {
    const next = (s.file ?? 0) + 1
    if (!book || next >= book.manifest.files.length) return
    const o = other()
    if (o.file === next || o.outgoing || remaining(s) > PRELOAD_SECONDS) return
    openFile(o, next, 0)
  }

  /** A book's file ended: on to the next (ready in the spare element, ideally), or the book is done. */
  function bookFileEnded(s: Slot) {
    const b = book!
    const next = (s.file ?? 0) + 1
    if (next < b.manifest.files.length) {
      const o = other()
      if (o.file === next && o.ready && !o.outgoing) {
        active = 1 - active
        channel(o).set(level(o))
        void o.el.play().catch(() => host.playing(false))
        release(s)
      } else {
        openFile(s, next, 0)
        void s.el.play().catch(() => host.playing(false))
      }
      return
    }
    // The end: it counts as finished, and the player stops (the next in the series is only ever offered).
    reportBook(true, true)
    endBook(false)
    slots.forEach((slot) => release(slot))
    isLoaded = false
    host.playing(false)
    host.time(0)
    host.bookEnded?.(b.manifest)
    if (host.stopAtEnd()) host.stoppedAtEnd()
  }


  /** Starts a session for `track` and points the slot's element at its stream. False when something overtook it. */
  async function fill(s: Slot, track: ItemSummary, index: number, startAt: number): Promise<boolean> {
    const mine = ++loadCount
    s.track = track
    s.ready = false
    s.outgoing = false
    s.duration = track.durationSeconds ?? 0
    s.gain = 1
    s.reportedAt = performance.now()

    // A podcast episode picks up where it was left (the server knows where) unless asked for a place; it has no levels.
    const podcast = isPodcast(track)
    const [info, loudness] = await Promise.all([
      startPlayback({ itemId: track.id, profile: detectProfile(), startPositionSeconds: podcast && startAt === 0 ? track.playFrom : startAt }),
      podcast ? null : getLoudness(track.id).catch(() => null),
    ])
    if (mine !== loadCount) {
      void stopPlayback(info.sessionId)
      return false
    }
    s.session = info.sessionId
    s.duration = info.durationSeconds
    s.gain = replayGainScale(loudness, heardAsAlbum(host.queue().tracks, index))
    if (podcast && startAt === 0) startAt = info.startPositionSeconds

    if (info.method === 'Transcode' && !s.el.canPlayType('application/vnd.apple.mpegurl')) {
      const { default: HlsPlayer } = await import('hls.js/light')
      if (mine !== loadCount) return false // Whatever overtook it has released the slot, session included.
      const player = new HlsPlayer({ startPosition: startAt > 0 ? startAt : -1 })
      let recovered = false
      player.on(HlsPlayer.Events.ERROR, (_event, data) => {
        if (!data.fatal) return
        // A media error once gets hls.js's own recovery; anything else, or a second one, is a failed track.
        if (data.type === HlsPlayer.ErrorTypes.MEDIA_ERROR && !recovered) {
          recovered = true
          player.recoverMediaError()
        } else fail(s)
      })
      player.loadSource(info.url)
      player.attachMedia(s.el)
      s.hls = player
    } else {
      s.el.src = info.url
      if (startAt > 0) {
        s.el.addEventListener(
          'loadedmetadata',
          () => {
            s.el.currentTime = startAt
          },
          { once: true },
        )
      }
    }
    applyRate(s)
    if (startAt > 0 && s === current()) host.time(startAt)
    s.ready = true
    return true
  }

  /**
   * The spare slot takes over from the current one: at once, or over `fade` seconds with the two overlapping. `advance`
   * moves the queue's index along (not when the queue already did, for a Next that found its track ready); `ended` tells
   * the server the old track was heard to the end.
   */
  function switchTo(fade: number, { advance, ended }: { advance: boolean; ended: boolean }) {
    const from = current()
    const to = other()
    active = 1 - active
    isLoaded = true
    channel(to).set(fade > 0 ? 0 : level(to))
    mixer.resume()
    void to.el.play().catch(() => host.playing(false))
    if (fade > 0) {
      channel(to).ramp(0, level(to), fade)
      channel(from).ramp(level(from), 0, fade)
      from.outgoing = true
      // The outgoing track's own `ended` lets it go; this does when the fade outlives it (a seek, a longer file than the server said).
      const session = from.session
      window.setTimeout(() => {
        if (from.outgoing && from.session === session) release(from, ended)
      }, fade * 1000 + 500)
    } else release(from, ended)
    host.time(0)
    host.duration(to.duration)
    if (advance) host.advanced()
  }

  /**
   * A slot's track wouldn't play (its stream failed, or its session couldn't start). The spare is just let go: the
   * queue starts that track the ordinary way when it's due. The current one is tried again once from where it was,
   * then given up on.
   */
  function fail(s: Slot, at = s.file !== undefined ? bookTime() : s.el.currentTime) {
    const track = s.track
    if (!track) return
    if (s !== current()) {
      release(s)
      return
    }
    if (retried !== track) {
      retried = track
      void engine.load(track, host.queue().index, Number.isFinite(at) ? at : 0)
      return
    }
    retried = undefined
    endBook()
    release(s)
    host.playing(false)
    host.failed(track)
  }

  /** Gets the next track ready in the spare slot. Failures are left to `ended`, which starts it the ordinary way. */
  async function prepare(track: ItemSummary, index: number) {
    const s = other()
    if (s.outgoing) return
    release(s)
    try {
      if (!(await fill(s, track, index, 0))) return
    } catch {
      return
    }
    // Ready late, with a crossfade already due: it starts now rather than at the end.
    const c = current()
    if (!c.el.paused) lookAhead(c)
  }

  /** On every tick of the current track: time to get the next ready, or to start the crossfade into it? */
  function lookAhead(s: Slot) {
    // A book is its own queue: it gets its next file ready, not the next entry.
    if (book) return
    const { tracks, index } = host.queue()
    const next = tracks[index + 1]
    if (!next) return
    const left = remaining(s)
    const o = other()
    // Talk doesn't crossfade: an episode ends and the next begins.
    const crossfade = isPodcast(s.track) || isPodcast(next) ? 0 : host.settings().crossfade
    if (o.track !== next) {
      if (!o.outgoing && left <= Math.max(PRELOAD_SECONDS, crossfade + PRELOAD_SECONDS / 2)) void prepare(next, index + 1)
    } else if (o.ready && crossfade > 0 && left <= crossfade && !s.el.paused) {
      switchTo(crossfade, { advance: true, ended: true })
    }
  }

  const handlers = (s: Slot): Record<string, () => void> => ({
    // Only a direct stream's: hls.js reports its own failures, and the element is let go before its error matters.
    error: () => {
      if (!s.hls) fail(s)
    },
    timeupdate: () => {
      if (s !== current()) return
      if (book && s.file !== undefined) {
        host.time(timelineAt(book.manifest.files, s.file, s.el.currentTime))
        if (!s.el.paused && performance.now() - book.reportedAt > BOOK_PROGRESS_SECONDS * 1000) reportBook(false)
        prepareNextFile(s)
        return
      }
      host.time(s.el.currentTime)
      if (isPodcast(s.track) && s.session && !s.el.paused && performance.now() - s.reportedAt > PODCAST_PROGRESS_SECONDS * 1000) {
        s.reportedAt = performance.now()
        void reportProgress(s.session, s.el.currentTime, false).catch(() => undefined)
      }
      lookAhead(s)
    },
    play: () => {
      if (s !== current()) return
      host.playing(true)
      if (s.el.playbackRate !== host.rate(s.track!)) applyRate(s)
    },
    pause: () => {
      if (s !== current()) return
      host.playing(false)
      // A book's place, on every pause (not the one at the end of each file).
      if (book && s.file !== undefined) {
        if (!s.el.ended) reportBook(true)
        return
      }
      // Paused part way through an episode: the place is kept, for picking it up on another device.
      if (isPodcast(s.track) && s.session && !s.el.ended) {
        s.reportedAt = performance.now()
        void reportProgress(s.session, s.el.currentTime, true).catch(() => undefined)
      }
    },
    ended: () => {
      if (s !== current()) {
        release(s, true) // The outgoing track's tail, after a crossfade.
        return
      }
      if (book && s.file !== undefined) {
        bookFileEnded(s)
        return
      }
      const { tracks, index } = host.queue()
      const next = tracks[index + 1]
      const o = other()
      if (host.stopAtEnd()) {
        // The sleep timer said to stop here: the episode counts as heard, and nothing else starts.
        if (s.session) void reportProgress(s.session, s.el.currentTime, true, true).catch(() => undefined)
        host.playing(false)
        host.stoppedAtEnd()
      } else if (next && o.ready && o.track === next) switchTo(0, { advance: true, ended: true })
      else {
        if (s.session) void reportProgress(s.session, s.el.currentTime, true, true).catch(() => undefined)
        host.ended()
      }
    },
  })

  const engine: AudioEngine = {
    attach(elements) {
      slots = elements.map((el) => ({
        el,
        channel: undefined,
        hls: null,
        session: undefined,
        track: undefined,
        ready: false,
        duration: 0,
        gain: 1,
        outgoing: false,
        reportedAt: 0,
        file: undefined,
        opened: 0,
      }))
      const detach = slots.map((s) => {
        const on = handlers(s)
        Object.entries(on).forEach(([event, handler]) => s.el.addEventListener(event, handler))
        return () => Object.entries(on).forEach(([event, handler]) => s.el.removeEventListener(event, handler))
      })
      return () => detach.forEach((off) => off())
    },

    async load(track, index, startAt = 0) {
      const c = current()
      const o = other()
      if (!c) return
      if (retried !== track) retried = undefined
      isLoaded = true
      mixer.resume()
      // A book playing is let go first, its place sent while its element still has it.
      endBook()
      if (isBook(track)) {
        release(o)
        release(c)
        host.time(startAt)
        host.duration(track.durationSeconds ?? 0)
        try {
          if (!(await loadBook(c, track, startAt))) return
        } catch {
          fail(c, startAt)
          return
        }
        channel(c).set(level(c))
        await c.el.play().catch(() => host.playing(false))
        return
      }
      // Next pressed while the next track was ready (or a skip to it): it's already there to play.
      if (startAt === 0 && o.ready && o.track === track && !o.outgoing) {
        switchTo(0, { advance: false, ended: false })
        return
      }
      release(o)
      release(c)
      host.time(startAt)
      host.duration(track.durationSeconds ?? 0)
      try {
        if (!(await fill(c, track, index, startAt))) return
      } catch {
        // The server wouldn't start it (busy, gone, offline).
        fail(c, startAt)
        return
      }
      host.duration(c.duration)
      channel(c).set(level(c))
      await c.el.play().catch(() => host.playing(false))
    },

    loaded: () => isLoaded,
    paused: () => current()?.el.paused ?? true,

    play() {
      mixer.resume()
      // Smart rewind: back a little after a pause, more the longer it was.
      if (book && book.pausedAt !== null) {
        const back = host.smartRewind?.() ? smartRewindSeconds((Date.now() - book.pausedAt) / 1000) : 0
        book.pausedAt = null
        if (back > 0) seekBook(bookTime() - back)
      }
      void current()?.el.play().catch(() => host.playing(false))
      // A track still fading out resumes with it, so the crossfade finishes as it would have.
      if (other()?.outgoing) void other().el.play().catch(() => undefined)
    },

    pause() {
      if (book && !current()?.el.paused) book.pausedAt = Date.now()
      current()?.el.pause()
      if (other()?.outgoing) other().el.pause()
    },

    seek(seconds) {
      if (book) return seekBook(seconds)
      const c = current()
      if (c) c.el.currentTime = seconds
    },

    currentTime: () => (book ? bookTime() : (current()?.el.currentTime ?? 0)),

    queueChanged() {
      const o = other()
      // A book's spare element holds its next file, whatever the queue says.
      if (!o?.track || o.outgoing || o.file !== undefined) return
      const { tracks, index } = host.queue()
      if (o.track !== tracks[index + 1]) release(o)
    },

    settingsChanged() {
      const c = current()
      // Not mid-crossfade: that ramp is on its way to the right level already.
      if (c?.session && !other().outgoing) channel(c).set(level(c))
    },

    rateChanged() {
      slots.forEach((s) => s.track && applyRate(s))
    },

    stop() {
      endBook()
      slots.forEach((s) => release(s))
      isLoaded = false
      host.time(0)
      host.duration(0)
      host.playing(false)
    },

    analyser: () => mixer.analyser(),

    volume(next) {
      fade = Math.min(1, Math.max(0, next))
      const c = current()
      if (c?.track && !other().outgoing) channel(c).set(level(c))
    },

    beacon() {
      if (!book) return
      saveLocalPlace(book.track.id, bookTime())
      beaconProgress(book.session, bookTime())
    },
  }
  return engine
}
