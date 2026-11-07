import Hls from 'hls.js/light'
import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  ApiError,
  beaconProgress,
  getSubtitleSync,
  imageUrl,
  reportProgress,
  savePlayerPreferences,
  setAudioDelay,
  setPlaybackSpeed,
  setSubtitleChoice,
  setSubtitleDelay,
  startPlayback,
  startSubtitleSync,
  stopPlayback,
  type PlaybackInfo,
  type PlaybackPreferences,
} from '../api/client'
import type { SubtitlePosition, SubtitleSize } from '../api/client'
import {
  Back,
  Captions,
  ExitFullscreen,
  Forward10,
  Fullscreen,
  Muted,
  Next,
  Previous,
  Replay10,
  Reset,
  Sleep,
  Waveform,
  Volume,
} from '../components/icons'
import { icon } from '../components/icons.generated'
import { SubtitleSearchDialog } from '../components/SubtitleSearchDialog'
import { usePolling } from '../hooks/usePolling'
import { hasSoundGraph, MAX_AUDIO_DELAY_SECONDS, useSoundChain } from '../hooks/soundChain'
import { useAgeLimits } from '../hooks/sessionContext'
import { announceWatched } from '../hooks/watched'
import { clearEnding, isWatched, recordEnding, sendingFinalReport, whenBrowserBack, type PlaybackEnding, type PlayState } from '../hooks/playbackEnding'
import { episodeCode, formatClock } from '../lib/format'
import { belongsToFocused } from '../lib/focusedKeys'
import { mediaErrorRecovery } from '../lib/hlsRecovery'
import { speedLabel } from '../lib/podcasts'
import { drawStyled, fontsFor, type StyledSubtitles } from '../lib/styledSubtitles'
import { cueLine, pictureIn } from '../lib/subtitlePlacement'
import { skipAt, skipCancelled, skipFresh, skipPressed, skipSeeked, type SkipNow, type SkipState } from '../lib/skipTiming'
import { NEXT_EPISODE_COUNTDOWN_SECONDS, nextEpisodeTiming, upNextAt, upNextAtEnd, upNextFresh, upNextMoved, type UpNextState } from '../lib/upNext'
import { dialogueBoostChoices, isShaping, nightModeChoices, readSoundSettings, shapingFor, speakerChoices, timeOfDay, unshaped, useSoundSettings, useSoundShaping, writeSoundSettings } from '../lib/soundSettings'
import { detectProfile, prefersNativeHls } from '../lib/profile'
import { prepareVideo, rememberCopyingFailed, startVideo, wasCopyAsked } from '../lib/copiedVideo'
import { EARLY_STALL_MS, reportEarlyStall, reportStart, type StartTiming } from '../lib/startTiming'
import { drop as dropPrepared, getReady, holds as holdsPrepared, take as takePrepared } from '../lib/preparedPlayback'
import { extended, FADE_SECONDS, minutesLeft, secondsLeft, sleepChoices, sleepLabel, STILL_WATCHING_SECONDS, type SleepTimer } from '../lib/sleepTimer'
import { t, tk, Words } from '../i18n'
import '../styles/player.lazy.css'

type Quality = 'auto' | 1080 | 720 | 480
const qualities: { value: Quality; label: string; bitrate?: number }[] = [
  // Said through t(label) where shown; the rest are numbers.
  { value: 'auto', label: tk('Best for this device') },
  { value: 1080, label: '1080p', bitrate: 10_000_000 },
  { value: 720, label: '720p', bitrate: 4_000_000 },
  { value: 480, label: '480p', bitrate: 1_500_000 },
]

/** The speeds the player offers; an episode's is kept for its show. */
const speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2] as const



const defaultPreferences: PlaybackPreferences = { autoPlayNext: true, skipIntros: false, skipRecaps: false, skipCredits: false, subtitleSize: 'Standard' }

/** How much bigger than standard each subtitle size draws (player.css scales ::cue by --cue-scale). */
export const subtitleScale: Record<SubtitleSize, number> = { Small: 0.8, Standard: 1, Large: 1.25, ExtraLarge: 1.5 }
/** Said through t(position) where shown. */
const subtitlePositions: SubtitlePosition[] = [tk('Bottom'), tk('Raised'), tk('Higher'), tk('Top')]
/** Said through t(label) where shown. */
const subtitleSizes: { value: SubtitleSize; label: string }[] = [
  { value: 'Small', label: tk('Small') },
  { value: 'Standard', label: tk('Standard') },
  { value: 'Large', label: tk('Large') },
  { value: 'ExtraLarge', label: tk('Extra large') },
]

/** "In time", "0.5s later", "1.25s earlier". */
function delayLabel(seconds: number) {
  if (seconds === 0) return t('In time')
  return seconds > 0 ? t('{seconds}s later', { seconds: Math.abs(seconds) }) : t('{seconds}s earlier', { seconds: Math.abs(seconds) })
}

/** "In sync", "+0.15 s" (the sound later), "−0.2 s" (earlier). */
function syncLabel(seconds: number) {
  if (seconds === 0) return t('In sync')
  return `${seconds > 0 ? '+' : '−'}${Math.abs(seconds)} s`
}

/**
 * A stepper button that calls `onStep(false)` when clicked and, while held down, `onStep(true)` over and over (the
 * bigger step) until let go.
 */
function HoldButton({ onStep, ...props }: { onStep: (held: boolean) => void } & Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, 'onClick'>) {
  const step = useRef(onStep)
  useEffect(() => {
    step.current = onStep
  })
  const timer = useRef<number | undefined>(undefined)
  const jumped = useRef(false)
  const stop = () => {
    window.clearTimeout(timer.current)
    window.clearInterval(timer.current)
    timer.current = undefined
  }
  useEffect(() => stop, [])
  return (
    <button
      {...props}
      onPointerDown={(e) => {
        if (e.button !== 0) return
        stop()
        jumped.current = false
        timer.current = window.setTimeout(() => {
          jumped.current = true
          step.current(true)
          timer.current = window.setInterval(() => step.current(true), 250)
        }, 400)
      }}
      onPointerUp={stop}
      onPointerLeave={stop}
      onPointerCancel={stop}
      onContextMenu={(e) => e.preventDefault()}
      // A click that ends a hold isn't a press; Enter and Space still are.
      onClick={() => {
        if (jumped.current) jumped.current = false
        else step.current(false)
      }}
    />
  )
}
const HIDE_CHROME_MS = 3000
// Said through t() where shown.
const STREAM_STOPPED = tk('The stream stopped. The server may have had trouble converting this file.')
const TAKEN_OVER = tk('Someone started watching on another screen, so this one had to stop.')
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
  /** Asked the old way, with the picture converted: a copied one just failed to play (lib/copiedVideo). */
  noCopy?: boolean
}

/** Why the server ended this transcode for another viewer, if it did (its playlist answers 410 with the reason). */
async function takenOverReason(sessionId: string): Promise<string | undefined> {
  const response = await fetch(`/api/v1/playback/${sessionId}/main.m3u8`, { signal: AbortSignal.timeout(REASON_TIMEOUT_MS) }).catch(() => null)
  if (response?.status !== 410) return undefined
  const problem = (await response.json().catch(() => null)) as { detail?: string } | null
  return problem?.detail ?? t(TAKEN_OVER)
}

