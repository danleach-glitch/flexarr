import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { Link, useLocation } from 'react-router'
import { imageUrl } from '../api/client'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { formatClock } from '../lib/format'
import { useMusicSettings } from '../lib/musicSettings'
import { coverPalette, fillPalette, glow, parseHex, type Rgb } from '../lib/palette'
import { Aurora } from './Aurora'
import { ChevronDown, Forward30, Next, Pause, Play, Replay15 } from './icons'

/**
 * Now Playing over the whole window: the cover large, the track's name, where it's got to and the controls, with light
 * in the cover's colours moving to the music behind it (unless the visualiser is turned off in Settings). Opened from
 * the music bar's cover; Escape, the close button or following a link puts it away.
 */
export function NowPlayingView({ onClose }: { onClose: () => void }) {
  const player = useAudioPlayer()
  const time = useAudioTime()
  const { visualiser } = useMusicSettings()
  const closeButton = useRef<HTMLButtonElement>(null)
  const location = useLocation()
  const openedAt = useRef(location.pathname)
  const track = player.current
  const poster = track?.artwork.poster
  const [found, setFound] = useState<{ poster: string; colours: Rgb[] } | null>(null)

  useEffect(() => {
    if (!poster) return
    let live = true
    void coverPalette(imageUrl(poster, 185) ?? poster).then((colours) => live && setFound({ poster, colours }))
    return () => {
      live = false
    }
  }, [poster])

  // The cover's colours once read; until then (or with none to read) the track's accent and its neighbours.
  const colours = useMemo(() => {
    const read = found && found.poster === poster ? found.colours : []
    const accent = parseHex(track?.accentColor)
    return fillPalette(read.length > 0 ? read : accent ? [glow(accent)] : [])
  }, [found, poster, track?.accentColor])

  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    closeButton.current?.focus()
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
      opener?.focus()
    }
  }, [onClose])

  // A link followed from here goes to its page, which the view would otherwise cover.
  useEffect(() => {
    if (location.pathname !== openedAt.current) onClose()
  }, [location.pathname, onClose])

  useEffect(() => {
    if (!track) onClose()
  }, [track, onClose])

  if (!track) return null

  const duration = player.duration || track.durationSeconds || 0
  const podcast = track.kind === 'PodcastEpisode'
  const album = track.parentId ? (podcast ? `/podcasts/${track.parentId}` : `/item/${track.parentId}`) : undefined
  const artist = podcast ? album : track.seriesId && !track.trackArtist ? `/item/${track.seriesId}` : undefined
  const titleLink = podcast ? `/podcasts/episode/${track.id}` : album
  const upNext = player.queue[player.index + 1]
  const [r, g, b] = colours[0]
  const style = { '--ambient': track.accentColor ?? `rgb(${r} ${g} ${b})`, '--glow': `rgb(${r} ${g} ${b})` } as CSSProperties

  return (
    <div className="np-view" role="dialog" aria-modal="true" aria-label="Now playing" style={style}>
      <Aurora colours={colours} analyser={player.analyser} playing={player.playing} still={!visualiser} />
      <div className="np-view-veil" aria-hidden="true" />
      <button ref={closeButton} className="icon-button np-view-close" onClick={onClose} aria-label="Close Now Playing">
        <ChevronDown />
      </button>

      <div className="np-view-stage">
        <div className="np-view-cover">{poster && <img src={imageUrl(poster, 780)} alt="" />}</div>

        <div className="np-view-info">
          {titleLink ? (
            <Link to={titleLink} className="np-view-title">
              {track.title}
            </Link>
          ) : (
            <h2 className="np-view-title">{track.title}</h2>
          )}
          {artist ? (
            <Link to={artist} className="np-view-artist">
              {track.seriesTitle}
            </Link>
          ) : (
            <span className="np-view-artist">{track.trackArtist ?? track.seriesTitle}</span>
          )}

          <div className="np-view-seek">
            <input
              type="range"
              min={0}
              max={Math.max(1, Math.floor(duration))}
              value={Math.min(time, Math.floor(duration))}
              onChange={(e) => player.seek(Number(e.target.value))}
              aria-label="Seek"
              style={{ '--progress': `${duration > 0 ? (Math.min(time, duration) / duration) * 100 : 0}%` } as CSSProperties}
            />
            <div className="np-view-times">
              <span>{formatClock(time)}</span>
              <span>-{formatClock(Math.max(0, duration - time))}</span>
            </div>
          </div>

          <div className="np-view-controls">
            {podcast ? (
              <button className="icon-button np-view-skip" onClick={() => player.skipBy(-15)} aria-label="Back 15 seconds">
                <Replay15 />
              </button>
            ) : (
              <button className="icon-button np-view-skip" onClick={player.previous} aria-label="Previous track">
                <Next style={{ transform: 'scaleX(-1)' }} />
              </button>
            )}
            <button className="icon-button np-view-toggle" onClick={player.toggle} aria-label={player.playing ? 'Pause' : 'Play'}>
              {player.playing ? <Pause /> : <Play />}
            </button>
            {podcast ? (
              <button className="icon-button np-view-skip" onClick={() => player.skipBy(30)} aria-label="Forward 30 seconds">
                <Forward30 />
              </button>
            ) : (
              <button className="icon-button np-view-skip" onClick={player.next} aria-label="Next track" disabled={!upNext}>
                <Next />
              </button>
            )}
          </div>

          {upNext && (
            <p className="np-view-next">
              <span>Up next</span> {upNext.title}
              {upNext.trackArtist || upNext.seriesTitle ? ` · ${upNext.trackArtist ?? upNext.seriesTitle}` : ''}
            </p>
          )}
        </div>
      </div>
    </div>
  )
}
