import type Hls from 'hls.js'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { imageUrl, reportProgress, startPlayback, stopPlayback, type ItemSummary } from '../api/client'
import { detectProfile } from '../lib/profile'
import { AudioPlayerContext, AudioTimeContext, type AudioPlayer } from './audioPlayerContext'

/**
 * Music playback that survives navigation: one <audio> element for the whole app, a queue, and
 * Media Session integration so keyboard media keys and the OS "now playing" controls work.
 */
export function AudioPlayerProvider({ children }: { children: ReactNode }) {
  // One audio.current for the app's lifetime, rendered below and only touched from effects and handlers.
  const audio = useRef<HTMLAudioElement>(null)
  const hls = useRef<Hls | null>(null)
  const session = useRef<string | undefined>(undefined)
  const [queue, setQueue] = useState<ItemSummary[]>([])
  const [index, setIndex] = useState(0)
  const [playing, setPlaying] = useState(false)
  const [time, setTime] = useState(0)
  const [duration, setDuration] = useState(0)

  const current = queue[index]

  const load = useCallback(async (track: ItemSummary) => {
    const el = audio.current
    if (!el) return
    hls.current?.destroy()
    hls.current = null
    if (session.current) void stopPlayback(session.current)

    const info = await startPlayback({ itemId: track.id, profile: detectProfile(), startPositionSeconds: 0 })
    session.current = info.sessionId
    setDuration(info.durationSeconds)

    if (info.method === 'Transcode' && !el.canPlayType('application/vnd.apple.mpegurl')) {
      const { default: HlsPlayer } = await import('hls.js/light')
      const player = new HlsPlayer()
      player.loadSource(info.url)
      player.attachMedia(el)
      hls.current = player
    } else {
      el.src = info.url
    }
    await el.play().catch(() => setPlaying(false))
  }, [])

  // Start the current track whenever it changes.
  useEffect(() => {
    if (current) void load(current)
  }, [current, load])

  useEffect(() => {
    const el = audio.current
    if (!el) return
    const onTime = () => setTime(Math.floor(el.currentTime))
    const onPlay = () => setPlaying(true)
    const onPause = () => setPlaying(false)
    const onEnded = () => {
      if (session.current) void reportProgress(session.current, el.currentTime, true, true).catch(() => undefined)
      setIndex((i) => (i + 1 < queue.length ? i + 1 : i))
      if (index + 1 >= queue.length) setPlaying(false)
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
  }, [queue.length, index])

  const play = useCallback((tracks: ItemSummary[], start = 0) => {
    setQueue(tracks)
    setIndex(start)
  }, [])
  const toggle = useCallback(() => {
    const el = audio.current
    if (!el) return
    if (el.paused) void el.play()
    else el.pause()
  }, [])
  const next = useCallback(() => setIndex((i) => Math.min(i + 1, queue.length - 1)), [queue.length])
  const previous = useCallback(() => {
    // Like every music player: back restarts the track unless you're at its very start.
    const el = audio.current
    if (el && el.currentTime > 3) el.currentTime = 0
    else setIndex((i) => Math.max(0, i - 1))
  }, [])
  const seek = useCallback((seconds: number) => {
    if (audio.current) audio.current.currentTime = seconds
  }, [])
  const stop = useCallback(() => {
    audio.current?.pause()
    hls.current?.destroy()
    if (session.current) void stopPlayback(session.current)
    session.current = undefined
    setQueue([])
    setIndex(0)
  }, [])

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
  }, [current, toggle, next, previous])

  const value = useMemo<AudioPlayer>(
    () => ({ queue, index, current, playing, duration, play, toggle, next, previous, seek, stop }),
    [queue, index, current, playing, duration, play, toggle, next, previous, seek, stop],
  )
  return (
    <AudioPlayerContext value={value}>
      <AudioTimeContext value={time}>
        {children}
        <audio ref={audio} hidden />
      </AudioTimeContext>
    </AudioPlayerContext>
  )
}