/** Whether the server still serves this stream: one that stopped (a server restart, another screen) isn't the browser's fault. */
async function streamAnswers(sessionId: string): Promise<boolean> {
  const response = await fetch(`/api/v1/playback/${sessionId}/main.m3u8`, { signal: AbortSignal.timeout(REASON_TIMEOUT_MS) }).catch(() => null)
  return response?.ok === true
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
  // What Play was pressed on and where, whatever this sitting has moved on to since (Next replaces the page, keeping
  // these), so that page can move on once the player closes.
  const location = useLocation()
  const playState = location.state as PlayState | null
  const startedWith = useRef(id)
  const origin = useRef(playState?.from ?? null)
  const leaving = useRef(false)

  const container = useRef<HTMLDivElement>(null)
  const video = useRef<HTMLVideoElement>(null)
  const hls = useRef<Hls | null>(null)
  const session = useRef<string | undefined>(undefined)
  const hideTimer = useRef<number | undefined>(undefined)
  const recoverRef = useRef<() => Promise<void>>(async () => undefined)
  const convertInsteadRef = useRef<() => void>(() => undefined)
  /** Whether the stream playing was asked for with its picture copied (lib/copiedVideo). */
  const copyAsked = useRef(false)
  /** A copied stream failed to play: true when it's asked for again the old way (once), false when it wasn't one. */
  const copyFailedRef = useRef<() => Promise<boolean>>(async () => false)
  const recovering = useRef(false)
  const lastRecovery = useRef(0)
  // Ends when the item changes or the page closes, so a recovery in progress gives up.
  const lifetime = useRef({ alive: true })
  // Counts playback requests, so only the latest one's answer is used.
  const requests = useRef(0)
  /** This title's start, timed until its first picture (see lib/startTiming). */
  const timing = useRef<StartTiming | null>(null)
  /** When this title's first picture came, and its playback id, to note a stall soon after (see lib/startTiming). */
  const started = useRef<{ at: number; id?: string } | null>(null)

  const [info, setInfo] = useState<PlaybackInfo>()
  // How the attached stream is played, set as it's attached: an error can come before the render that brings `info`.
  const method = useRef<PlaybackInfo['method']>(undefined)
  const [error, setError] = useState<PlayerError>()
  const [playing, setPlaying] = useState(false)
  const [waiting, setWaiting] = useState(true)
  /** The converted stream is Safari's own HLS player, whose sound mustn't go through Web Audio. */
  const [nativeHls, setNativeHls] = useState(false)
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
  // The sleep timer stopped this episode: nothing moves on from it.
  const [sleepStopped, setSleepStopped] = useState(false)
  // Next Episode's Cancel, and a seek or start past where it would have moved on (lib/upNext). The ref is the same,
  // for the timeupdate handler, which runs between renders.
  const [upNextState, setUpNextStateValue] = useState<UpNextState>(upNextFresh)
  const upNextStateRef = useRef<UpNextState>(upNextFresh)
  const setUpNextState = (next: UpNextState) => {
    upNextStateRef.current = next
    setUpNextStateValue(next)
  }
  // The position at the last timeupdate (null before the first, so a start counts as a jump), whether a seek came
  // since, and whether this episode has already moved on or closed.
  const lastPosition = useRef<number | null>(null)
  const seeked = useRef(false)
  const movedOn = useRef(false)
  const upNextTick = useRef<(position: number) => void>(() => undefined)
  // The video ended (with a next episode to go to).
  const [ended, setEnded] = useState(false)
  // The countdown over the last frame when the file ended before its own had started; null when not running.
  const [endCountdown, setEndCountdown] = useState<number | null>(null)
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
  // Searching Bazarr for subtitles (a dialog over the player), and re-syncing a subtitle file to the audio.
  const [findingSubtitles, setFindingSubtitles] = useState(false)
  const [resync, setResync] = useState<{ state: 'running' | 'failed'; error?: string }>()
  // A kids profile doesn't find or re-sync subtitles.
  const { kids } = useAgeLimits()
  // Bumped when a subtitle file changes on the server (a re-sync), so the track is fetched again.
  const [subtitleVersion, setSubtitleVersion] = useState(0)
  const scale = subtitleScale[preferences.subtitleSize ?? 'Standard']
  const position = preferences.subtitlePosition ?? 'Bottom'
  const blackBars = preferences.subtitlesInBlackBars ?? false
  const [notice, setNotice] = useState<string>()
  // The latest request to get the next episode ready, so an older answer arriving late is let go.
  // Skip intro / recap (lib/skipTiming, docs/skip-timing.md): what's been skipped or cancelled this episode, kept
  // between timeupdates, and what the pill shows now.
  const skipState = useRef<SkipState>(skipFresh)
  const [skipShown, setSkipShown] = useState<Omit<SkipNow, 'state' | 'skipTo'>>({})
  const skipTick = useRef<(position: number) => void>(() => undefined)

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

    // Safari can play a converted stream itself, but its sound can go silent through Web Audio: when the sound is shaped
    // here (or the element already goes through Web Audio), hls.js plays it instead, as in other browsers.
    const shapedHere = hasSoundGraph(el) || isShaping(shapingFor(readSoundSettings(), new Date()))
    const viaHlsJs = playback.method === 'Transcode' && Hls.isSupported() && (!prefersNativeHls() || shapedHere)
    setNativeHls(playback.method === 'Transcode' && !viaHlsJs)
    if (viaHlsJs) {
      // Subtitles are rendered as <track>s, so leave them out of the HLS manifest.
      // The first segment is fetched while the playlists load (not once the video element is ready), and there's only
      // ever one quality, so no bandwidth test first.
      const player = new Hls({ startPosition: position, maxBufferLength: 60, backBufferLength: 90, startFragPrefetch: true, testBandwidth: false })
      // Media errors get hls.js's own recovery first; one that keeps coming back asks the server for a fresh stream.
      // A stream with its picture copied that this browser can't play after all is asked for again the old way.
      const onMediaError = mediaErrorRecovery(player, () => void copyFailedRef.current().then((done) => (done ? undefined : recoverRef.current())))
      player.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) onMediaError()
        else if (data.type === Hls.ErrorTypes.NETWORK_ERROR) void recoverRef.current()
        else void copyFailedRef.current().then((done) => (done ? undefined : recoverRef.current()))
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
        const wanted = {
          itemId: playingItem.current ?? id,
          shuffle: shuffleRef.current || undefined,
          audioStreamIndex: options.audioStreamIndex,
          subtitleKey: options.subtitleKey,
          startPositionSeconds: options.startPositionSeconds,
          maxHeight: q === 'auto' ? undefined : q,
          maxBitrate: preset?.bitrate,
          forceTranscode: options.forceTranscode || q !== 'auto' || undefined,
        }
        // Got ready ahead of time (the next episode during the credits, or on the title's page) for exactly this
        // stream: it opens straight away. Anything else got ready is let go.
        const ready = replaces ? null : await takePrepared(wanted)
        if (replaces) dropPrepared()
        const playback = ready ?? await startVideo({ ...wanted, replacesSessionId: replaces }, startPlayback, !options.noCopy)
        if (!life.alive || life !== lifetime.current || request !== requests.current) {
          void stopPlayback(playback.sessionId)
          // Nothing left for this request to do: a newer one (or none, the page has moved on) is in charge.
          return true
        }
        if (session.current && session.current !== playback.sessionId) void stopPlayback(session.current)
        if (timing.current && timing.current.answeredAt === undefined) {
          Object.assign(timing.current, { answeredAt: performance.now(), warmed: ready !== null, method: playback.method, playbackId: playback.playbackId })
        }
        session.current = playback.sessionId
        copyAsked.current = wasCopyAsked(playback)
        // The same title again (another audio track, a recovery) keeps its speed; another starts at its show's.
        const sameTitle = playingItem.current === playback.item.id
        playingItem.current = playback.item.id
        setInfo(playback)
        method.current = playback.method
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
      if (!el || Date.now() - lastRecovery.current < 60_000) return setError({ message: t(STREAM_STOPPED), kind: 'other' })
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
      if (life.alive) setError({ message: t(STREAM_STOPPED), kind: 'other' })
    } finally {
      recovering.current = false
    }
  }
  // A browser that claimed it could play the file but can't: converted instead, on the audio and subtitles chosen.
  const convertInstead = () =>
    void restartHere({ forceTranscode: true, audioStreamIndex: info?.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' })
  // A copied stream this browser can't play: asked for again the old way from where it got to, and from now on. Only
  // while the server still serves it: a stream lost to a server restart or another screen (which Safari can report as
  // unplayable too) is a recovery's, and isn't remembered against copying.
  const copyFailed = async () => {
    const failed = session.current
    if (!copyAsked.current || failed === undefined) return false
    copyAsked.current = false
    const answers = await streamAnswers(failed)
    // Another stream started meanwhile (a recovery, another audio track): it's in charge.
    if (session.current !== failed) return true
    if (!answers) {
      copyAsked.current = true
      return false
    }
    rememberCopyingFailed()
    void restartHere({ noCopy: true, audioStreamIndex: info?.selectedAudioIndex ?? undefined, subtitleKey: subtitleKey ?? 'off' })
    return true
  }
  useEffect(() => {
    recoverRef.current = recover
    convertInsteadRef.current = convertInstead
    copyFailedRef.current = copyFailed
  })

  useEffect(() => {
    setSleepStopped(false)
    setUpNextState(upNextFresh)
    lastPosition.current = null
    seeked.current = false
    movedOn.current = false
    setEnded(false)
    setEndCountdown(null)
    // The last title's position goes too: left over, it would put the next episode in its own credits at once (Skip
    // credits would mark it watched and count down to the one after).
    setTime(0)
    setBufferedTo(0)
    skipState.current = skipFresh
    setSkipShown({})
    playingItem.current = undefined
    // Come back to with Forward after closing: the title's page, not the episode again from the top.
    if (playState?.closed) {
      navigate(`/item/${id}`, { replace: true })
      return
    }
    clearEnding()
    const t = params.get('t')
    const el = video.current
    const life = { alive: true }
    lifetime.current = life
    timing.current = { kind: 'video', pressedAt: performance.now() }
    void start({ startPositionSeconds: t !== null ? Number(t) : undefined })
    return () => {
      life.alive = false
      const at = el?.currentTime ?? 0
      hls.current?.destroy()
      // A direct or native stream is let go too, so its late errors can't start a recovery for the next title.
      if (el) {
        el.removeAttribute('src')
        el.load()
      }
      if (session.current) {
        // Where it stopped, then the end of the session; the page that loads next waits a moment for them.
        const ending = session.current
        sendingFinalReport(
          (at > 0 ? reportProgress(ending, at, true).catch(() => undefined) : Promise.resolve()).then(() => stopPlayback(ending)),
        )
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
    // A closed tab or window never unmounts the page, so its stream would run on for nobody until the server's idle
    // check. Hiding sent the place already; this ends the session. Not for a page kept to come back to (`persisted`).
    const onPageHide = (event: PageTransitionEvent) => {
      if (event.persisted || !session.current) return
      const ending = session.current
      session.current = undefined
      void stopPlayback(ending)
    }
    window.addEventListener('pagehide', onPageHide)
    const el = video.current
    el?.addEventListener('pause', report)
    return () => {
      clearInterval(timer)
      document.removeEventListener('visibilitychange', onHide)
      window.removeEventListener('pagehide', onPageHide)
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
      // Next Episode follows the real position, so it moves on on time rather than up to a second late.
      upNextTick.current(el.currentTime)
      skipTick.current(el.currentTime)
      for (let i = 0; i < el.buffered.length; i++) {
        if (el.buffered.start(i) <= el.currentTime + 0.5 && el.buffered.end(i) >= el.currentTime) setBufferedTo(Math.floor(el.buffered.end(i)))
      }
    }
    const onPlay = () => setPlaying(true)
    const onPause = () => setPlaying(false)
    const onWaiting = () => {
      setWaiting(true)
      const first = started.current
      // A seek (Skip intro, say) waits too: only a stall counts, and only the first.
      if (el.seeking) return
      if (first && performance.now() - first.at < EARLY_STALL_MS) reportEarlyStall(first.id, performance.now() - first.at)
      started.current = null
    }
    const onPlaying = () => setWaiting(false)
    const onFirstPicture = () => {
      if (timing.current?.answeredAt === undefined) return
      reportStart(timing.current)
      started.current = { at: performance.now(), id: timing.current.playbackId }
      timing.current = null
    }
    const onVolume = () => {
      setVolume(el.volume)
      setMuted(el.muted)
    }
    const onError = () => {
      // Safari's own player can't decode a copied stream (rather than losing the connection): asked the old way.
      // MediaError's MEDIA_ERR_DECODE (3) and MEDIA_ERR_SRC_NOT_SUPPORTED (4).
      const undecodable = el.error?.code === 3 || el.error?.code === 4
      if (method.current === 'DirectPlay') convertInsteadRef.current()
      else if (method.current === 'Transcode') {
        if (undecodable) void copyFailedRef.current().then((done) => (done ? undefined : recoverRef.current()))
        else void recoverRef.current()
      }
      else setError({ message: t('This file couldn’t be played.'), kind: 'other' })
    }
    const onEnded = () => {
      if (session.current) void reportProgress(session.current, el.currentTime, true, true).catch(() => undefined)
      if (info?.next) setEnded(true)
    }
    // Scrubbing back from the end plays on.
    const onSeeking = () => {
      seeked.current = true
      skipState.current = skipSeeked(skipState.current)
      if (!el.ended) {
        setEnded(false)
        setEndCountdown(null)
      }
    }
    el.addEventListener('timeupdate', onTime)
    el.addEventListener('play', onPlay)
    el.addEventListener('pause', onPause)
    el.addEventListener('waiting', onWaiting)
    el.addEventListener('playing', onPlaying)
    el.addEventListener('playing', onFirstPicture)
    el.addEventListener('canplay', onPlaying)
    el.addEventListener('volumechange', onVolume)
    el.addEventListener('error', onError)
    el.addEventListener('ended', onEnded)
    el.addEventListener('seeking', onSeeking)
    return () => {
      el.removeEventListener('timeupdate', onTime)
      el.removeEventListener('play', onPlay)
      el.removeEventListener('pause', onPause)
      el.removeEventListener('waiting', onWaiting)
      el.removeEventListener('playing', onFirstPicture)
      el.removeEventListener('playing', onPlaying)
      el.removeEventListener('canplay', onPlaying)
      el.removeEventListener('volumechange', onVolume)
      el.removeEventListener('error', onError)
      el.removeEventListener('ended', onEnded)
      el.removeEventListener('seeking', onSeeking)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [info])

  // Subtitles sit where the profile put them over the picture (or in the black bar below it), and rise above the
  // controls (and the Next Episode card) while they're showing. Cues are placed by their top edge as a percentage of
  // the video's height (Chrome ignores lineAlign), so each cue's own height (its lines at the ::cue font size in
  // player.css) is allowed for and multi-line cues grow upwards. A track's cues (a
  // film has a thousand or two) are all placed once, when they've loaded or the space changes; after that each
  // cuechange touches only the cues on screen.
  const controls = useRef<HTMLElement>(null)
  // The Next Episode card, while it shows: subtitles rise above it too.
  const [upNextCard, setUpNextCard] = useState<HTMLElement | null>(null)
  useEffect(() => {
    const el = video.current
    const tracks = el?.textTracks
    if (!el || !tracks || typeof VTTCue === 'undefined') return
    let placed = new WeakSet<TextTrack>()
    const place = () => {
      const box = el.getBoundingClientRect()
      const height = box.height || innerHeight
      const linePx = Math.min(32, Math.max(17.6, innerWidth * 0.024)) * scale * 1.3 + 2
      const card = upNextCard ? box.bottom - upNextCard.getBoundingClientRect().top + 12 : 0
      const controlsLift = Math.max(chrome && controls.current ? controls.current.getBoundingClientRect().height + 20 : 0, card)
      const { top, picture, bar } = pictureIn(box.width || innerWidth, height, el.videoWidth, el.videoHeight)
      for (const track of Array.from(el.textTracks)) {
        if (track.mode !== 'showing' || !track.cues) continue
        const cues = placed.has(track) ? track.activeCues : track.cues
        if (track.cues.length) placed.add(track)
        for (const cue of Array.from(cues ?? [])) {
          if (!(cue instanceof VTTCue)) continue
          const lines = cue.text.split('\n').length
          cue.snapToLines = false
          cue.lineAlign = 'start'
          cue.line = cueLine({ height, top, picture, bar, cueHeight: lines * linePx, controlsLift, position, blackBars })
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
    // The picture's shape is known once the video has loaded, and can change with the stream (a new quality).
    el.addEventListener('resize', onResize)
    return () => {
      for (const track of Array.from(tracks)) track.removeEventListener('cuechange', place)
      tracks.removeEventListener('addtrack', onAdd)
      tracks.removeEventListener('change', place)
      window.removeEventListener('resize', onResize)
      el.removeEventListener('resize', onResize)
    }
  }, [chrome, subtitleKey, scale, position, blackBars, upNextCard])

  // Styled subtitles (ASS/SSA) are drawn as authored, with their fonts, over the video, rather than as plain cues. If
  // that can't start (a browser without WebGL or WebAssembly), the plain version shows instead.
  const [styledFailed, setStyledFailed] = useState<string>()
  const styled = useRef<StyledSubtitles | null>(null)
  const latestDelay = useRef(subtitleDelay)
  const styledTrack = info?.subtitles.find((s) => s.key === subtitleKey && s.styledUrl && `${info.sessionId}:${s.key}` !== styledFailed)
  const styledSource = styledTrack?.styledUrl ? `${styledTrack.styledUrl}${subtitleVersion ? `?v=${subtitleVersion}` : ''}` : undefined
  const sessionId = info?.sessionId
  // Only the fonts this track's script names (the key is part of `styledSource`, so a new track draws afresh).
  const fontsUrl = styledTrack ? fontsFor(info?.fontsUrl, styledTrack.key) : null
  useEffect(() => {
    const el = video.current
    if (!el || !styledSource || !sessionId) return
    const failed = `${sessionId}:${subtitleKey}`
    const abort = new AbortController()
    drawStyled(el, styledSource, fontsUrl, latestDelay.current, abort.signal).then(
      (drawn) => {
        styled.current = drawn
        drawn.delay = latestDelay.current
        setSubtitleStatus('idle')
      },
      () => {
        if (!abort.signal.aborted) setStyledFailed(failed)
      },
    )
    return () => {
      abort.abort()
      styled.current?.destroy()
      styled.current = null
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [styledSource, sessionId, fontsUrl])
  useEffect(() => {
    latestDelay.current = subtitleDelay
    if (styled.current) styled.current.delay = subtitleDelay
  }, [subtitleDelay])

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
  // How this sitting ends, from what was playing when the player closes (by its own Back or the browser's).
  const now = useRef({ info, time, duration, finished: ended, credits: undefined as number | undefined })
  now.current = { info, time, duration, finished: ended, credits: info?.markers.find((m) => m.type === 'Credits')?.start }
  const endingNow = (): PlaybackEnding | undefined => {
    const { info: playing, time: at, duration: length, finished: over, credits: creditsStart } = now.current
    // A shuffle belongs to its show, whose page has nothing to move on to.
    if (!playing || shuffleRef.current) return undefined
    return { from: origin.current, startedWith: startedWith.current, stoppedOn: playing.item.id, watched: over || isWatched(at, length, creditsStart), next: playing.next?.id ?? null }
  }
  // Leaving by the browser's Back (or a swipe) counts as closing too. It's recorded as the history moves, before the
  // router renders the page Play was pressed on (see browserMoved), so that page moves on at once; unmounting catches any other way out.
  useEffect(
    () => {
      const record = () => {
        if (leaving.current) return
        const ending = endingNow()
        if (ending) recordEnding(ending)
      }
      const onBack = () => {
        record()
        leaving.current = true
      }
      whenBrowserBack(onBack)
      return () => {
        whenBrowserBack(undefined, onBack)
        record()
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- on leaving only; it reads the latest through refs
    [],
  )

  // Back to where Play was pressed, as the apps' players close: at once, while that page waits a moment for the last
  // report (see finalReportSent). It moves on if it showed the episode just watched (see endingFrom).
  const exit = () => {
    if (leaving.current) return
    leaving.current = true
    const ending = endingNow()
    if (ending) recordEnding(ending)
    if ((window.history.state as { idx?: number } | null)?.idx) {
      // Forward from that page comes back here: closed, so it opens the page rather than playing again.
      navigate(`${location.pathname}${location.search}`, { replace: true, state: { closed: true } satisfies PlayState })
      navigate(-1)
    } else {
      // Opened straight from a link: the episode's page (moved on, as above), else its show's or the film's.
      const back = info?.ancestors[0]?.id ?? info?.item.id
      const episode = ending && info?.item.kind === 'Episode' && !shuffle ? (ending.watched && ending.next) || ending.stoppedOn : undefined
      navigate(episode ? `/item/${episode}` : back ? `/item/${back}` : '/')
    }
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

  function searchForSubtitles() {
    setMenu(null)
    video.current?.pause()
    setFindingSubtitles(true)
  }

  /** Re-times the chosen subtitle file to the audio, in the background; it reloads when done. */
  async function resyncSubtitles() {
    setMenu(null)
    if (!info || !subtitleKey) return
    setResync({ state: 'running' })
    try {
      await startSubtitleSync(info.item.id, subtitleKey, info.fileId)
    } catch (e) {
      setResync({ state: 'failed', error: e instanceof ApiError && e.messages[0] ? e.messages[0] : t('The subtitles couldn’t be re-synced.') })
    }
  }

  usePolling(
    () => {
      if (!info) return
      void getSubtitleSync(info.item.id, info.fileId).then((sync) => {
        if (sync.state === 'running') return
        if (sync.state === 'failed') {
          setResync({ state: 'failed', error: sync.error ?? t('The subtitles couldn’t be re-synced.') })
          return
        }
        setResync(undefined)
        // Burned-in subtitles need a new stream; text ones are fetched again.
        if (info.subtitleBurnedIn) void restartHere({ subtitleKey: subtitleKey ?? 'off', audioStreamIndex: info.selectedAudioIndex ?? undefined })
        else setSubtitleVersion((v) => v + 1)
      })
    },
    resync?.state === 'running' ? 3000 : null,
  )

  // A failed re-sync says why for a few seconds.
  useEffect(() => {
    if (resync?.state !== 'failed') return
    const timer = setTimeout(() => setResync(undefined), 8000)
    return () => clearTimeout(timer)
  }, [resync])

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

  const credits = info?.markers.find((m) => m.type === 'Credits')
  const skipCounting = skipShown.counting
  const skippable = skipCounting?.marker ?? skipShown.button
  const skipLabel = skipCounting
    ? skippable?.type === 'Recap'
      ? t(Words.skipRecapIn, { seconds: skipCounting.left })
      : t(Words.skipIntroIn, { seconds: skipCounting.left })
    : skippable?.type === 'Recap'
      ? t(Words.skipRecap)
      : t(Words.skipIntro)
  // Waiting for the end means the credits, or the very end without them, at the speed it's playing.
  const sleepEnd = credits?.start ?? duration
  const sleepLeft = secondsLeft(sleep, clock, duration > 0 ? Math.max(0, (sleepEnd - time) / speed) : Infinity)

  // When Next Episode shows and what follows (docs/next-episode-timing.md). The countdown follows the position, so the
  // picture plays on under it, pausing holds it and scrubbing back before it puts it away.
  const upNext = info?.next
    ? nextEpisodeTiming({ credits: credits?.start, duration, skipCredits: preferences.skipCredits, autoPlayNext: preferences.autoPlayNext })
    : null
  // A sleep timer that stops playback before then holds the episode there: no countdown, and no closing.
  const sleepsBefore = (at: number) => sleepLeft !== null && sleepLeft <= (at - time) / speed
  const holds = (at: number) => sleepStopped || sleepsBefore(at)
  const upNow = upNextAt({ timing: upNext, duration, state: upNextState, time, holds })
  const countingLive = upNow.counting
  const counting = countingLive || endCountdown !== null
  const countdown = countingLive ? upNow.countdown : (endCountdown ?? 0)
  // After Cancel the card stays, with only Play now.
  const showUpNext = !!info?.next && !sleepStopped && (upNow.show || counting)

  const playNext = useCallback(() => {
    if (info?.next) navigate(`/play/${info.next.id}?t=0${shuffle ? '&shuffle=1' : ''}`, { replace: true })
  }, [info, navigate, shuffle])

  // Moving on from Next Episode (Play now, or the countdown) counts this one as watched, whatever its length. The
  // control bar's next button doesn't: that's skipping ahead mid-episode.
  const moveOn = () => {
    if (!info?.next || movedOn.current) return
    movedOn.current = true
    if (session.current && !ended) void reportProgress(session.current, video.current?.currentTime ?? time, true, true).catch(() => undefined)
    playNext()
  }

  // Each timeupdate, on the real position: a seek or start past where it would move on plays on with the card up, and
  // playing into it while counting down moves on (lib/upNext).
  upNextTick.current = (position: number) => {
    if (!info?.next || playingItem.current !== info.item.id || movedOn.current) return
    const jumped = seeked.current || lastPosition.current === null
    seeked.current = false
    lastPosition.current = position
    const state = upNextMoved(upNext, upNextStateRef.current, position, jumped)
    if (state !== upNextStateRef.current) setUpNextState(state)
    if (ended) return
    const now = upNextAt({ timing: upNext, duration, state, time: position, holds })
    if (now.action === 'next') moveOn()
    else if (now.action === 'close') {
      // Skip credits with auto-play off: the episode is over as the credits start, which closes the player.
      movedOn.current = true
      video.current?.pause()
      if (session.current) void reportProgress(session.current, position, true, true).catch(() => undefined)
      exit()
    }
  }

  // Like a music player's back: a few seconds in, or with nothing before (a film, a first episode), it starts this
  // title again; otherwise it goes to the episode before.
  const playPrevious = () => {
    if (info?.previous && (video.current?.currentTime ?? 0) <= 3) navigate(`/play/${info.previous.id}?t=0`, { replace: true })
    else seekTo(0)
  }

  // Skip intros and recaps by themselves when the profile asks, after a countdown that follows the position (it holds
  // when paused), once each. Each timeupdate, on the real position.
  skipTick.current = (position: number) => {
    if (!info || error) return
    const now = skipAt({ markers: info.markers, time: position, skipIntros: preferences.skipIntros, skipRecaps: preferences.skipRecaps, state: skipState.current })
    skipState.current = now.state
    if (now.skipTo !== undefined) {
      seekTo(now.skipTo)
      setSkipShown({})
      return
    }
    setSkipShown((shown) =>
      shown.button === now.button && shown.counting?.marker === now.counting?.marker && shown.counting?.left === now.counting?.left && shown.counting?.progress === now.counting?.progress
        ? shown
        : { button: now.button, counting: now.counting },
    )
  }
  const pressSkip = () => {
    if (!info || !skippable) return
    const pressed = skipPressed(skipState.current, skippable, info.markers, preferences.skipIntros, preferences.skipRecaps)
    skipState.current = pressed.state
    setSkipShown({})
    seekTo(pressed.skipTo)
  }
  const cancelSkip = () => {
    if (!skipCounting) return
    skipState.current = skipCancelled(skipState.current, skipCounting.marker)
    setSkipShown({})
  }

  useEffect(() => {
    if (!notice) return
    const timer = setTimeout(() => setNotice(undefined), 2500)
    return () => clearTimeout(timer)
  }, [notice])

  // A setting changed from the player's menu (auto-play, Skip credits) acts at once, playing or paused.
  useEffect(() => {
    const el = video.current
    if (el && lastPosition.current !== null) upNextTick.current(el.currentTime)
  }, [upNext?.showAt, upNext?.countdownFrom, upNext?.nextAt, upNext?.closeAt])

  // The end: on to the next episode if the countdown had started; closed after Cancel or with auto-play off; otherwise
  // (the file was shorter than the server said) a countdown runs over the last frame.
  useEffect(() => {
    if (!ended || !info?.next || playingItem.current !== info.item.id || movedOn.current) return
    const end = upNextAtEnd({
      counting: countingLive,
      state: upNextStateRef.current,
      autoPlayNext: preferences.autoPlayNext,
      holds: holds(time + NEXT_EPISODE_COUNTDOWN_SECONDS),
    })
    if (end === 'next') moveOn()
    else if (end === 'close') exit()
    else if (end === 'countdown') setEndCountdown(NEXT_EPISODE_COUNTDOWN_SECONDS)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, as it ends
  }, [ended])

  useEffect(() => {
    if (endCountdown === null) return
    if (endCountdown <= 0) {
      moveOn()
      return
    }
    const timer = setTimeout(() => setEndCountdown((c) => (c === null ? null : c - 1)), 1000)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- moveOn is a plain function of this render
  }, [endCountdown])

  // Get the next episode ready as the card shows, but no earlier than a minute before it would play (the warm-up keeps
  // a stream only so long), and let it go if the viewer heads back into the episode or cancels.
  const wantReady = !!info?.next && !sleepStopped && (upNow.prepare || endCountdown !== null)
  useEffect(() => {
    const next = info?.next
    if (!next) return
    if (!wantReady) {
      if (holdsPrepared(next.id) && !ended) dropPrepared()
      return
    }
    // As the next episode's page will ask for it: from the start (see playNext). A busy server says no, and it starts
    // the usual way.
    const q = quality
    const preset = qualities.find((x) => x.value === q)
    getReady({
      itemId: next.id,
      profile: detectProfile(),
      startPositionSeconds: 0,
      maxHeight: q === 'auto' ? undefined : q,
      maxBitrate: preset?.bitrate,
      forceTranscode: q !== 'auto' || undefined,
      shuffle: shuffle || undefined,
    }, prepareVideo)
  }, [wantReady, info, ended, quality, shuffle])

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
  const sound = useSoundSettings()
  const shaping = useSoundShaping(sound)
  const playingChannels = info?.audioTracks.find((a) => a.index === info.selectedAudioIndex)?.channels ?? null
  // Not until the stream is known; and a converted stream Safari plays itself is left as it comes (see attach).
  const canShape = !!info && !nativeHls
  useSoundChain(video, soundHere ? Math.max(0, audioDelay) : 0, canShape ? shaping : unshaped, playingChannels)

  /**
   * Moves the file's sound by `by` seconds (0 puts it back), for everyone who plays it. Played here, a later sound changes at
   * once; an earlier one, or a stream the server makes, needs a new stream, asked for when the nudging stops.
   */
  function nudgeSound(by: number) {
    const itemId = playingItem.current
    if (!itemId || !info) return
    const moved = by === 0 ? 0 : Math.round((audioDelay + by) * 1000) / 1000
    const next = Math.max(-MAX_AUDIO_DELAY_SECONDS, Math.min(MAX_AUDIO_DELAY_SECONDS, moved))
    if (next === audioDelay) return
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
            setNotice(t('The sound couldn’t be moved just now'))
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
    // Kept for the show (a film's is for this time only). A film's is said too, so a stream being converted is made as
    // fast as it's watched rather than falling behind at 1.5× or 2×.
    if (info?.item.kind === 'Episode' || info?.method === 'Transcode') void setPlaybackSpeed(info.item.id, next).catch(() => undefined)
  }

  // A new converted stream of a film watched faster or slower (another audio track, a recovery) starts at normal pace
  // on the server: it's told the speed again. An episode's new stream already starts at its show's.
  const streamId = info?.sessionId
  useEffect(() => {
    if (!info || info.method !== 'Transcode' || info.item.kind === 'Episode' || speed === 1) return
    void setPlaybackSpeed(info.item.id, speed).catch(() => undefined)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per stream; choosing a speed says it itself
  }, [streamId])

  // ---------- Sleep timer

  useEffect(() => {
    if (!sleep) return
    const timer = window.setInterval(() => setClock(Date.now()), 500)
    return () => window.clearInterval(timer)
  }, [sleep])

  const stillWatching = sleepLeft !== null && sleepLeft <= STILL_WATCHING_SECONDS && playing && !error
  const sleepWhat = info?.item.kind === 'Episode' ? 'episode' : 'movie'

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
      setSleepStopped(true)
      setEndCountdown(null)
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

  /** Saves the profile's preferences, here and for its other devices. */
  function savePreferences(next: PlaybackPreferences) {
    setPreferences(next)
    void savePlayerPreferences(next).catch(() => setPreferences(preferences))
  }

  function chooseSubtitleSize(size: SubtitleSize) {
    savePreferences({ ...preferences, subtitleSize: size })
  }

  function choosePlacement(change: Pick<PlaybackPreferences, 'subtitlePosition'> | Pick<PlaybackPreferences, 'subtitlesInBlackBars'>) {
    savePreferences({ ...preferences, ...change })
  }

  /** Turns one of the profile's playback preferences on or off, here and for its other devices. */
  function togglePreference(key: 'autoPlayNext' | 'skipIntros' | 'skipRecaps' | 'skipCredits') {
    savePreferences({ ...preferences, [key]: !preferences[key] })
  }

  /** Cancel the countdown: play on with only Play now on the card; the player then closes at the end (or now, if it has
   * ended). It holds for the rest of the episode. */
  function cancelUpNext() {
    setUpNextState({ ...upNextStateRef.current, cancelled: true })
    setEndCountdown(null)
    dropPrepared()
    if (ended) exit()
  }

  // ---------- Keyboard

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // The subtitle search dialog has the keys (Escape closes it, not the player).
      if (belongsToFocused(e) || e.metaKey || e.ctrlKey || findingSubtitles) return
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
          pressSkip()
          break
        case 'n':
          playNext()
          break
        case 'Escape':
          if (menu) setMenu(null)
          else if (skipCounting && !showUpNext) cancelSkip()
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
  // Styled subtitles drawn as authored have no plain track as well.
  const selectedText = styledTrack ? undefined : info?.subtitles.find((s) => s.key === subtitleKey && s.isText && s.url)
  // The server shifts the cues; a new address (with the delay in it) fetches them again.
  const textUrl = selectedText?.url ? `${selectedText.url}${selectedText.url.includes('?') ? '&' : '?'}delay=${subtitleDelay}${subtitleVersion ? `&v=${subtitleVersion}` : ''}` : undefined
  const selectedTrack = info?.subtitles.find((s) => s.key === subtitleKey)

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
            // React doesn't pass on a <track>'s load and error events, so they're listened for here.
            ref={(el) => {
              if (!el) return
              const loaded = () => setSubtitleStatus('idle')
              const failed = () => setSubtitleStatus('failed')
              el.addEventListener('load', loaded)
              el.addEventListener('error', failed)
              if (el.readyState === HTMLTrackElement.LOADED) loaded()
              return () => {
                el.removeEventListener('load', loaded)
                el.removeEventListener('error', failed)
              }
            }}
          />
        )}
      </video>

      {/* The sleep timer's last minute dims the picture little by little; stopped, it stays dim until the viewer's back. */}
      {(stillWatching || asleep) && (
        <div className="player-sleep-dim" aria-hidden="true" style={{ opacity: asleep ? 1 : Math.min(1, 1 - (sleepLeft ?? 0) / STILL_WATCHING_SECONDS) }} />
      )}
      {waiting && !error && <div className="player-spinner" role="status" aria-label={t('Loading')} />}
      {subtitleStatus !== 'idle' && (
        <p className="player-toast" role="status">
          {subtitleStatus === 'loading' ? t('Preparing subtitles. They’ll appear in a moment.') : t('These subtitles couldn’t be loaded. Try another track.')}
        </p>
      )}
      {resync && subtitleStatus === 'idle' && (
        <p className="player-toast" role="status">
          {resync.state === 'running' ? t('Re-syncing the subtitles to the audio. This takes a minute or so.') : resync.error}
        </p>
      )}

      <header className="player-top">
        <button className="icon-button" onClick={exit} aria-label={t('Back')}>
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
            {t('Converting: {summary}', { summary: info.transcodeSummary })}
          </span>
        )}
      </header>

      {error && (
        // Keyed by the message, so a new one moves focus to its main button again.
        <div key={error.message} className="player-error" role="alertdialog" aria-modal="true" aria-labelledby="player-error-message" aria-describedby={error.kind === 'takenOver' ? 'player-error-note' : undefined}>
          <p id="player-error-message">{error.message}</p>
          {error.kind === 'takenOver' && (
            <p id="player-error-note" className="player-error-note muted">
              {t('The other screen will stop if you watch here.')}
            </p>
          )}
          <div className="player-error-actions">
            {/* Starts afresh rather than picking up the stream that was ended, so it can take the place back. */}
            <button className="button" autoFocus onClick={() => void retry(error.kind === 'other' ? { forceTranscode: true } : {})}>
              {error.kind === 'takenOver' ? t('Watch here instead') : t('Try again')}
            </button>
            <button className="button quiet" onClick={exit}>
              {t('Go back')}
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
          <p id="still-watching-title">{t('Still watching?')}</p>
          <p id="still-watching-note" className="muted">
            {t('Pausing in {seconds} s', { seconds: Math.max(1, Math.ceil(sleepLeft ?? 0)) })}
          </p>
          <div className="player-error-actions">
            <button className="button" autoFocus onClick={() => chooseSleep(extended(sleep, Date.now()))}>
              {t('+15 min')}
            </button>
            <button className="button quiet" onClick={() => chooseSleep(null)}>
              {t('Turn off timer')}
            </button>
          </div>
        </div>
      )}

      {/* Said once as a countdown starts: its number ticks every second, so it stays out of the live region. */}
      <div className="visually-hidden" aria-live="polite">
        {skipCounting && !showUpNext && !error ? (skipCounting.marker.type === 'Recap' ? t(Words.skipRecap) : t(Words.skipIntro)) : ''}
      </div>

      {skippable && !showUpNext && !error && (
        // One split pill, as Next Episode's: the skip and, while counting down, its X. Each half is its own button.
        <div className={skipCounting ? 'skip-pill counting' : 'skip-pill'}>
          <button
            className="skip-pill-skip"
            onClick={pressSkip}
            autoFocus
            style={{ '--fill': `${(skipCounting?.progress ?? 0) * 100}%` } as CSSProperties}
          >
            <span className="skip-pill-label">{skipLabel}</span>
            {/* The fill: white sweeping across the grey, with the words dark where it has reached. */}
            {skipCounting && (
              <span className="pill-fill" aria-hidden="true">
                <span className="skip-pill-label">{skipLabel}</span>
              </span>
            )}
          </button>
          {skipCounting && (
            <button className="skip-pill-cancel" onClick={cancelSkip} aria-label={t(Words.cancel)}>
              <icon.removeFromRow />
            </button>
          )}
        </div>
      )}

      {/* Said once as the card shows: the countdown's number ticks every second, so it stays out of the live region. */}
      <div className="visually-hidden" aria-live="polite">
        {showUpNext && info?.next && !error ? t('Next episode: {code} {title}', { code: episodeCode(info.next), title: info.next.title }) : ''}
      </div>

      {showUpNext && info?.next && !error && (
        <aside className="up-next" ref={setUpNextCard} aria-label={t('Next episode')}>
          <div className="up-next-still">
            <img src={imageUrl(info.next.artwork.thumb ?? info.next.artwork.backdrop, 500)} alt="" />
            {counting && (
              <svg viewBox="0 0 36 36" className="up-next-ring" aria-hidden="true">
                <circle cx="18" cy="18" r="16" style={{ strokeDashoffset: 100 - (countdown / NEXT_EPISODE_COUNTDOWN_SECONDS) * 100 }} pathLength={100} />
              </svg>
            )}
          </div>
          <div className="up-next-text">
            <span className="muted up-next-count">
              {counting ? t(Words.nextEpisodeIn, { seconds: countdown }) : t('Next episode')}
            </span>
            <strong>
              {episodeCode(info.next)} {info.next.title}
            </strong>
            {/* One split pill: Play now and, while counting down, its X. Each half is its own button. */}
            <div className={counting ? 'up-next-pill counting' : 'up-next-pill'}>
              <button
                className="up-next-play"
                onClick={moveOn}
                style={counting ? ({ '--fill': `${((NEXT_EPISODE_COUNTDOWN_SECONDS - countdown) / NEXT_EPISODE_COUNTDOWN_SECONDS) * 100}%` } as CSSProperties) : undefined}
              >
                <span className="up-next-play-label">
                  <icon.play /> {t('Play now')}
                </span>
                {counting && (
                  <span className="pill-fill" aria-hidden="true">
                    <span className="up-next-play-label">
                      <icon.play /> {t('Play now')}
                    </span>
                  </span>
                )}
              </button>
              {counting && (
                <button className="up-next-cancel" onClick={cancelUpNext} aria-label={t(Words.cancel)}>
                  <icon.removeFromRow />
                </button>
              )}
            </div>
          </div>
        </aside>
      )}

      {/* Hidden behind an error: nothing there would work, and focus belongs to the error's buttons. */}
      <footer className="player-controls" ref={controls} hidden={!!error} inert={!!error}>
        <Scrubber time={time} duration={duration} bufferedTo={bufferedTo} markers={info?.markers ?? []} previews={previews} onSeek={seekTo} />
        <div className="player-buttons">
          <button className="icon-button" onClick={toggle} aria-label={playing ? t('Pause') : t(Words.play)}>
            {playing ? <icon.pause /> : <icon.play />}
          </button>
          <button className="icon-button" onClick={() => seekBy(-10)} aria-label={t('Back 10 seconds')}>
            <Replay10 />
          </button>
          <button className="icon-button" onClick={() => seekBy(10)} aria-label={t('Forward 10 seconds')}>
            <Forward10 />
          </button>
          <button className="icon-button" onClick={playPrevious} aria-label={t('Previous')}>
            <Previous />
          </button>
          {info?.next && (
            <button className="icon-button" onClick={playNext} aria-label={t('Next episode')}>
              <Next />
            </button>
          )}
          <div className="player-volume">
            <button className="icon-button" onClick={() => video.current && (video.current.muted = !video.current.muted)} aria-label={muted ? t('Unmute') : t('Mute')}>
              {muted || volume === 0 ? <Muted /> : <Volume />}
            </button>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={muted ? 0 : volume}
              aria-label={t('Volume')}
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
            <button className="icon-button" aria-label={t(Words.subtitles)} aria-expanded={menu === 'subtitles'} onClick={() => setMenu(menu === 'subtitles' ? null : 'subtitles')}>
              <Captions />
            </button>
            {menu === 'subtitles' && info && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>{t(Words.subtitles)}</h3>
                  <MenuItem selected={!subtitleKey} onClick={() => chooseSubtitle(null)}>
                    {t('Off')}
                  </MenuItem>
                  {info.subtitles.map((s) => (
                    <MenuItem key={s.key} selected={s.key === subtitleKey} onClick={() => chooseSubtitle(s.key)}>
                      {s.label}
                      {!s.isText && <span className="muted"> {t('(burned in)')}</span>}
                    </MenuItem>
                  ))}
                  {info.canSearchSubtitles && !kids && (
                    <button className="player-menu-item" onClick={searchForSubtitles}>
                      {t('Search for subtitles…')}
                    </button>
                  )}
                  {info.canSearchSubtitles && !kids && selectedTrack?.isExternal && (
                    <button className="player-menu-item" disabled={resync?.state === 'running'} onClick={() => void resyncSubtitles()}>
                      {resync?.state === 'running' ? t('Re-syncing…') : t('Re-sync to audio')}
                    </button>
                  )}
                  {selectedText && (
                    <>
                      <h3>{t('Timing')}</h3>
                      <div className="player-menu-stepper">
                        <button aria-label={t('Show subtitles earlier')} onClick={() => nudgeSubtitles(-0.25)}>
                          −
                        </button>
                        <span aria-live="polite">{delayLabel(subtitleDelay)}</span>
                        <button aria-label={t('Show subtitles later')} onClick={() => nudgeSubtitles(0.25)}>
                          +
                        </button>
                      </div>
                      {subtitleDelay !== 0 && (
                        <button className="player-menu-item" onClick={() => nudgeSubtitles(0)}>
                          {t('Reset timing')}
                        </button>
                      )}
                    </>
                  )}
                </div>
                {info.subtitles.some((s) => s.isText) && (
                  <div className="player-menu-column">
                    <h3>{t('Subtitle size')}</h3>
                    {subtitleSizes.map((s) => (
                      <MenuItem key={s.value} selected={(preferences.subtitleSize ?? 'Standard') === s.value} onClick={() => chooseSubtitleSize(s.value)}>
                        {t(s.label)}
                      </MenuItem>
                    ))}
                    <h3>{t('Position')}</h3>
                    {subtitlePositions.map((p) => (
                      <MenuItem key={p} selected={position === p} onClick={() => choosePlacement({ subtitlePosition: p })}>
                        {t(p)}
                      </MenuItem>
                    ))}
                    <MenuItem selected={blackBars} onClick={() => choosePlacement({ subtitlesInBlackBars: !blackBars })}>
                      {t('In the black bars')}
                    </MenuItem>
                  </div>
                )}
              </div>
            )}
          </div>

          {info && info.audioTracks.length > 0 && (
          <div className="menu-anchor">
            <button className="icon-button" aria-label={t('Audio')} aria-expanded={menu === 'audio'} onClick={() => setMenu(menu === 'audio' ? null : 'audio')}>
              <Waveform />
            </button>
            {menu === 'audio' && (
              <div className="player-menu" role="menu">
                {info.audioTracks.length > 0 && (
                  <div className="player-menu-column">
                    {info.audioTracks.length > 1 && (
                      <>
                        <h3>{t('Audio')}</h3>
                        {info.audioTracks.map((a) => (
                          <MenuItem key={a.index} selected={a.index === info.selectedAudioIndex} onClick={() => chooseAudio(a.index)}>
                            {a.label}
                            <span className="muted"> {[a.codec.toUpperCase(), a.channels && a.channels > 2 ? `${a.channels - 1}.1` : null].filter(Boolean).join(' ')}</span>
                          </MenuItem>
                        ))}
                      </>
                    )}
                    {/* Fixes the file for everyone, so it's worth saying so. */}
                    {/* A click moves it 10 ms; held, it jumps 50 ms at a time. */}
                    <h3>{t('Audio sync')}</h3>
                    <div className="player-menu-stepper">
                      <HoldButton aria-label={t('Play the sound earlier')} title={t('Hold to move faster')} onStep={(held) => nudgeSound(held ? -0.05 : -0.01)}>
                        −
                      </HoldButton>
                      <span aria-live="polite">{syncLabel(audioDelay)}</span>
                      <HoldButton aria-label={t('Play the sound later')} title={t('Hold to move faster')} onStep={(held) => nudgeSound(held ? 0.05 : 0.01)}>
                        +
                      </HoldButton>
                      <button aria-label={t('Reset sync')} title={t('Reset sync')} disabled={audioDelay === 0} onClick={() => nudgeSound(0)}>
                        <Reset />
                      </button>
                    </div>
                  </div>
                )}
                {info.audioTracks.length > 0 && canShape && (
                  <div className="player-menu-column sound">
                    <h3 id="sound-boost">{t('Dialogue boost')}</h3>
                    <Segments labelledBy="sound-boost" choices={dialogueBoostChoices.map((b) => ({ value: b, label: t(b) }))} value={sound.dialogueBoost} onChoose={(dialogueBoost) => writeSoundSettings({ dialogueBoost })} />
                    <h3 id="sound-night">{t('Night mode')}</h3>
                    <Segments
                      labelledBy="sound-night"
                      choices={nightModeChoices.map((n) => ({
                        value: n,
                        label: n === 'Scheduled' ? `${timeOfDay(sound.nightStart)}–${timeOfDay(sound.nightEnd)}` : t(n),
                        spoken: n === 'Scheduled' ? t('{start} to {end}, on a schedule', { start: timeOfDay(sound.nightStart), end: timeOfDay(sound.nightEnd) }) : undefined,
                      }))}
                      value={sound.nightMode}
                      onChoose={(nightMode) => writeSoundSettings({ nightMode })}
                    />
                    <h3 id="sound-speakers">{t('Speakers')}</h3>
                    <Segments labelledBy="sound-speakers" choices={speakerChoices} value={sound.speakers} onChoose={(speakers) => writeSoundSettings({ speakers })} />
                  </div>
                )}
              </div>
            )}
          </div>
          )}

          <div className="menu-anchor">
            <button
              className={speed !== 1 ? 'player-quality active' : 'player-quality'}
              aria-expanded={menu === 'speed'}
              aria-label={t('Speed, {speed}', { speed: speedLabel(speed) })}
              onClick={() => setMenu(menu === 'speed' ? null : 'speed')}
            >
              {speedLabel(speed)}
            </button>
            {menu === 'speed' && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>{t('Speed')}</h3>
                  {speeds.map((s) => (
                    <MenuItem key={s} selected={s === speed} onClick={() => chooseSpeed(s)}>
                      {s === 1 ? t('Normal') : speedLabel(s)}
                    </MenuItem>
                  ))}
                  {info?.item.kind === 'Episode' && <p className="player-menu-note muted">{t('Kept for this show.')}</p>}
                </div>
              </div>
            )}
          </div>

          <div className="menu-anchor">
            <button
              className="player-quality"
              aria-expanded={menu === 'quality'}
              aria-label={t('Quality: {quality}', { quality: quality === 'auto' ? (info?.method === 'DirectPlay' ? t('Original') : t('Auto')) : `${quality}p` })}
              onClick={() => setMenu(menu === 'quality' ? null : 'quality')}
            >
              {quality === 'auto' ? (info?.method === 'DirectPlay' ? t('Original') : t('Auto')) : `${quality}p`}
            </button>
            {menu === 'quality' && (
              <div className="player-menu" role="menu">
                <div className="player-menu-column">
                  <h3>{t('Quality')}</h3>
                  {qualities.map((q) => (
                    <MenuItem key={q.value} selected={q.value === quality} onClick={() => chooseQuality(q.value)}>
                      {q.value === 'auto' ? t(q.label) : q.label}
                    </MenuItem>
                  ))}
                </div>
                <div className="player-menu-column">
                  <h3>{t('While watching')}</h3>
                  <MenuItem toggle selected={preferences.autoPlayNext} onClick={() => togglePreference('autoPlayNext')}>
                    {t(Words.autoPlayNext)}
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipIntros} onClick={() => togglePreference('skipIntros')}>
                    {t('Skip intros')}
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipRecaps} onClick={() => togglePreference('skipRecaps')}>
                    {t('Skip recaps')}
                  </MenuItem>
                  <MenuItem toggle selected={preferences.skipCredits} onClick={() => togglePreference('skipCredits')}>
                    {t(Words.skipCredits)}
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
                  <h3>{t('Sleep timer')}</h3>
                  {sleepChoices(sleepWhat).map((choice, i, all) => (
                    <MenuItem
                      key={choice.label}
                      // The last choice waits for the end.
                      selected={i === all.length - 1 ? !!sleep && 'endOfEpisode' in sleep : false}
                      onClick={() => chooseSleep(choice.start())}
                    >
                      {choice.label}
                    </MenuItem>
                  ))}
                  {sleep && (
                    <button className="player-menu-item" onClick={() => chooseSleep(null)}>
                      {t('Turn off')}
                    </button>
                  )}
                </div>
              </div>
            )}
          </div>

          {'pictureInPictureEnabled' in document && document.pictureInPictureEnabled && (
            <button className="icon-button" aria-label={t('Picture in picture')} onClick={() => void video.current?.requestPictureInPicture?.()}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
                <rect x="3" y="5" width="18" height="14" rx="2" />
                <rect x="12" y="11" width="7" height="6" rx="1" fill="currentColor" />
              </svg>
            </button>
          )}
          {'webkitShowPlaybackTargetPicker' in HTMLVideoElement.prototype && (
            <button
              className="icon-button"
              aria-label={t('AirPlay')}
              onClick={() => (video.current as unknown as { webkitShowPlaybackTargetPicker: () => void }).webkitShowPlaybackTargetPicker()}
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
                <path d="M5 17H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-1" />
                <path d="m12 15 5 6H7Z" fill="currentColor" />
              </svg>
            </button>
          )}
          <button className="icon-button" onClick={toggleFullscreen} aria-label={fullscreen ? t('Exit full screen') : t('Full screen')}>
            {fullscreen ? <ExitFullscreen /> : <Fullscreen />}
          </button>
        </div>
      </footer>

      {/* Inside the player, so it shows in full screen too. */}
      {findingSubtitles && info && (
        <SubtitleSearchDialog
          itemId={info.item.id}
          fileId={info.fileId}
          title={info.item.kind === 'Episode' && series ? `${series.title} ${episodeCode(info.item) ?? ''}`.trim() : info.item.title}
          onDownloaded={(saved) => {
            setFindingSubtitles(false)
            void restartHere({ subtitleKey: saved.key, audioStreamIndex: info.selectedAudioIndex ?? undefined })
          }}
          onClose={() => {
            setFindingSubtitles(false)
            void video.current?.play()
          }}
        />
      )}
    </div>
  )
}

/** A setting's few choices in one row: Dialogue boost, Night mode and Speakers. */
function Segments<T extends string>({ labelledBy, choices, value, onChoose }: { labelledBy: string; choices: { value: T; label: string; spoken?: string }[]; value: T; onChoose: (value: T) => void }) {
  return (
    <div className="player-menu-segments" role="group" aria-labelledby={labelledBy}>
      {choices.map((c) => (
        <button key={c.value} role="menuitemradio" aria-checked={c.value === value} aria-label={c.spoken} onClick={() => onChoose(c.value)}>
          {c.label}
        </button>
      ))}
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
      aria-label={t('Seek')}
      aria-valuemin={0}
      aria-valuemax={Math.round(duration)}
      aria-valuenow={Math.round(time)}
      aria-valuetext={t('{time} of {duration}', { time: formatClock(time), duration: formatClock(duration) })}
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
      // A touch drag the browser takes over (it turned into a scroll) ends without a seek.
      onPointerCancel={() => {
        setDragging(false)
        setHover(null)
      }}
    >
      <div className="scrubber-track">
        <span className="scrubber-buffered" style={{ width: pct(bufferedTo) }} />
        {markers.map((m) => (
          <span key={`${m.type}-${m.start}`} className={`scrubber-marker marker-${m.type.toLowerCase()}`} style={{ left: pct(m.start), width: `calc(${pct(m.end)} - ${pct(m.start)})` }} title={m.type === 'Intro' ? t('Intro') : m.type === 'Recap' ? t('Recap') : m.type === 'Credits' ? t('Credits') : m.type} />
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
