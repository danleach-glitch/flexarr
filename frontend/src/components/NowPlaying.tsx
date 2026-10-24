import type { CSSProperties } from 'react'
import { Link } from 'react-router'
import { imageUrl } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock } from '../lib/format'
import { Close, Next, Pause, Play } from './icons'

/** The music bar along the bottom while something is queued. */
export function NowPlaying() {
  const player = useAudioPlayer()
  const track = player.current
  if (!track) return null

  return (
    <aside className="now-playing" aria-label="Now playing" style={track.accentColor ? ({ '--ambient': track.accentColor } as CSSProperties) : undefined}>
      <Link to={track.parentId ? `/item/${track.parentId}` : '/'} className="now-playing-cover" aria-label="Open album">
        {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 185)} alt="" />}
      </Link>
      <div className="now-playing-text">
        <strong>{track.title}</strong>
        <span className="muted">{track.seriesTitle}</span>
      </div>
      <div className="now-playing-controls">
        <button className="icon-button" onClick={player.previous} aria-label="Previous track">
          <Next style={{ transform: 'scaleX(-1)' }} />
        </button>
        <button className="icon-button now-playing-toggle" onClick={player.toggle} aria-label={player.playing ? 'Pause' : 'Play'}>
          {player.playing ? <Pause /> : <Play />}
        </button>
        <button className="icon-button" onClick={player.next} aria-label="Next track" disabled={player.index >= player.queue.length - 1}>
          <Next />
        </button>
      </div>
      <div className="now-playing-seek">
        <span>{formatClock(player.time)}</span>
        <input
          type="range"
          min={0}
          max={Math.max(1, Math.floor(player.duration))}
          value={Math.floor(player.time)}
          onChange={(e) => player.seek(Number(e.target.value))}
          aria-label="Seek"
        />
        <span>{formatClock(player.duration)}</span>
      </div>
      <button className="icon-button" onClick={player.stop} aria-label="Stop and close">
        <Close />
      </button>
    </aside>
  )
}
