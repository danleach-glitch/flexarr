import Hls from 'hls.js'
import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { beaconProgress, imageUrl, reportProgress, startPlayback, stopPlayback, type PlaybackInfo } from '../api/client'
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
  Volume,
} from '../components/icons'
import { episodeCode, formatClock } from '../lib/format'
import { detectProfile, prefersNativeHls } from '../lib/profile'

type Quality = 'auto' | 1080 | 720 | 480
const qualities: { value: Quality; label: string; bitrate?: number }[] = [
  { value: 'auto', label: 'Best for this device' },
  { value: 1080, label: '1080p', bitrate: 10_000_000 },
  { value: 720, label: '720p', bitrate: 4_000_000 },
  { value: 480, label: '480p', bitrate: 1_500_000 },
]

const UP_NEXT_SECONDS = 10
const HIDE_CHROME_MS = 3000

interface StartOptions {
  audioStreamIndex?: number
  subtitleKey?: string
  startPositionSeconds?: number
  quality?: Quality
  forceTranscode?: boolean
}

export function PlayerPage() {
  const { id = '' } = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()

  const container = useRef<HTMLDivElement>(null)
  const video = useRef<HTMLVideoElement>(null)
  const hls = useRef<Hls | null>(null)
  const session = useRef<string | undefined>(undefined)
  const hideTimer = useRef<number | undefined>(undefined)

  const [info, setInfo] = useState<PlaybackInfo>()
  const [error, setError] = useState<string>()
  const [playing, setPlaying] = useState(false)
  const [waiting, setWaiting] = useState(true)
  const [time, setTime] = useState(0)
  const [bufferedTo, setBufferedTo] = useState(0)
  const [volume, setVolume] = useState(1)
  const [muted, setMuted] = useState(false)
  const [fullscreen, setFullscreen] = useState(false)
  const [chrome, setChrome] = useState(true)
  const [menu, setMenu] = useState<'subtitles' | 'audio' | 'quality' | null>(null)
  const [quality, setQuality] = useState<Quality>('auto')
  const [subtitleKey, setSubtitleKey] = useState<string | null>(null)
  const [upNextCancelled, setUpNextCancelled] = useState(false)
  const [countdown, setCountdown] = useState(UP_NEXT_SECONDS)

  const duration = info?.durationSeconds ?? 0

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
      player.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) player.recoverMediaError()
        else setError('The stream stopped. The server may have had trouble converting this file.')
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

  const start = useCallback(
    async (options: StartOptions = {}) => {
      setError(undefined)
      const q = options.quality ?? quality
      const preset = qualities.find((x) => x.value === q)
      try {
        const playback = await startPlayback({
          itemId: id,
          profile: detectProfile(),
          audioStreamIndex: options.audioStreamIndex,
          subtitleKey: options.subtitleKey,
          startPositionSeconds: options.startPositionSeconds,
          maxHeight: q === 'auto' ? undefined : q,
          maxBitrate: preset?.bitrate,
          forceTranscode: options.forceTranscode || q !== 'auto' || undefined,
        })
        if (session.current && session.current !== playback.sessionId) void stopPlayback(session.current)
        session.current = playback.sessionId
        setInfo(playback)
        setSubtitleKey(playback.selectedSubtitleKey)
        attach(playback, options.startPositionSeconds ?? playback.startPositionSeconds)
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e))
      }
    },
    [id, quality, attach],
  )

  useEffect(() => {
    setUpNextCancelled(false)
    setCountdown(UP_NEXT_SECONDS)
    const t = params.get('t')
    const el = video.current
    void start({ startPositionSeconds: t !== null ? Number(t) : undefined })
    return () => {
      hls.current?.destroy()
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
    const onTime = () => {
      setTime(el.currentTime)
      for (let i = 0; i < el.buffered.length; i++) {
        if (el.buffered.start(i) <= el.currentTime + 0.5 && el.buffered.end(i) >= el.currentTime) setBufferedTo(el.buffered.end(i))
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
      else setError("This file couldn't be played.")
    }
    const onEnded = () => {
      if (session.current) void reportProgress(session.current, el.currentTime, true, true).catch(() => undefined)
      if (info?.next && !upNextCancelled) navigate(`/play/${info.next.id}?t=0`, { replace: true })
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
    window.clearTimeout(hideTimer.current)
    hideTimer.current = window.setTimeout(() => setChrome(false), HIDE_CHROME_MS)
  }, [])

  useEffect(() => {
    wake()
    return () => window.clearTimeout(hideTimer.current)
  }, [wake])

  const chromeShown = chrome || !playing || menu !== null || !!error

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
  const showUpNext = !!info?.next && !upNextCancelled && time >= upNextAt && duration > 0

  useEffect(() => {
    if (!showUpNext || !playing) return
    const timer = setInterval(() => setCountdown((c) => c - 1), 1000)
    return () => clearInterval(timer)
  }, [showUpNext, playing])

  useEffect(() => {
    if (showUpNext && countdown <= 0 && info?.next) navigate(`/play/${info.next.id}?t=0`, { replace: true })
  }, [countdown, showUpNext, info, navigate])

  // ---------- Keyboard

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.metaKey || e.ctrlKey) return
      wake()
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
          if (info?.next) navigate(`/play/${info.next.id}?t=0`, { replace: true })
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

  return (
    <div
      ref={container}
      className={chromeShown ? 'player' : 'player chrome-hidden'}
      style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}
      onMouseMove={wake}
      onClick={(e) => e.target === video.current && toggle()}
    >
      <video
        ref={video}
        playsInline
        poster={imageUrl(item?.artwork.backdrop ?? series?.artwork.backdrop, 1280)}
        crossOrigin="anonymous"
        onDoubleClick={toggleFullscreen}
        x-webkit-airplay="allow"
      >
        {selectedText && <track key={selectedText.key} kind="subtitles" src={selectedText.url!} srcLang={selectedText.language ?? 'und'} label={selectedText.key} default />}
      </video>

      {waiting && !error && <div className="player-spinner" role="status" aria-label="Loading" />}

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
        <div className="player-error" role="alert">
          <p>{error}</p>
          <div className="player-error-actions">
            <button className="button" onClick={() => void restartHere({ forceTranscode: true })}>
              Try again
            </button>
            <button className="button quiet" onClick={exit}>
              Go back
            </button>
          </div>
        </div>
      )}

      {skippable && !showUpNext && (
        <button className="skip-button" onClick={() => seekTo(skippable.end)} autoFocus>
          {skippable.type === 'Recap' ? 'Skip recap' : 'Skip intro'}
        </button>
      )}

      {showUpNext && info?.next && (
        <aside className="up-next" aria-live="polite">
          <div className="up-next-still">
            <img src={imageUrl(info.next.artwork.thumb ?? info.next.artwork.backdrop, 500)} alt="" />
            <svg viewBox="0 0 36 36" className="up-next-ring" aria-hidden="true">
              <circle cx="18" cy="18" r="16" style={{ strokeDashoffset: 100 - (countdown / UP_NEXT_SECONDS) * 100 }} pathLength={100} />
            </svg>
          </div>
          <div className="up-next-text">
            <span className="muted">Next episode in {Math.max(0, countdown)}</span>
            <strong>
              {episodeCode(info.next)} {info.next.title}
            </strong>
            <div className="up-next-actions">
              <button className="button small" onClick={() => navigate(`/play/${info.next!.id}?t=0`, { replace: true })}>
                <Play /> Play now
              </button>
              <button className="button quiet small" onClick={() => setUpNextCancelled(true)}>
                Keep watching
              </button>
            </div>
          </div>
        </aside>
      )}

      <footer className="player-controls">
        <Scrubber time={time} duration={duration} bufferedTo={bufferedTo} markers={info?.markers ?? []} onSeek={seekTo} />
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
            <button className="icon-button" onClick={() => navigate(`/play/${info.next!.id}?t=0`, { replace: true })} aria-label="Next episode">
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
                </div>
                {info.audioTracks.length > 1 && (
                  <div className="player-menu-column">
                    <h3>Audio</h3>
                    {info.audioTracks.map((a) => (
                      <MenuItem key={a.index} selected={a.index === info.selectedAudioIndex} onClick={() => chooseAudio(a.index)}>
                        {a.label}
                        <span className="muted"> {[a.codec.toUpperCase(), a.channels && a.channels > 2 ? `${a.channels - 1}.1` : null].filter(Boolean).join(' ')}</span>
                      </MenuItem>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>

          <div className="menu-anchor">
            <button className="player-quality" aria-expanded={menu === 'quality'} onClick={() => setMenu(menu === 'quality' ? null : 'quality')}>
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

function MenuItem({ selected, onClick, children }: { selected: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button role="menuitemradio" aria-checked={selected} className={selected ? 'player-menu-item selected' : 'player-menu-item'} onClick={onClick}>
      {children}
    </button>
  )
}

/** Seek bar with buffered range, intro/credits marks and a time readout while hovering or dragging. */
function Scrubber({
  time,
  duration,
  bufferedTo,
  markers,
  onSeek,
}: {
  time: number
  duration: number
  bufferedTo: number
  markers: PlaybackInfo['markers']
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
          {formatClock(hover)}
        </span>
      )}
    </div>
  )
}
