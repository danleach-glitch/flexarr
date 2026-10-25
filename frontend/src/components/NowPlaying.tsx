import { useRef, useState, type CSSProperties } from 'react'
import { Link, useLocation } from 'react-router'
import { imageUrl } from '../api/client'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { formatClock } from '../lib/format'
import { Close, Next, Pause, Play, Queue } from './icons'
import { QueuePanel } from './QueuePanel'

/**
 * The music bar along the bottom: there while something is queued, and always on music pages (empty until a track
 * is picked). Cover, title and artist link to their pages; the queue opens in a panel above it.
 */
export function NowPlaying() {
  const player = useAudioPlayer()
  const time = useAudioTime()
  // The panel is open for one page: following a link from the queue (or anywhere) puts it away.
  const [openOn, setOpenOn] = useState<string | null>(null)
  const queueButton = useRef<HTMLButtonElement>(null)
  const location = useLocation()
  const track = player.current
  const queueOpen = openOn === location.pathname
  const setQueueOpen = (open: boolean) => setOpenOn(open ? location.pathname : null)

  if (!track && !player.onMusicPage) return null

  // Until the server has said how long the file is, the library's figure does.
  const duration = player.duration || track?.durationSeconds || 0
  const upcoming = Math.max(0, player.queue.length - player.index - 1)
  const album = track?.parentId ? `/item/${track.parentId}` : undefined
  const artist = track?.seriesId ? `/item/${track.seriesId}` : undefined

  const closeQueue = () => {
    setQueueOpen(false)
    queueButton.current?.focus()
  }

  return (
    <>
      {queueOpen && track && <QueuePanel onClose={closeQueue} />}
      <aside className="now-playing" aria-label="Now playing" style={track?.accentColor ? ({ '--ambient': track.accentColor } as CSSProperties) : undefined}>
        <div className="now-playing-track">
          {album ? (
            <Link to={album} className="now-playing-cover" tabIndex={-1} aria-hidden="true">
              {track?.artwork.poster && <img src={imageUrl(track.artwork.poster, 185)} alt="" />}
            </Link>
          ) : (
            <div className="now-playing-cover" aria-hidden="true">
              {track?.artwork.poster && <img src={imageUrl(track.artwork.poster, 185)} alt="" />}
            </div>
          )}
          <div className="now-playing-text">
            {track ? (
              <>
                {album ? (
                  <Link to={album} className="now-playing-title">
                    {track.title}
                  </Link>
                ) : (
                  <strong className="now-playing-title">{track.title}</strong>
                )}
                {artist ? (
                  <Link to={artist} className="now-playing-artist">
                    {track.seriesTitle}
                  </Link>
                ) : (
                  <span className="now-playing-artist">{track.seriesTitle}</span>
                )}
              </>
            ) : (
              <>
                <strong className="now-playing-title">Nothing playing</strong>
                <span className="now-playing-artist">Pick a track to start</span>
              </>
            )}
          </div>
        </div>
        <div className="now-playing-centre">
          <div className="now-playing-controls">
            <button className="icon-button now-playing-skip" onClick={player.previous} aria-label="Previous track" disabled={!track}>
              <Next style={{ transform: 'scaleX(-1)' }} />
            </button>
            <button className="icon-button now-playing-toggle" onClick={player.toggle} aria-label={player.playing ? 'Pause' : 'Play'} disabled={!track}>
              {player.playing ? <Pause /> : <Play />}
            </button>
            <button className="icon-button now-playing-skip" onClick={player.next} aria-label="Next track" disabled={!track || upcoming === 0}>
              <Next />
            </button>
          </div>
          <div className="now-playing-seek">
            <span>{formatClock(time)}</span>
            <input
              type="range"
              min={0}
              max={Math.max(1, Math.floor(duration))}
              value={Math.min(time, Math.floor(duration))}
              onChange={(e) => player.seek(Number(e.target.value))}
              aria-label="Seek"
              disabled={!track}
            />
            <span>-{formatClock(Math.max(0, duration - time))}</span>
          </div>
        </div>
        <div className="now-playing-tools">
          {track && (
            <>
              <button
                ref={queueButton}
                className="icon-button now-playing-queue"
                aria-label={upcoming === 0 ? 'Queue' : `Queue, ${upcoming} ${upcoming === 1 ? 'track' : 'tracks'} to come`}
                aria-expanded={queueOpen}
                aria-controls="queue-panel"
                onClick={() => (queueOpen ? closeQueue() : setQueueOpen(true))}
              >
                <Queue />
                {upcoming > 0 && <span className="now-playing-count" aria-hidden="true">{upcoming > 99 ? '99+' : upcoming}</span>}
              </button>
              <button
                className="icon-button now-playing-stop"
                onClick={() => {
                  setQueueOpen(false)
                  player.stop()
                }}
                aria-label="Stop and close"
              >
                <Close />
              </button>
            </>
          )}
        </div>
      </aside>
    </>
  )
}
