import Hls from 'hls.js/light'
import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import {
  ApiError,
  beaconProgress,
  imageUrl,
  reportProgress,
  setAudioDelay,
  setPlaybackPreferences,
  setPlaybackSpeed,
  setSubtitleChoice,
  setSubtitleDelay,
  startPlayback,
  stopPlayback,
  type PlaybackInfo,
  type PlaybackPreferences,
} from '../api/client'
import type { SubtitleSize } from '../api/client'
import {
  Back,
  Captions,
  ExitFullscreen,
  Forward10,
  Fullscreen,
  Muted,
  Next,
  Pause,
  Play,
  Replay10,
  Reset,
  Sleep,
  Volume,
} from '../components/icons'
import { MAX_AUDIO_DELAY_SECONDS, useDelayedSound } from '../hooks/delayedSound'
import { announceWatched } from '../hooks/watched'
import { episodeCode, formatClock } from '../lib/format'
import { belongsToFocused } from '../lib/focusedKeys'
import { mediaErrorRecovery } from '../lib/hlsRecovery'
import { speedLabel } from '../lib/podcasts'
import { detectProfile, prefersNativeHls } from '../lib/profile'
import { extended, FADE_SECONDS, minutesLeft, secondsLeft, sleepChoices, sleepLabel, STILL_WATCHING_SECONDS, type SleepTimer } from '../lib/sleepTimer'

type Quality = 'auto' | 1080 | 720 | 480
const qualities: { value: Quality; label: string; bitrate?: number }[] = [
  { value: 'auto', label: 'Best for this device' },
  { value: 1080, label: '1080p', bitrate: 10_000_000 },
  { value: 720, label: '720p', bitrate: 4_000_000 },
  { value: 480, label: '480p', bitrate: 1_500_000 },
]

/** The speeds the player offers; an episode's is kept for its show. */
const speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2] as const

/** The countdown between one episode finishing and the next starting. */
const UP_NEXT_SECONDS = 5

/**
 * The next episode's session, started during the credits so it opens at once. Kept outside the page so it survives
 * the move to the next episode's address, which is the same page with another id.
 */
let prepared: { itemId: string; info: PlaybackInfo } | null = null

/** Lets the prepared session go (best effort): it wasn't wanted, or something else is playing. */
function dropPrepared() {
  if (prepared) void stopPlayback(prepared.info.sessionId).catch(() => undefined)
  prepared = null
}

