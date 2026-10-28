import Hls from 'hls.js/light'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { ApiError, getLiveChannels, liveHeartbeat, removeRecording, stopLiveChannel, watchLiveChannel, type LiveChannel, type LiveWatch } from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { FavouriteButton } from '../components/FavouriteButton'
import { Back, ChevronDown, ChevronUp, ExitFullscreen, Fullscreen, Muted, Pause, Play, Replay10, Forward10, Star, Tv, Volume } from '../components/icons'
import { useFavourites } from '../hooks/useFavourites'
import { usePolling } from '../hooks/usePolling'
import { formatClock } from '../lib/format'
import { belongsToFocused } from '../lib/focusedKeys'
import { mediaErrorRecovery } from '../lib/hlsRecovery'
import { prefersNativeHls } from '../lib/profile'
import { programmeTime, progress } from '../lib/livetv'

const HIDE_CHROME_MS = 3000
/** Within this many seconds of the newest segment counts as live (players sit a few segments back). */
const LIVE_SLACK_SECONDS = 20
const HEARTBEAT_MS = 15_000

/** A channel, full screen, with rewind back through what the server keeps of it, and channel up and down. */
export default function LivePlayerPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const container = useRef<HTMLDivElement>(null)
  const video = useRef<HTMLVideoElement>(null)
  const hls = useRef<Hls | null>(null)
  const viewer = useRef<string | undefined>(undefined)
  const hideTimer = useRef<number | undefined>(undefined)
  // Counts channel starts, so only the latest one's answer is used when someone flicks quickly.
  const starts = useRef(0)

  const [channels, setChannels] = useState<LiveChannel[]>([])
  const [watch, setWatch] = useState<LiveWatch>()
  const [error, setError] = useState<string>()
  /** Every stream is recording: what, so the viewer can watch that channel instead or stop the recording. */
  const [blocked, setBlocked] = useState<{ recordingId: string; channelId: string; channelName: string }>()
  const [playing, setPlaying] = useState(false)
  const [waiting, setWaiting] = useState(true)
  const [window_, setWindow] = useState({ start: 0, end: 0, time: 0 })
  const [volume, setVolume] = useState(1)
  const [muted, setMuted] = useState(false)
  const [fullscreen, setFullscreen] = useState(false)
  const [chrome, setChrome] = useState(true)
  const [list, setList] = useState(false)
  const { isFavourite, toggle: toggleFavourite } = useFavourites()

  const loadChannels = useCallback(() => {
    getLiveChannels().then(setChannels, () => undefined)
  }, [])
  useEffect(loadChannels, [loadChannels])
  // What's on moves on; the now-and-next is fetched again each minute.
  usePolling(loadChannels, 60_000)

  const attach = useCallback((playlist: string) => {
    const el = video.current
    if (!el) return
    hls.current?.destroy()
    hls.current = null
    if (!prefersNativeHls() && Hls.isSupported()) {
      // Start a few segments back from the newest, and never jump forward on its own: the viewer may have rewound.
      const player = new Hls({ liveSyncDurationCount: 3, liveMaxLatencyDurationCount: Infinity, backBufferLength: 120, maxBufferLength: 30 })
      const stopped = () => setError('The channel stopped sending. Try again, or pick another channel.')
      const onMediaError = mediaErrorRecovery(player, stopped)
      player.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal || hls.current !== player) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) onMediaError()
        else stopped()
      })
      player.once(Hls.Events.MANIFEST_PARSED, () => el.play().catch(() => setPlaying(false)))
      player.loadSource(playlist)
      player.attachMedia(el)
      hls.current = player
    } else {
      el.src = playlist
      el.play().catch(() => setPlaying(false))
    }
  }, [])

  const start = useCallback(
    async (channelId: string) => {
      const request = ++starts.current
      setError(undefined)
      setBlocked(undefined)
      setWaiting(true)
      // The old channel's player stops first: the server lets that channel go as this one starts.
      hls.current?.destroy()
      hls.current = null
      try {
        const started = await watchLiveChannel(channelId, viewer.current)
        if (request !== starts.current) {
          // A newer channel was asked for meanwhile; let this one go.
          void stopLiveChannel(started.viewer).catch(() => undefined)
          return
        }
        viewer.current = started.viewer
        setWatch(started)
        attach(started.playlist)
      } catch (e) {
        if (request !== starts.current) return
        viewer.current = undefined
        setWatch(undefined)
        setWaiting(false)
        const problem = e instanceof ApiError && e.status === 409 ? e.problem : undefined
        if (problem && typeof problem.recordingId === 'string' && typeof problem.channelId === 'string') {
          setBlocked({ recordingId: problem.recordingId, channelId: problem.channelId, channelName: String(problem.channelName ?? 'that channel') })
        }
        setError(e instanceof ApiError && e.messages.length ? e.messages.join(' ') : 'The channel couldn’t be played.')
      }
    },
    [attach],
  )

  useEffect(() => {
    void start(id)
  }, [id, start])

  // Let the channel go when the page closes; the server notices anyway if this never arrives.
  useEffect(
    () => () => {
      hls.current?.destroy()
      hls.current = null
      if (viewer.current) void stopLiveChannel(viewer.current).catch(() => undefined)
    },
    [],
  )

  // Paused players fetch nothing, so the player says it's still here.
  usePolling(() => {
    if (!viewer.current) return
    void liveHeartbeat(viewer.current).catch((e: unknown) => {
      // Stopped for a recording: say so (the picture would otherwise just stop).
      if (e instanceof ApiError && e.status === 410) {
        hls.current?.destroy()
        hls.current = null
        viewer.current = undefined
        setError(e.messages.join(' ') || 'A recording needed the stream.')
      }
    })
  }, HEARTBEAT_MS)

  const measure = () => {
    const el = video.current
    if (!el || el.seekable.length === 0) return
    const last = el.seekable.length - 1
    setWindow({ start: el.seekable.start(0), end: el.seekable.end(last), time: el.currentTime })
  }

  const behind = Math.max(0, window_.end - window_.time)
  const atLive = behind < LIVE_SLACK_SECONDS
  const seekTo = (seconds: number) => {
    const el = video.current
    if (!el) return
    el.currentTime = Math.min(Math.max(seconds, window_.start), window_.end)
    measure()
  }
  const goLive = () => {
    const el = video.current
    if (!el) return
    el.currentTime = hls.current?.liveSyncPosition ?? Math.max(window_.start, window_.end - 12)
    void el.play().catch(() => undefined)
  }
  const toggle = () => {
    const el = video.current
    if (!el) return
    if (el.paused) void el.play().catch(() => undefined)
    else el.pause()
  }

  const index = channels.findIndex((c) => c.id === id)
  const channel = channels[index] ?? watch?.channel
  const step = (by: number) => {
    if (channels.length === 0) return
    const next = channels[(Math.max(0, index) + by + channels.length) % channels.length]
    navigate(`/live/${next.id}`, { replace: true })
  }

  const showChrome = () => {
    setChrome(true)
    window.clearTimeout(hideTimer.current)
    hideTimer.current = window.setTimeout(() => setChrome(false), HIDE_CHROME_MS)
  }
  useEffect(() => () => window.clearTimeout(hideTimer.current), [])

  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen()
    else void container.current?.requestFullscreen()
  }
  useEffect(() => {
    const change = () => setFullscreen(!!document.fullscreenElement)
    document.addEventListener('fullscreenchange', change)
    return () => document.removeEventListener('fullscreenchange', change)
  }, [])

  const exit = () => navigate('/livetv')

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (belongsToFocused(e) || e.metaKey || e.ctrlKey || e.altKey) return
      const handled = (() => {
        switch (e.key) {
          case ' ':
          case 'k':
            toggle()
            return true
          case 'ArrowLeft':
            seekTo(window_.time - 10)
            return true
          case 'ArrowRight':
            seekTo(window_.time + 10)
            return true
          case 'ArrowUp':
          case 'PageUp':
            step(-1)
            return true
          case 'ArrowDown':
          case 'PageDown':
            step(1)
            return true
          case 'l':
            goLive()
            return true
          case 'c':
            setList((open) => !open)
            return true
          case 'f':
            toggleFullscreen()
            return true
          case 'm':
            if (video.current) video.current.muted = !video.current.muted
            return true
          case 'Escape':
            if (list) {
              setList(false)
              return true
            }
            return false
          default:
            return false
        }
      })()
      if (handled) {
        e.preventDefault()
        showChrome()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  const chromeShown = chrome || !playing || !!error || list
  const span = Math.max(1, window_.end - window_.start)
  // Players sit a few seconds behind the newest segment; at live, the bar reads as full.
  const position = `${(atLive ? 1 : Math.min(1, Math.max(0, (window_.time - window_.start) / span))) * 100}%`
  const now = channel?.now

  return (
    <div
      ref={container}
      className={chromeShown ? 'player live-player' : 'player live-player chrome-hidden'}
      onPointerMove={showChrome}
      onClick={(e) => e.target === video.current && toggle()}
    >
      <video
        ref={video}
        playsInline
        onPlay={() => setPlaying(true)}
        onPause={() => setPlaying(false)}
        onWaiting={() => setWaiting(true)}
        onPlaying={() => setWaiting(false)}
        onCanPlay={() => setWaiting(false)}
        onTimeUpdate={measure}
        onProgress={measure}
        onVolumeChange={(e) => {
          setVolume(e.currentTarget.volume)
          setMuted(e.currentTarget.muted)
        }}
      />

      {waiting && !error && <div className="player-spinner" role="status" aria-label="Loading" />}

      <header className="player-top">
        <button className="icon-button" onClick={exit} aria-label="Back to Live TV">
          <Back />
        </button>
        {channel && (
          <div className="live-player-channel">
            {channel.logoUrl ? <img src={channel.logoUrl} alt="" /> : <Tv />}
            <div className="player-title">
              <span>{now?.title ?? channel.name}</span>
              <span className="muted">
                {channel.number} {channel.name}
                {now && ` · ${programmeTime(now)}`}
              </span>
            </div>
          </div>
        )}
        {channel && <FavouriteButton name={channel.name} favourite={isFavourite(channel)} onToggle={() => toggleFavourite(channel)} className="live-player-star" />}
        {watch && watch.method !== 'Original' && <span className="player-badge">{watch.method}</span>}
      </header>

      {error && (
        <div key={error} className="player-error" role="alertdialog" aria-modal="true" aria-labelledby="live-error-message">
          <p id="live-error-message">{error}</p>
          <div className="player-error-actions">
            {blocked ? (
              <>
                <button className="button" autoFocus onClick={() => navigate(`/live/${blocked.channelId}`, { replace: true })}>
                  Watch {blocked.channelName}
                </button>
                <AsyncButton
                  className="button quiet"
                  onClick={async () => {
                    await removeRecording(blocked.recordingId).catch(() => undefined)
                    // The recording takes a moment to let its stream go.
                    await new Promise((done) => window.setTimeout(done, 2500))
                    void start(id)
                  }}
                >
                  Stop the recording
                </AsyncButton>
              </>
            ) : (
              <button className="button" autoFocus onClick={() => void start(id)}>
                Try again
              </button>
            )}
            <button className="button quiet" onClick={() => setList(true)}>
              Other channels
            </button>
            <button className="button quiet" onClick={exit}>
              Go back
            </button>
          </div>
        </div>
      )}

      {list && (
        <aside className="live-channel-list" aria-label="Channels">
          <ol>
            {/* Favourites first, then the rest in number order. */}
            {[...channels.filter(isFavourite), ...channels.filter((c) => !isFavourite(c))].map((c) => (
              <li key={c.id}>
                <button
                  className={c.id === id ? 'live-channel-row current' : 'live-channel-row'}
                  aria-current={c.id === id ? 'true' : undefined}
                  autoFocus={c.id === id}
                  onClick={() => {
                    setList(false)
                    if (c.id !== id) navigate(`/live/${c.id}`, { replace: true })
                  }}
                >
                  <span className="live-channel-number">{c.number}</span>
                  {c.logoUrl ? <img src={c.logoUrl} alt="" /> : <span className="live-logo-fallback" />}
                  <span className="live-channel-text">
                    <strong>
                      {c.name}
                      {isFavourite(c) && <Star filled className="chip-star live-list-star" aria-label="Favourite" />}
                    </strong>
                    <span className="muted">{c.now?.title ?? 'No listings'}</span>
                  </span>
                </button>
              </li>
            ))}
          </ol>
        </aside>
      )}

      <footer className="player-controls" hidden={!!error} inert={!!error}>
        {now && (
          <div className="live-programme-progress" aria-hidden="true">
            <span style={{ width: `${progress(now) * 100}%` }} />
          </div>
        )}
        <div
          className="scrubber"
          role="slider"
          tabIndex={0}
          aria-label="Rewind"
          aria-valuemin={0}
          aria-valuemax={Math.round(span)}
          aria-valuenow={Math.round(window_.time - window_.start)}
          aria-valuetext={atLive ? 'Live' : `${formatClock(behind)} behind live`}
          onPointerDown={(e) => {
            const rect = e.currentTarget.getBoundingClientRect()
            seekTo(window_.start + ((e.clientX - rect.left) / rect.width) * span)
          }}
        >
          <div className="scrubber-track">
            <span className="scrubber-played" style={{ width: position }} />
            <span className="scrubber-thumb" style={{ left: position }} />
          </div>
        </div>
        <div className="player-buttons">
          <button className="icon-button" onClick={toggle} aria-label={playing ? 'Pause' : 'Play'}>
            {playing ? <Pause /> : <Play />}
          </button>
          <button className="icon-button" onClick={() => seekTo(window_.time - 10)} aria-label="Back 10 seconds">
            <Replay10 />
          </button>
          <button className="icon-button" onClick={() => seekTo(window_.time + 10)} aria-label="Forward 10 seconds" disabled={atLive}>
            <Forward10 />
          </button>
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
          <button className={atLive ? 'live-badge on' : 'live-badge'} onClick={goLive} disabled={atLive} aria-label={atLive ? 'Live' : 'Jump to live'}>
            <span className="live-dot" aria-hidden="true" />
            {atLive ? 'Live' : `−${formatClock(behind)}`}
          </button>
          <span className="player-spacer" />
          <button className="icon-button" onClick={() => step(-1)} aria-label="Previous channel">
            <ChevronUp />
          </button>
          <button className="icon-button" onClick={() => step(1)} aria-label="Next channel">
            <ChevronDown />
          </button>
          <button className="player-quality" onClick={() => setList((open) => !open)} aria-expanded={list}>
            Channels
          </button>
          <button className="icon-button" onClick={toggleFullscreen} aria-label={fullscreen ? 'Exit full screen' : 'Full screen'}>
            {fullscreen ? <ExitFullscreen /> : <Fullscreen />}
          </button>
        </div>
      </footer>
    </div>
  )
}
