import type Hls from 'hls.js'
import { getLoudness, reportProgress, startPlayback, stopPlayback, type ItemSummary } from '../api/client'
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
  /** Stops everything and forgets it. */
  stop(): void
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

  const current = () => slots[active]
  const other = () => slots[1 - active]
  const channel = (s: Slot) => (s.channel ??= mixer.channel(s.el))
  const level = (s: Slot) => (host.settings().normalise ? s.gain : 1)

  /** Seconds left in the slot's track, trusting the element once it knows its length; Infinity when nobody knows yet. */
  const remaining = (s: Slot) => {
    const length = Number.isFinite(s.el.duration) && s.el.duration > 0 ? s.el.duration : s.duration
    return length > 0 ? length - s.el.currentTime : Infinity
  }

  /** Forgets what's in a slot and ends its session; `ended` also tells the server the track was played to the end. */
  function release(s: Slot, ended = false) {
    if (s.track && !s.ready) loadCount += 1 // A load still in flight for it is discarded.
    s.hls?.destroy()
    s.hls = null
    if (s.session) {
      if (ended) void reportProgress(s.session, s.el.currentTime, true, true).catch(() => undefined)
      void stopPlayback(s.session)
    }
    s.session = undefined
    s.track = undefined
    s.ready = false
    s.outgoing = false
    s.el.pause()
    s.el.removeAttribute('src')
  }

  /** Starts a session for `track` and points the slot's element at its stream. False when something overtook it. */
  async function fill(s: Slot, track: ItemSummary, index: number, startAt: number): Promise<boolean> {
    const mine = ++loadCount
    s.track = track
    s.ready = false
    s.outgoing = false
    s.duration = track.durationSeconds ?? 0
    s.gain = 1

    const [info, loudness] = await Promise.all([
      startPlayback({ itemId: track.id, profile: detectProfile(), startPositionSeconds: startAt }),
      getLoudness(track.id).catch(() => null),
    ])
    if (mine !== loadCount) {
      void stopPlayback(info.sessionId)
      return false
    }
    s.session = info.sessionId
    s.duration = info.durationSeconds
    s.gain = replayGainScale(loudness, heardAsAlbum(host.queue().tracks, index))

    if (info.method === 'Transcode' && !s.el.canPlayType('application/vnd.apple.mpegurl')) {
      const { default: HlsPlayer } = await import('hls.js/light')
      if (mine !== loadCount) return false // Whatever overtook it has released the slot, session included.
      const player = new HlsPlayer({ startPosition: startAt > 0 ? startAt : -1 })
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
    const { tracks, index } = host.queue()
    const next = tracks[index + 1]
    if (!next) return
    const left = remaining(s)
    const o = other()
    const { crossfade } = host.settings()
    if (o.track !== next) {
      if (!o.outgoing && left <= Math.max(PRELOAD_SECONDS, crossfade + PRELOAD_SECONDS / 2)) void prepare(next, index + 1)
    } else if (o.ready && crossfade > 0 && left <= crossfade && !s.el.paused) {
      switchTo(crossfade, { advance: true, ended: true })
    }
  }

  const handlers = (s: Slot): Record<string, () => void> => ({
    timeupdate: () => {
      if (s !== current()) return
      host.time(s.el.currentTime)
      lookAhead(s)
    },
    play: () => {
      if (s === current()) host.playing(true)
    },
    pause: () => {
      if (s === current()) host.playing(false)
    },
    ended: () => {
      if (s !== current()) {
        release(s, true) // The outgoing track's tail, after a crossfade.
        return
      }
      const { tracks, index } = host.queue()
      const next = tracks[index + 1]
      const o = other()
      if (next && o.ready && o.track === next) switchTo(0, { advance: true, ended: true })
      else {
        if (s.session) void reportProgress(s.session, s.el.currentTime, true, true).catch(() => undefined)
        host.ended()
      }
    },
  })

  return {
    attach(elements) {
      slots = elements.map((el) => ({ el, channel: undefined, hls: null, session: undefined, track: undefined, ready: false, duration: 0, gain: 1, outgoing: false }))
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
      isLoaded = true
      mixer.resume()
      // Next pressed while the next track was ready (or a skip to it): it's already there to play.
      if (startAt === 0 && o.ready && o.track === track && !o.outgoing) {
        switchTo(0, { advance: false, ended: false })
        return
      }
      release(o)
      release(c)
      host.time(startAt)
      host.duration(track.durationSeconds ?? 0)
      if (!(await fill(c, track, index, startAt))) return
      host.duration(c.duration)
      channel(c).set(level(c))
      await c.el.play().catch(() => host.playing(false))
    },

    loaded: () => isLoaded,
    paused: () => current()?.el.paused ?? true,

    play() {
      mixer.resume()
      void current()?.el.play().catch(() => host.playing(false))
      // A track still fading out resumes with it, so the crossfade finishes as it would have.
      if (other()?.outgoing) void other().el.play().catch(() => undefined)
    },

    pause() {
      current()?.el.pause()
      if (other()?.outgoing) other().el.pause()
    },

    seek(seconds) {
      const c = current()
      if (c) c.el.currentTime = seconds
    },

    currentTime: () => current()?.el.currentTime ?? 0,

    queueChanged() {
      const o = other()
      if (!o?.track || o.outgoing) return
      const { tracks, index } = host.queue()
      if (o.track !== tracks[index + 1]) release(o)
    },

    settingsChanged() {
      const c = current()
      // Not mid-crossfade: that ramp is on its way to the right level already.
      if (c?.session && !other().outgoing) channel(c).set(level(c))
    },

    stop() {
      slots.forEach((s) => release(s))
      isLoaded = false
      host.time(0)
      host.duration(0)
      host.playing(false)
    },
  }
}