const defaultPreferences: PlaybackPreferences = { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Standard' }

/** How much bigger than standard each subtitle size draws (player.css scales ::cue by --cue-scale). */
export const subtitleScale: Record<SubtitleSize, number> = { Small: 0.8, Standard: 1, Large: 1.25, ExtraLarge: 1.5 }
const subtitleSizes: { value: SubtitleSize; label: string }[] = [
  { value: 'Small', label: 'Small' },
  { value: 'Standard', label: 'Standard' },
  { value: 'Large', label: 'Large' },
  { value: 'ExtraLarge', label: 'Extra large' },
]

/** "In time", "0.5s later", "1.25s earlier". */
function delayLabel(seconds: number) {
  if (seconds === 0) return 'In time'
  return `${Math.abs(seconds)}s ${seconds > 0 ? 'later' : 'earlier'}`
}

/** "In sync", "+0.15 s" (the sound later), "−0.2 s" (earlier). */
function syncLabel(seconds: number) {
  if (seconds === 0) return 'In sync'
  return `${seconds > 0 ? '+' : '−'}${Math.abs(seconds)} s`
}
const HIDE_CHROME_MS = 3000
const STREAM_STOPPED = 'The stream stopped. The server may have had trouble converting this file.'
const TAKEN_OVER = 'Someone started watching on another screen, so this one had to stop.'
// Long enough for a server that's up, short enough that a hung one doesn't hold up the recovery.
const REASON_TIMEOUT_MS = 5000

/**
 * What went wrong, and so what the viewer can do: take the stream back from the screen it was ended for
 * (takenOver), wait for the server to have room (busy), or try again.
 */
interface PlayerError {
  message: string
  kind: 'takenOver' | 'busy' | 'other'
}

interface StartOptions {
  audioStreamIndex?: number
  subtitleKey?: string
  startPositionSeconds?: number
  quality?: Quality
  forceTranscode?: boolean
}

/** Why the server ended this transcode for another viewer, if it did (its playlist answers 410 with the reason). */
async function takenOverReason(sessionId: string): Promise<string | undefined> {
  const response = await fetch(`/api/v1/playback/${sessionId}/main.m3u8`, { signal: AbortSignal.timeout(REASON_TIMEOUT_MS) }).catch(() => null)
  if (response?.status !== 410) return undefined
  const problem = (await response.json().catch(() => null)) as { detail?: string } | null
  return problem?.detail ?? TAKEN_OVER
}

export function PlayerPage() {
  const { id = '' } = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  // Shuffling a show: the page opens with the show's id, and each Next is a random episode.
  const shuffle = params.get('shuffle') === '1'
  const shuffleRef = useRef(shuffle)
  shuffleRef.current = shuffle
  // The episode actually playing, once known: a shuffle starts from the show, and restarts (another audio track, a
  // recovery) must carry on with this episode rather than pick a new one.
  const playingItem = useRef<string | undefined>(undefined)

  const container = useRef<HTMLDivElement>(null)
  const video = useRef<HTMLVideoElement>(null)
  const hls = useRef<Hls | null>(null)
  const session = useRef<string | undefined>(undefined)
  const hideTimer = useRef<number | undefined>(undefined)
  const recoverRef = useRef<() => Promise<void>>(async () => undefined)
  const recovering = useRef(false)
  const lastRecovery = useRef(0)
  // Ends when the item changes or the page closes, so a recovery in progress gives up.
  const lifetime = useRef({ alive: true })
  // Counts playback requests, so only the latest one's answer is used.
  const requests = useRef(0)

  const [info, setInfo] = useState<PlaybackInfo>()
  const [error, setError] = useState<PlayerError>()
  const [playing, setPlaying] = useState(false)
  const [waiting, setWaiting] = useState(true)
  const [time, setTime] = useState(0)
  const [bufferedTo, setBufferedTo] = useState(0)
  const [volume, setVolume] = useState(1)
  const [muted, setMuted] = useState(false)
  const [fullscreen, setFullscreen] = useState(false)
  const [chrome, setChrome] = useState(true)
  const [menu, setMenu] = useState<'subtitles' | 'audio' | 'quality' | 'speed' | 'sleep' | null>(null)
  const [quality, setQuality] = useState<Quality>('auto')
  const [subtitleKey, setSubtitleKey] = useState<string | null>(null)
  // Embedded subtitles are extracted on first use, which can take a little while for large files.
  const [subtitleStatus, setSubtitleStatus] = useState<'idle' | 'loading' | 'failed'>('idle')
  const [upNextCancelled, setUpNextCancelled] = useState(false)
  const [countdown, setCountdown] = useState(UP_NEXT_SECONDS)
  // The episode is over (it ended, or its credits began with Skip credits on): the countdown to the next one runs.
  const [finished, setFinished] = useState(false)
  const [preferences, setPreferences] = useState<PlaybackPreferences>(defaultPreferences)
  const [subtitleDelay, setSubtitleDelayState] = useState(0)
  // The file's sound offset, as last nudged here (the server is told a moment after the nudging stops).
  const [audioDelay, setAudioDelayState] = useState(0)
  const syncTimer = useRef<number | undefined>(undefined)
  const [speed, setSpeed] = useState(1)
  // The sleep timer runs across episodes, and stopping leaves the picture dimmed until the viewer comes back.
  const [sleep, setSleep] = useState<SleepTimer | null>(null)
  const [asleep, setAsleep] = useState(false)
  const [clock, setClock] = useState(() => Date.now())
  // The volume before the timer started fading it, put back when it's done.
  const fadeFrom = useRef<number | null>(null)
  const scale = subtitleScale[preferences.subtitleSize ?? 'Standard']
  const [notice, setNotice] = useState<string>()
  // The latest request to get the next episode ready, so an older answer arriving late is let go.
  const preparing = useRef<{ itemId: string } | null>(null)
  // Intros and recaps already skipped by themselves, so seeking back into one to watch it isn't overruled.
  const autoSkipped = useRef(new Set<string>())

  const duration = info?.durationSeconds ?? 0

  // Watching may have marked the title watched (past the credits, or to the end): leaving it tells pages that come
  // back afterwards, such as a library grid restored on Back, to refresh its tick.
  // Once per title, so the one announced is the one just played (Next episode moves on to another).
  const playedId = info?.item.id
  useEffect(() => {
    const played = info?.item
    if (!played) return
    return () => announceWatched(played)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per title; its id stands for it
  }, [playedId])
  const [previews, setPreviews] = useState<Previews | null>(null)

  // Scrubbing previews are made the first time a file plays; check now, then again while they're being made.
  useEffect(() => {
    if (!info) return
    setPreviews(null)
    let cancelled = false
    let attempts = 0
    const base = `/api/v1/playback/${info.sessionId}`
    const check = async () => {
      const response = await fetch(`${base}/trickplay.json`).catch(() => null)
      if (cancelled) return
      if (response?.ok) setPreviews({ ...((await response.json()) as Omit<Previews, 'base'>), base })
      else if (++attempts < 12) timer = window.setTimeout(check, 20_000)
    }
    let timer = window.setTimeout(check, 0)
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [info])

  // ---------- Session lifecycle

  const attach = useCallback((playback: PlaybackInfo, position: number) => {
    const el = video.current
    if (!el) return
    hls.current?.destroy()
    hls.current = null
    setWaiting(true)

    const seekOnLoad = () => {
      if (position > 0) el.currentTime = position
      el.play().catch(() => setPlaying(false))
    }

    if (playback.method === 'Transcode' && !prefersNativeHls() && Hls.isSupported()) {
      // Subtitles are rendered as <track>s, so leave them out of the HLS manifest.
      const player = new Hls({ startPosition: position, maxBufferLength: 60, backBufferLength: 90 })
      // Media errors get hls.js's own recovery first; one that keeps coming back asks the server for a fresh stream.
      const onMediaError = mediaErrorRecovery(player, () => void recoverRef.current())
      player.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) onMediaError()
        else void recoverRef.current()
      })
      player.loadSource(`${playback.url}?subtitles=false`)
      player.attachMedia(el)
      player.once(Hls.Events.MANIFEST_PARSED, () => el.play().catch(() => setPlaying(false)))
      hls.current = player
    } else {
      el.src = playback.method === 'Transcode' ? `${playback.url}?subtitles=false` : playback.url
      el.addEventListener('loadedmetadata', seekOnLoad, { once: true })
    }
  }, [])

  /** Starts (or restarts) playback. True once settled: playing, or refused (busy server, stream taken over) with the error shown. */
  const start = useCallback(
    async (options: StartOptions = {}, quiet = false) => {
      setError(undefined)
      const q = options.quality ?? quality
      const preset = qualities.find((x) => x.value === q)
      // Leaving for another title (or asking again, say for another quality) while this request is out makes its
      // answer stale: that session is stopped rather than played over the newer one.
      const life = lifetime.current
      const request = ++requests.current
      const replaces = session.current
      try {
        // The next episode, got ready during the last one's credits, opens straight away.
        const ready = !options.audioStreamIndex && !options.subtitleKey && !options.forceTranscode && !replaces && prepared?.itemId === id ? prepared.info : null
        if (ready) prepared = null
        else dropPrepared()
        const playback = ready ?? await startPlayback({
          itemId: playingItem.current ?? id,
          shuffle: shuffleRef.current || undefined,
          profile: detectProfile(),
          audioStreamIndex: options.audioStreamIndex,
          subtitleKey: options.subtitleKey,
          startPositionSeconds: options.startPositionSeconds,
          maxHeight: q === 'auto' ? undefined : q,
          maxBitrate: preset?.bitrate,
          forceTranscode: options.forceTranscode || q !== 'auto' || undefined,
          replacesSessionId: replaces,
        })
        if (!life.alive || life !== lifetime.current || request !== requests.current) {
          void stopPlayback(playback.sessionId)
          // Nothing left for this request to do: a newer one (or none, the page has moved on) is in charge.
          return true
        }
        if (session.current && session.current !== playback.sessionId) void stopPlayback(session.current)
        session.current = playback.sessionId
        // The same title again (another audio track, a recovery) keeps its speed; another starts at its show's.
        const sameTitle = playingItem.current === playback.item.id
        playingItem.current = playback.item.id
        setInfo(playback)
        setAudioDelayState(playback.audioDelaySeconds ?? 0)
        setSpeed((current) => playback.speed ?? (sameTitle ? current : 1))
        if (playback.preferences) setPreferences({ ...defaultPreferences, ...playback.preferences })
        setSubtitleDelayState(playback.subtitleDelaySeconds ?? 0)
        setSubtitleKey(playback.selectedSubtitleKey)
        attach(playback, options.startPositionSeconds ?? playback.startPositionSeconds)
        return true
      } catch (e) {
        // A server at its transcode limit, or a stream ended for another screen (410), is an answer, not a hiccup:
        // say so rather than retrying, which would only take the stream back from the other screen.
        const status = e instanceof ApiError ? e.status : undefined
        // Asking again with an ended stream's id is refused for as long as the server remembers it, so whatever the
        // viewer does next (Watch here instead, another audio track) starts afresh. A stream the server just doesn't
        // know any more is kept: asking with it never takes another screen's place, so Try again waits its turn.
        if (status === 410 && session.current === replaces) session.current = undefined
        const refused = status === 503 || status === 410
        if ((!quiet || refused) && request === requests.current) {
          setError({ message: e instanceof Error ? e.message : String(e), kind: status === 410 ? 'takenOver' : status === 503 ? 'busy' : 'other' })
        }
        return refused
      }
    },
    [id, quality, attach],
  )

  // A transcode lives in the server's memory, so a server restart (an upgrade, say) ends it mid-stream. Ask for a
  // fresh one from where we were before saying anything, retrying while the server comes back up. At most once a
  // minute: a stream that keeps stopping is a real problem worth showing.
  const recover = async () => {
    const el = video.current
    if (recovering.current) return
    // Claimed before asking why, so a second error meanwhile doesn't start another recovery.
    recovering.current = true
    const life = lifetime.current
    const failed = session.current
    try {
      // Nothing of ours is playing (the title was left, or the old stream erred after being let go): nothing to recover.
      if (failed === undefined) return
      // A stream ended to make way for another screen says so (410), even inside the once-a-minute limit.
      const takenOver = failed ? await takenOverReason(failed) : undefined
      // Left for another title, or started another stream (an audio change, Next episode), while that was out.
      if (!life.alive || life !== lifetime.current || session.current !== failed) return
      if (takenOver) {
        session.current = undefined // Watch here instead starts afresh (see start).
        return setError({ message: takenOver, kind: 'takenOver' })
      }
      if (!el || Date.now() - lastRecovery.current < 60_000) return setError({ message: STREAM_STOPPED, kind: 'other' })
      lastRecovery.current = Date.now()
      const position = el.currentTime
      hls.current?.destroy()
      hls.current = null
      setWaiting(true)
      for (let attempt = 0; attempt < 6; attempt++) {
        if (attempt > 0) await new Promise((resolve) => setTimeout(resolve, 3000))
        if (!life.alive || session.current !== failed) return
        const settled = await start(
          { startPositionSeconds: position, audioStreamIndex: info?.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' },
          true,
        )
        if (settled) return // Playing again, or the server said why not.
      }
      if (life.alive) setError({ message: STREAM_STOPPED, kind: 'other' })
    } finally {
      recovering.current = false
    }
  }
  useEffect(() => {
    recoverRef.current = recover
  })

  useEffect(() => {
    setUpNextCancelled(false)
    setCountdown(UP_NEXT_SECONDS)
    setFinished(false)
    autoSkipped.current = new Set()
    playingItem.current = undefined
    const t = params.get('t')
    const el = video.current
    const life = { alive: true }
    lifetime.current = life
    void start({ startPositionSeconds: t !== null ? Number(t) : undefined })
    return () => {
      life.alive = false
      hls.current?.destroy()
      // A direct or native stream is let go too, so its late errors can't start a recovery for the next title.
      if (el) {
        el.removeAttribute('src')
        el.load()
      }
      if (session.current) {
        beaconProgress(session.current, el?.currentTime ?? 0)
        void stopPlayback(session.current)
        session.current = undefined
      }
    }
    // Restart only when the item changes; option changes call start() themselves.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  /** Re-request playback at the current position with different options. */
  const restartHere = (options: StartOptions) => start({ startPositionSeconds: video.current?.currentTime ?? 0, ...options })

  /** The error's main button: from where it stopped, or (refused before anything played) from where the title was asked for. */
  const retry = (options: StartOptions = {}) => {
    const t = params.get('t')
    const keep = { audioStreamIndex: info?.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' }
    return info ? restartHere({ ...keep, ...options }) : start({ startPositionSeconds: t !== null ? Number(t) : undefined, ...options })
  }

  // Nothing behind the error can be used, so the stream that stopped doesn't play on from what it had buffered.
  useEffect(() => {
    if (error) video.current?.pause()
  }, [error])

  // ---------- Progress reporting

  useEffect(() => {
    const report = () => {
      const el = video.current
      if (session.current && el && el.currentTime > 0) void reportProgress(session.current, el.currentTime, el.paused).catch(() => undefined)
    }
    const timer = setInterval(() => !video.current?.paused && report(), 10_000)
    const onHide = () => document.visibilityState === 'hidden' && session.current && beaconProgress(session.current, video.current?.currentTime ?? 0)
    document.addEventListener('visibilitychange', onHide)
    const el = video.current
    el?.addEventListener('pause', report)
    return () => {
      clearInterval(timer)
      document.removeEventListener('visibilitychange', onHide)
      el?.removeEventListener('pause', report)
    }
  }, [])

  // ---------- Video element state

  useEffect(() => {
    const el = video.current
    if (!el) return
    // timeupdate fires about four times a second; whole seconds are all the controls show, and React skips a
    // render when the value hasn't changed.
    const onTime = () => {
      setTime(Math.floor(el.currentTime))
      for (let i = 0; i < el.buffered.length; i++) {
        if (el.buffered.start(i) <= el.currentTime + 0.5 && el.buffered.end(i) >= el.currentTime) setBufferedTo(Math.floor(el.buffered.end(i)))
      }
    }
    const onPlay = () => setPlaying(true)
    const onPause = () => setPlaying(false)
    const onWaiting = () => setWaiting(true)
    const onPlaying = () => setWaiting(false)
    const onVolume = () => {
      setVolume(el.volume)
      setMuted(el.muted)
    }
    const onError = () => {
      // A browser that claimed it could play the file but can't: convert instead.
      if (info?.method === 'DirectPlay') void restartHere({ forceTranscode: true })
      else if (info?.method === 'Transcode') void recoverRef.current()
      else setError({ message: "This file couldn't be played.", kind: 'other' })
    }
    const onEnded = () => {
      if (session.current) void reportProgress(session.current, el.currentTime, true, true).catch(() => undefined)
      if (info?.next && !upNextCancelled) setFinished(true)
    }
    el.addEventListener('timeupdate', onTime)
    el.addEventListener('play', onPlay)
    el.addEventListener('pause', onPause)
    el.addEventListener('waiting', onWaiting)
    el.addEventListener('playing', onPlaying)
    el.addEventListener('canplay', onPlaying)
    el.addEventListener('volumechange', onVolume)
    el.addEventListener('error', onError)
    el.addEventListener('ended', onEnded)
    return () => {
      el.removeEventListener('timeupdate', onTime)
      el.removeEventListener('play', onPlay)
      el.removeEventListener('pause', onPause)
      el.removeEventListener('waiting', onWaiting)
      el.removeEventListener('playing', onPlaying)
      el.removeEventListener('canplay', onPlaying)
      el.removeEventListener('volumechange', onVolume)
      el.removeEventListener('error', onError)
      el.removeEventListener('ended', onEnded)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [info, upNextCancelled])

  // Subtitles sit clear of the bottom edge, and rise above the controls while they're showing. Cues are placed by
  // their top edge as a percentage of the video's height (Chrome ignores lineAlign), so each cue's own height (its
  // lines at the ::cue font size in player.css) is allowed for and multi-line cues grow upwards. A track's cues (a
  // film has a thousand or two) are all placed once, when they've loaded or the space changes; after that each
  // cuechange touches only the cues on screen.
  const controls = useRef<HTMLElement>(null)
  useEffect(() => {
    const el = video.current
    const tracks = el?.textTracks
    if (!el || !tracks || typeof VTTCue === 'undefined') return
    let placed = new WeakSet<TextTrack>()
    const place = () => {
      const height = el.getBoundingClientRect().height || innerHeight
      const lift = chrome && controls.current ? controls.current.getBoundingClientRect().height + 20 : height * 0.08
      const linePx = Math.min(32, Math.max(17.6, innerWidth * 0.024)) * scale * 1.3 + 2
      for (const track of Array.from(el.textTracks)) {
        if (track.mode !== 'showing' || !track.cues) continue
        const cues = placed.has(track) ? track.activeCues : track.cues
        if (track.cues.length) placed.add(track)
        for (const cue of Array.from(cues ?? [])) {
          if (!(cue instanceof VTTCue)) continue
          const lines = cue.text.split('\n').length
          cue.snapToLines = false
          cue.lineAlign = 'start'
          cue.line = Math.max(5, 100 - ((lift + lines * linePx) / height) * 100)
        }
      }
    }
    place()
    const watch = (track: TextTrack) => track.addEventListener('cuechange', place)
    const onAdd = (e: TrackEvent) => e.track && watch(e.track as TextTrack)
    for (const track of Array.from(tracks)) watch(track)
    tracks.addEventListener('addtrack', onAdd)
    tracks.addEventListener('change', place)
    const onResize = () => {
      placed = new WeakSet()
      place()
    }
    window.addEventListener('resize', onResize)
    return () => {
      for (const track of Array.from(tracks)) track.removeEventListener('cuechange', place)
      tracks.removeEventListener('addtrack', onAdd)
      tracks.removeEventListener('change', place)
      window.removeEventListener('resize', onResize)
    }
  }, [chrome, subtitleKey, scale])

  // Show the chosen text subtitle track (the <track> is swapped when the choice changes).
  useEffect(() => {
    const el = video.current
    if (!el) return
    for (const track of Array.from(el.textTracks)) track.mode = track.label === subtitleKey ? 'showing' : 'disabled'
  }, [subtitleKey, info])

  useEffect(() => {
    const onChange = () => setFullscreen(!!document.fullscreenElement)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  // ---------- Chrome (controls) visibility

  const wake = useCallback(() => {
    setChrome(true)
    setAsleep(false)
    window.clearTimeout(hideTimer.current)
    hideTimer.current = window.setTimeout(() => setChrome(false), HIDE_CHROME_MS)
  }, [])

  useEffect(() => {
    wake()
    return () => window.clearTimeout(hideTimer.current)
  }, [wake])

  const chromeShown = chrome || !playing || menu !== null || !!error

  // An open menu closes at a press anywhere outside it. A press on the picture only closes it (it doesn't pause too);
  // another control goes on to do its own thing, opening its menu instead, say.
  const closedByPress = useRef(false)
  useEffect(() => {
    if (menu === null) return
    const onPress = (e: PointerEvent) => {
      if (e.target instanceof Element && e.target.closest('.player-menu, .menu-anchor > button[aria-expanded="true"]')) return
      closedByPress.current = e.target === video.current
      setMenu(null)
    }
    document.addEventListener('pointerdown', onPress)
    return () => document.removeEventListener('pointerdown', onPress)
  }, [menu])

  // ---------- Actions

  const toggle = () => {
    const el = video.current
    if (!el) return
    if (el.paused) void el.play()
    else el.pause()
  }
  const seekBy = (delta: number) => {
    const el = video.current
    if (el) el.currentTime = Math.min(Math.max(0, el.currentTime + delta), duration || el.duration)
  }
  const seekTo = (seconds: number) => {
    const el = video.current
    if (el) el.currentTime = seconds
  }
  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen()
    else void container.current?.requestFullscreen?.()
  }
  const exit = () => {
    const back = info?.ancestors[0]?.id ?? info?.item.id
    navigate(back ? `/item/${info?.item.kind === 'Episode' ? info.item.id : back}` : '/')
  }

  function chooseSubtitle(key: string | null) {
    setMenu(null)
    const track = info?.subtitles.find((s) => s.key === key)
    // Picture subtitles are burned into the video by the server, which means a new stream.
    if ((track && !track.isText) || info?.subtitleBurnedIn) {
      void restartHere({ subtitleKey: key ?? 'off', audioStreamIndex: info?.selectedAudioIndex ?? undefined })
    } else {
      setSubtitleKey(key)
      setSubtitleStatus(key ? 'loading' : 'idle')
      // Switched here in the browser, so the server must be told for the choice to hold next time.
      if (info) void setSubtitleChoice(info.sessionId, key).catch(() => undefined)
    }
  }

  function chooseAudio(index: number) {
    setMenu(null)
    if (index !== info?.selectedAudioIndex) void restartHere({ audioStreamIndex: index, subtitleKey: subtitleKey ?? 'off' })
  }

  function chooseQuality(q: Quality) {
    setMenu(null)
    setQuality(q)
    void restartHere({ quality: q, audioStreamIndex: info?.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' })
  }

  // ---------- Markers and Up Next

  const intro = info?.markers.find((m) => m.type === 'Intro')
  const recap = info?.markers.find((m) => m.type === 'Recap')
  const credits = info?.markers.find((m) => m.type === 'Credits')
  const skippable = [recap, intro].find((m) => m && time >= m.start && time < m.end - 1)
  const upNextAt = credits?.start ?? (duration ? duration - 25 : Infinity)
  // Offered from the credits on, as a button; once the episode is over, with the countdown if auto-play is on.
  const showUpNext = !!info?.next && !upNextCancelled && (finished || (time >= upNextAt && duration > 0))
  const counting = finished && preferences.autoPlayNext && showUpNext

  const playNext = useCallback(() => {
    if (info?.next) navigate(`/play/${info.next.id}?t=0${shuffle ? '&shuffle=1' : ''}`, { replace: true })
  }, [info, navigate, shuffle])

  // Skip intros and recaps by themselves when the profile asks, once each.
  useEffect(() => {
    if (!skippable || error) return
    const key = `${skippable.type}@${skippable.start}`
    const wanted = skippable.type === 'Intro' ? preferences.skipIntros : preferences.skipRecaps
    if (!wanted || autoSkipped.current.has(key)) return
    autoSkipped.current.add(key)
    seekTo(skippable.end)
    setNotice(skippable.type === 'Recap' ? 'Skipped the recap' : 'Skipped the intro')
    // eslint-disable-next-line react-hooks/exhaustive-deps -- seekTo is a plain function of this render
  }, [skippable, preferences, error])

  useEffect(() => {
    if (!notice) return
    const timer = setTimeout(() => setNotice(undefined), 2500)
    return () => clearTimeout(timer)
  }, [notice])

  // Skip credits: the episode is over as soon as they start.
  useEffect(() => {
    if (!preferences.skipCredits || !credits || !info?.next || upNextCancelled || finished || time < credits.start) return
    video.current?.pause()
    if (session.current) void reportProgress(session.current, time, true, true).catch(() => undefined)
    setFinished(true)
  }, [preferences.skipCredits, credits, info, upNextCancelled, finished, time])

  useEffect(() => {
    if (finished) setCountdown(UP_NEXT_SECONDS)
  }, [finished])

  useEffect(() => {
    if (!counting) return
    const timer = setInterval(() => setCountdown((c) => c - 1), 1000)
    return () => clearInterval(timer)
  }, [counting])

  useEffect(() => {
    if (counting && countdown <= 0) playNext()
  }, [countdown, counting, playNext])

  // Get the next episode ready once the Up Next card shows (the credits, or the last moments), and let it go if the
  // viewer heads back into the episode.
  useEffect(() => {
    const next = info?.next
    if (!next) return
    if (!showUpNext) {
      if (prepared && !finished) dropPrepared()
      return
    }
    if (prepared?.itemId === next.id) return
    dropPrepared()
    const q = quality
    const preset = qualities.find((x) => x.value === q)
    const claim = { itemId: next.id }
    void startPlayback({
      itemId: next.id,
      profile: detectProfile(),
      startPositionSeconds: 0,
      maxHeight: q === 'auto' ? undefined : q,
      maxBitrate: preset?.bitrate,
      forceTranscode: q !== 'auto' || undefined,
      shuffle: shuffle || undefined,
      prepare: true,
    })
      .then((ready) => {
        // Still wanted? (Not if the card went away, or another episode is being got ready, meanwhile.)
        if (prepared === null && claim === preparing.current) prepared = { itemId: next.id, info: ready }
        else void stopPlayback(ready.sessionId).catch(() => undefined)
      })
      .catch(() => undefined) // A busy server: the next episode starts the usual way.
    preparing.current = claim
  }, [showUpNext, info, finished, quality, shuffle])

  // Leaving the player lets an unused next episode go.
  useEffect(() => () => dropPrepared(), [])

  /** Moves this title's subtitles by a quarter of a second, remembered for next time; the subtitles reload shifted. */
  function nudgeSubtitles(by: number) {
    const id = playingItem.current
    if (!id) return
    const next = by === 0 ? 0 : Math.round((subtitleDelay + by) * 100) / 100
    if (Math.abs(next) > 60) return
    void setSubtitleDelay(id, next).then(
      () => setSubtitleDelayState(next),
      () => undefined, // Unchanged on the server, so unchanged here: the next nudge tries again.
    )
  }

  // ---------- Audio sync

  // A file the browser plays itself takes a later sound there and then; anything else is the server's to move.
  const soundHere = info?.method === 'DirectPlay' && !info.audioDelayInStream
  useDelayedSound(video, soundHere ? Math.max(0, audioDelay) : 0)

  /**
   * Moves the file's sound by 50 ms (0 puts it back), for everyone who plays it. Played here, a later sound changes at
   * once; an earlier one, or a stream the server makes, needs a new stream, asked for when the nudging stops.
   */
  function nudgeSound(by: number) {
    const itemId = playingItem.current
    if (!itemId || !info) return
    const next = by === 0 ? 0 : Math.round((audioDelay + by) * 1000) / 1000
    if (Math.abs(next) > MAX_AUDIO_DELAY_SECONDS) return
    setAudioDelayState(next)
    const local = info.method === 'DirectPlay' && next >= 0
    window.clearTimeout(syncTimer.current)
    syncTimer.current = window.setTimeout(
      () =>
        void setAudioDelay(itemId, next).then(
          () => {
            const streamed = info.audioDelayInStream ? (info.audioDelaySeconds ?? 0) : 0
            if (!local && next !== streamed) void restartHere({ audioStreamIndex: info.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' })
          },
          () => {
            setAudioDelayState(info.audioDelaySeconds ?? 0)
            setNotice('The sound couldn’t be moved just now')
          },
        ),
      local ? 400 : 1200,
    )
  }
  useEffect(() => () => window.clearTimeout(syncTimer.current), [])

  // ---------- Speed

  useEffect(() => {
    const el = video.current
    if (!el) return
    // The default too, which a new source (the next episode, another stream) starts at.
    el.defaultPlaybackRate = speed
    el.playbackRate = speed
  }, [speed, info])

  function chooseSpeed(next: number) {
    setMenu(null)
    setSpeed(next)
    // Kept for the show (a film's is for this time only).
    if (info?.item.kind === 'Episode') void setPlaybackSpeed(info.item.id, next).catch(() => undefined)
  }

  // ---------- Sleep timer

  useEffect(() => {
    if (!sleep) return
    const timer = window.setInterval(() => setClock(Date.now()), 500)
    return () => window.clearInterval(timer)
  }, [sleep])

  // Waiting for the end means the credits, or the very end without them, at the speed it's playing.
  const sleepEnd = credits?.start ?? duration
  const sleepLeft = secondsLeft(sleep, clock, duration > 0 ? Math.max(0, (sleepEnd - time) / speed) : Infinity)
  const stillWatching = sleepLeft !== null && sleepLeft <= STILL_WATCHING_SECONDS && playing && !error
  const sleepWhat = info?.item.kind === 'Episode' ? 'episode' : 'film'

  /** The volume back where it was before the timer faded it. */
  const unfade = () => {
    if (fadeFrom.current !== null && video.current) video.current.volume = fadeFrom.current
    fadeFrom.current = null
  }

  function chooseSleep(timer: SleepTimer | null) {
    setMenu(null)
    unfade()
    setClock(Date.now())
    setSleep(timer)
  }

  // The last seconds fade the sound out; at the end, playback pauses (progress is saved as for any pause), nothing plays
  // on after it, and the picture stays dimmed until the viewer comes back.
  useEffect(() => {
    const el = video.current
    if (sleepLeft === null || !el) return
    if (sleepLeft <= 0) {
      el.pause()
      unfade()
      setSleep(null)
      setAsleep(true)
      setUpNextCancelled(true)
      setFinished(false)
      dropPrepared()
      return
    }
    if (sleepLeft <= FADE_SECONDS && playing) {
      fadeFrom.current ??= el.volume
      el.volume = fadeFrom.current * Math.max(0, sleepLeft / FADE_SECONDS)
    } else if (fadeFrom.current !== null) {
      unfade()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- unfade is a plain function of this render
  }, [sleepLeft, playing])

  function chooseSubtitleSize(size: SubtitleSize) {
    const next = { ...preferences, subtitleSize: size }
    setPreferences(next)
    void setPlaybackPreferences(next).catch(() => setPreferences(preferences))
  }

  /** Turns one of the profile's playback preferences on or off, here and for its other devices. */
  function togglePreference(key: 'autoPlayNext' | 'skipIntros' | 'skipRecaps' | 'skipCredits') {
    const next = { ...preferences, [key]: !preferences[key] }
    setPreferences(next)
    void setPlaybackPreferences(next).catch(() => setPreferences(preferences))
  }

  /** Not now: hide the card, and if the credits were skipped, let them play. */
  function cancelUpNext() {
    setUpNextCancelled(true)
    dropPrepared()
    if (finished && video.current && !video.current.ended) void video.current.play()
    setFinished(false)
  }

  // ---------- Keyboard

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (belongsToFocused(e) || e.metaKey || e.ctrlKey) return
      // With an error showing, only its buttons (and Escape, to leave) do anything.
      if (error) {
        if (e.key === 'Escape') exit()
        return
      }
      wake()
      // Still watching? Any key says so (Tab still moves between the prompt's buttons).
      if (stillWatching && e.key !== 'Tab' && !(e.target instanceof HTMLButtonElement && (e.key === 'Enter' || e.key === ' '))) {
        e.preventDefault()
        chooseSleep(extended(sleep, Date.now()))
        return
      }
      switch (e.key) {
        case ' ':
        case 'k':
          e.preventDefault()
          toggle()
          break
        case 'ArrowLeft':
        case 'j':
          e.preventDefault()
          seekBy(-10)
          break
        case 'ArrowRight':
        case 'l':
          e.preventDefault()
          seekBy(10)
          break
        case 'ArrowUp':
          e.preventDefault()
          if (video.current) video.current.volume = Math.min(1, video.current.volume + 0.1)
          break
        case 'ArrowDown':
          e.preventDefault()
          if (video.current) video.current.volume = Math.max(0, video.current.volume - 0.1)
          break
        case 'm':
          if (video.current) video.current.muted = !video.current.muted
          break
        case 'f':
          toggleFullscreen()
          break
        case 'c':
          chooseSubtitle(subtitleKey ? null : (info?.subtitles.find((s) => s.isText && !s.isForced)?.key ?? null))
          break
        case 's':
          if (skippable) seekTo(skippable.end)
          break
        case 'n':
          playNext()
          break
        case 'Escape':
          if (menu) setMenu(null)
          else if (!document.fullscreenElement) exit()
          break
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  // ---------- Render

  const item = info?.item
  const series = info?.ancestors[0]
  const accent = item?.accentColor ?? series?.accentColor
  const selectedText = info?.subtitles.find((s) => s.key === subtitleKey && s.isText && s.url)
  // The server shifts the cues; a new address (with the delay in it) fetches them again.
  const textUrl = selectedText?.url ? `${selectedText.url}${selectedText.url.includes('?') ? '&' : '?'}delay=${subtitleDelay}` : undefined

  return (
    <div
      ref={container}
      className={chromeShown ? 'player' : 'player chrome-hidden'}
      style={{ ...(accent ? { '--ambient': accent } : {}), '--cue-scale': scale } as CSSProperties}
      onMouseMove={wake}
      onClick={(e) => {
        if (closedByPress.current) closedByPress.current = false
        else if (!error && e.target === video.current) toggle()
      }}
    >
      <video
        ref={video}
        playsInline
        poster={imageUrl(item?.artwork.backdrop ?? series?.artwork.backdrop, 1280)}
        crossOrigin="anonymous"
        onDoubleClick={toggleFullscreen}
        x-webkit-airplay="allow"
      >
        {selectedText && (
          <track
            key={textUrl}
            kind="subtitles"
            src={textUrl}
            srcLang={selectedText.language ?? 'und'}
            label={selectedText.key}
            default
            onLoad={() => setSubtitleStatus('idle')}
            onError={() => setSubtitleStatus('failed')}
          />
        )}
      </video>

      {/* The sleep timer's last minute dims the picture little by little; stopped, it stays dim until the viewer's back. */}
      {(stillWatching || asleep) && (
        <div className="player-sleep-dim" aria-hidden="true" style={{ opacity: asleep ? 1 : Math.min(1, 1 - (sleepLeft ?? 0) / STILL_WATCHING_SECONDS) }} />
      )}
      {waiting && !error && <div className="player-spinner" role="status" aria-label="Loading" />}
      {subtitleStatus !== 'idle' && (
        <p className="player-toast" role="status">
          {subtitleStatus === 'loading' ? 'Preparing subtitles. They’ll appear in a moment.' : 'These subtitles couldn’t be loaded. Try another track.'}
        </p>
      )}

      <header className="player-top">
        <button className="icon-button" onClick={exit} aria-label="Back">
          <Back />
        </button>
        {item && (
          <div className="player-title">
            {series ? (
              <>
                <span>{series.title}</span>
                <span className="muted">
                  {episodeCode(item)} {item.title}
                </span>
              </>
            ) : (
              <span>{item.title}</span>
            )}
          </div>
        )}
        {info?.transcodeSummary && (
          <span className="player-badge" title={info.reasons.join('\n')}>
            Converting: {info.transcodeSummary}
          </span>
        )}
      </header>

      {error && (
        // Keyed by the message, so a new one moves focus to its main button again.
        <div key={error.message} className="player-error" role="alertdialog" aria-modal="true" aria-labelledby="player-error-message" aria-describedby={error.kind === 'takenOver' ? 'player-error-note' : undefined}>
          <p id="player-error-message">{error.message}</p>
          {error.kind === 'takenOver' && (
            <p id="player-error-note" className="player-error-note muted">
              The other screen will stop if you watch here.
            </p>
          )}
          <div className="player-error-actions">
            {/* Starts afresh rather than picking up the stream that was ended, so it can take the place back. */}
            <button className="button" autoFocus onClick={() => void retry(error.kind === 'other' ? { forceTranscode: true } : {})}>
              {error.kind === 'takenOver' ? 'Watch here instead' : 'Try again'}
            </button>
            <button className="button quiet" onClick={exit}>
              Go back
            </button>
          </div>
        </div>
      )}

      {notice && !error && (
        <p className="player-notice" role="status">
          {notice}
        </p>
      )}

      {stillWatching && (
        <div className="player-still-watching" role="alertdialog" aria-labelledby="still-watching-title" aria-describedby="still-watching-note">
          <p id="still-watching-title">Still watching?</p>
          <p id="still-watching-note" className="muted">
            Pausing in {Math.max(1, Math.ceil(sleepLeft ?? 0))} s
          </p>
          <div className="player-error-actions">
            <button className="button" autoFocus onClick={() => chooseSleep(extended(sleep, Date.now()))}>
              +15 min
            </button>
            <button className="button quiet" onClick={() => chooseSleep(null)}>
              Turn off timer
            </button>
          </div>
        </div>
      )}

      {skippable && !showUpNext && !error && (
        <button className="skip-button" onClick={() => seekTo(skippable.end)} autoFocus>
          {skippable.type === 'Recap' ? 'Skip recap' : 'Skip intro'}
        </button>
      )}

      {showUpNext && info?.next && !error && (
        <aside className="up-next" aria-live="polite">
          <div className="up-next-still">
            <img src={imageUrl(info.next.artwork.thumb ?? info.next.artwork.backdrop, 500)} alt="" />
            {counting && (
              <svg viewBox="0 0 36 36" className="up-next-ring" aria-hidden="true">
                <circle cx="18" cy="18" r="16" style={{ strokeDashoffset: 100 - (countdown / UP_NEXT_SECONDS) * 100 }} pathLength={100} />
              </svg>
            )}
          </div>
          <div className="up-next-text">
            <span className="muted">
              Next episode
              {counting ? ` in ${Math.max(0, countdown)}` : ''}
            </span>
            <strong>
              {episodeCode(info.next)} {info.next.title}
            </strong>
            <div className="up-next-actions">
              <button className="button small" onClick={playNext}>
                <Play /> Play now
              </button>
              <button className="button quiet small" onClick={cancelUpNext}>
                {finished ? 'Cancel' : 'Keep watching'}
              </button>
            </div>
          </div>
        </aside>
      )}

      {/* Hidden behind an error: nothing there would work, and focus belongs to the error's buttons. */}
      <footer className="player-controls" ref={controls} hidden={!!error} inert={!!error}>
        <Scrubber time={time} duration={duration} bufferedTo={bufferedTo} markers={info?.markers ?? []} previews={previews} onSeek={seekTo} />
        <div className="player-buttons">
          <button className="icon-button" onClick={toggle} aria-label={playing ? 'Pause' : 'Play'}>
            {playing ? <Pause /> : <Play />}
          </button>
          <button className="icon-button" onClick={() => seekBy(-10)} aria-label="Back 10 seconds">
            <Replay10 />
          </button>
          <button className="icon-button" onClick={() => seekBy(10)} aria-label="Forward 10 seconds">
            <Forward10 />
          </button>
          {info?.next && (
            <button className="icon-button" onClick={playNext} aria-label="Next episode">
              <Next />
            </button>
          )}
          <div className="player-volume">
            <button className="icon-button" onClick={() => video.current && (video.current.muted = !video.current.muted)} aria-label={muted ? 'Unmute' : 'Mute'}>
              {muted || volume === 0 ? <Muted /> : <Volume />}
            </button>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              aria-label="Volume"
              onChange={(e) => {
                if (!video.current) return
                video.current.volume = Number(e.target.value)
                video.current.muted = false
              }}
            />
          </div>
          <span className="player-time">
            {formatClock(time)} <span className="muted">/ {formatClock(duration)}</span>
          </span>
          <span className="player-spacer" />

          <div className="menu-anchor">
            <button className="icon-button" aria-label="Subtitles and audio" aria-expanded={menu === 'subtitles'} onClick={() => setMenu(menu === 'subtitles' ? null : 'subtitles')}>
              <Captions />
            </button>
            {menu === 'subtitles' && info && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>Subtitles</h3>
                  <MenuItem selected={!subtitleKey} onClick={() => chooseSubtitle(null)}>
                    Off
                  </MenuItem>
                  {info.subtitles.map((s) => (
                    <MenuItem key={s.key} selected={s.key === subtitleKey} onClick={() => chooseSubtitle(s.key)}>
                      {s.label}
                      {!s.isText && <span className="muted"> (burned in)</span>}
                    </MenuItem>
                  ))}
                  {selectedText && (
                    <>
                      <h3>Timing</h3>
                      <div className="player-menu-stepper">
                        <button aria-label="Show subtitles earlier" onClick={() => nudgeSubtitles(-0.25)}>
                          −
                        </button>
                        <span aria-live="polite">{delayLabel(subtitleDelay)}</span>
                        <button aria-label="Show subtitles later" onClick={() => nudgeSubtitles(0.25)}>
                          +
                        </button>
                      </div>
                      {subtitleDelay !== 0 && (
                        <button className="player-menu-item" onClick={() => nudgeSubtitles(0)}>
                          Reset timing
                        </button>
                      )}
                    </>
                  )}
                </div>
                {info.subtitles.some((s) => s.isText) && (
                  <div className="player-menu-column">
                    <h3>Subtitle size</h3>
                    {subtitleSizes.map((s) => (
                      <MenuItem key={s.value} selected={(preferences.subtitleSize ?? 'Standard') === s.value} onClick={() => chooseSubtitleSize(s.value)}>
                        {s.label}
                      </MenuItem>
                    ))}
                  </div>
                )}
                {info.audioTracks.length > 0 && (
                  <div className="player-menu-column">
                    {info.audioTracks.length > 1 && (
                      <>
                        <h3>Audio</h3>
                        {info.audioTracks.map((a) => (
                          <MenuItem key={a.index} selected={a.index === info.selectedAudioIndex} onClick={() => chooseAudio(a.index)}>
                            {a.label}
                            <span className="muted"> {[a.codec.toUpperCase(), a.channels && a.channels > 2 ? `${a.channels - 1}.1` : null].filter(Boolean).join(' ')}</span>
                          </MenuItem>
                        ))}
                      </>
                    )}
                    {/* Fixes the file for everyone, so it's worth saying so. */}
                    <h3>Audio sync</h3>
                    <div className="player-menu-stepper">
                      <button aria-label="Play the sound earlier" onClick={() => nudgeSound(-0.05)}>
                        −
                      </button>
                      <span aria-live="polite">{syncLabel(audioDelay)}</span>
                      <button aria-label="Play the sound later" onClick={() => nudgeSound(0.05)}>
                        +
                      </button>
                      <button aria-label="Reset sync" title="Reset sync" disabled={audioDelay === 0} onClick={() => nudgeSound(0)}>
                        <Reset />
                      </button>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>

          <div className="menu-anchor">
            <button
              className={speed !== 1 ? 'player-quality active' : 'player-quality'}
              aria-expanded={menu === 'speed'}
              aria-label={`Speed: ${speedLabel(speed)}`}
              onClick={() => setMenu(menu === 'speed' ? null : 'speed')}
            >
              {speedLabel(speed)}
            </button>
            {menu === 'speed' && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>Speed</h3>
                  {speeds.map((s) => (
                    <MenuItem key={s} selected={s === speed} onClick={() => chooseSpeed(s)}>
                      {s === 1 ? 'Normal' : speedLabel(s)}
                    </MenuItem>
                  ))}
                  {info?.item.kind === 'Episode' && <p className="player-menu-note muted">Kept for this show.</p>}
                </div>
              </div>
            )}
          </div>

          <div className="menu-anchor">
            <button
              className="player-quality"
              aria-expanded={menu === 'quality'}
              aria-label={`Quality: ${quality === 'auto' ? (info?.method === 'DirectPlay' ? 'Original' : 'Auto') : `${quality}p`}`}
              onClick={() => setMenu(menu === 'quality' ? null : 'quality')}
            >
              {quality === 'auto' ? (info?.method === 'DirectPlay' ? 'Original' : 'Auto') : `${quality}p`}
            </button>
            {menu === 'quality' && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>Quality</h3>
                  {qualities.map((q) => (
                    <MenuItem key={q.value} selected={q.value === quality} onClick={() => chooseQuality(q.value)}>
                      {q.label}
                    </MenuItem>
                  ))}
                </div>
                <div className="player-menu-column">
                  <h3>While watching</h3>
                  <MenuItem toggle selected={preferences.autoPlayNext} onClick={() => togglePreference('autoPlayNext')}>
                    Auto-play next episode
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipIntros} onClick={() => togglePreference('skipIntros')}>
                    Skip intros
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipRecaps} onClick={() => togglePreference('skipRecaps')}>
                    Skip recaps
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipCredits} onClick={() => togglePreference('skipCredits')}>
                    Skip credits
                  </MenuItem>
                </div>
              </div>
            )}
          </div>

          <div className="menu-anchor">
            <button
              className={sleep ? 'icon-button player-sleep active' : 'icon-button player-sleep'}
              aria-expanded={menu === 'sleep'}
              aria-label={sleepLabel(sleep, clock, sleepWhat)}
              title={sleepLabel(sleep, clock, sleepWhat)}
              onClick={() => setMenu(menu === 'sleep' ? null : 'sleep')}
            >
              <Sleep />
              {minutesLeft(sleep, clock) !== null && (
                <span className="player-sleep-count" aria-hidden="true">
                  {minutesLeft(sleep, clock)}
                </span>
              )}
            </button>
            {menu === 'sleep' && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>Sleep timer</h3>
                  {sleepChoices(sleepWhat).map((choice) => (
                    <MenuItem
                      key={choice.label}
                      selected={choice.label.startsWith('At the end') ? !!sleep && 'endOfEpisode' in sleep : false}
                      onClick={() => chooseSleep(choice.start())}
                    >
                      {choice.label}
                    </MenuItem>
                  ))}
                  {sleep && (
                    <button className="player-menu-item" onClick={() => chooseSleep(null)}>
                      Turn off
                    </button>
                  )}
                </div>
              </div>
            )}
          </div>

          {'pictureInPictureEnabled' in document && document.pictureInPictureEnabled && (
            <button className="icon-button" aria-label="Picture in picture" onClick={() => void video.current?.requestPictureInPicture?.()}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
                <rect x="3" y="5" width="18" height="14" rx="2" />
                <rect x="12" y="11" width="7" height="6" rx="1" fill="currentColor" />
              </svg>
            </button>
          )}
          {'webkitShowPlaybackTargetPicker' in HTMLVideoElement.prototype && (
            <button
              className="icon-button"
              aria-label="AirPlay"
              onClick={() => (video.current as unknown as { webkitShowPlaybackTargetPicker: () => void }).webkitShowPlaybackTargetPicker()}
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
                <path d="M5 17H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-1" />
                <path d="m12 15 5 6H7Z" fill="currentColor" />
              </svg>
            </button>
          )}
          <button className="icon-button" onClick={toggleFullscreen} aria-label={fullscreen ? 'Exit full screen' : 'Full screen'}>
            {fullscreen ? <ExitFullscreen /> : <Fullscreen />}
          </button>
        </div>
      </footer>
    </div>
  )
}

function MenuItem({ selected, onClick, children, toggle = false }: { selected: boolean; onClick: () => void; children: React.ReactNode; toggle?: boolean }) {
  return (
    <button role={toggle ? 'menuitemcheckbox' : 'menuitemradio'} aria-checked={selected} className={selected ? 'player-menu-item selected' : 'player-menu-item'} onClick={onClick}>
      {children}
    </button>
  )
}

interface Previews {
  base: string
  interval: number
  width: number
  height: number
  columns: number
  rows: number
  count: number
}

/** The thumbnail for a moment, cut out of its sprite sheet with background positioning. */
function PreviewFrame({ previews, at }: { previews: Previews; at: number }) {
  const index = Math.min(previews.count - 1, Math.floor(at / previews.interval))
  const perSheet = previews.columns * previews.rows
  const sheet = Math.floor(index / perSheet)
  const cell = index % perSheet
  return (
    <span
      className="scrubber-preview"
      style={{
        width: previews.width,
        height: previews.height,
        backgroundImage: `url(${previews.base}/trickplay/${sheet}.jpg)`,
        backgroundPosition: `-${(cell % previews.columns) * previews.width}px -${Math.floor(cell / previews.columns) * previews.height}px`,
        backgroundSize: `${previews.columns * previews.width}px ${previews.rows * previews.height}px`,
      }}
    />
  )
}

/** Seek bar with buffered range, intro/credits marks, and a preview frame and time while hovering or dragging. */
function Scrubber({
  time,
  duration,
  bufferedTo,
  markers,
  previews,
  onSeek,
}: {
  time: number
  duration: number
  bufferedTo: number
  markers: PlaybackInfo['markers']
  previews: Previews | null
  onSeek: (seconds: number) => void
}) {
  const bar = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<number | null>(null)
  const [dragging, setDragging] = useState(false)
  const pct = (s: number) => (duration > 0 ? `${(Math.min(s, duration) / duration) * 100}%` : '0%')

  const at = (e: ReactPointerEvent) => {
    const rect = bar.current!.getBoundingClientRect()
    return Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width)) * duration
  }

  const shown = dragging && hover !== null ? hover : time
  return (
    <div
      ref={bar}
      className="scrubber"
      role="slider"
      tabIndex={0}
      aria-label="Seek"
      aria-valuemin={0}
      aria-valuemax={Math.round(duration)}
      aria-valuenow={Math.round(time)}
      aria-valuetext={`${formatClock(time)} of ${formatClock(duration)}`}
      onPointerMove={(e) => setHover(at(e))}
      onPointerLeave={() => !dragging && setHover(null)}
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture(e.pointerId)
        setDragging(true)
        setHover(at(e))
      }}
      onPointerUp={(e) => {
        setDragging(false)
        onSeek(at(e))
        setHover(null)
      }}
    >
      <div className="scrubber-track">
        <span className="scrubber-buffered" style={{ width: pct(bufferedTo) }} />
        {markers.map((m) => (
          <span key={`${m.type}-${m.start}`} className={`scrubber-marker marker-${m.type.toLowerCase()}`} style={{ left: pct(m.start), width: `calc(${pct(m.end)} - ${pct(m.start)})` }} title={m.type} />
        ))}
        <span className="scrubber-played" style={{ width: pct(shown) }} />
        <span className="scrubber-thumb" style={{ left: pct(shown) }} />
      </div>
      {hover !== null && (
        <span className="scrubber-tooltip" style={{ left: pct(hover) }}>
          {previews && <PreviewFrame previews={previews} at={hover} />}
          {formatClock(hover)}
        </span>
      )}
    </div>
  )
}
