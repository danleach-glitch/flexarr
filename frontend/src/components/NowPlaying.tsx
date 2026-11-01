import { useCallback, useRef, useState, type CSSProperties } from 'react'
import { Link, useLocation } from 'react-router'
import { imageUrl } from '../api/client'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { useScrub } from '../hooks/useScrub'
import { formatClock } from '../lib/format'
import { authorsLine, chapterIndexAt, isBook } from '../lib/books'
import { BookOffers } from './BookOffers'
import { BookTools } from './BookTools'
import { Close, Fullscreen, Next, Pause, Play, Queue, SkipBack, SkipForward } from './icons'
import { NowPlayingView } from './NowPlayingView'
import { PodcastTools } from './PodcastTools'
import { QueuePanel } from './QueuePanel'


/**
 * The music bar along the bottom: there while something is queued, and always on music pages (empty until a track
 * is picked). The cover opens Now Playing over the whole window; title and artist link to their pages; the queue opens
 * in a panel above it. A podcast episode
 * gets 15 seconds back and 30 on instead of previous and next, a speed and a sleep timer.
 */
export function NowPlaying() {
  const player = useAudioPlayer()
  const time = useAudioTime()
  const scrub = useScrub(time, player.seek)
  // The panel is open for one page: following a link from the queue (or anywhere) puts it away.
  const [openOn, setOpenOn] = useState<string | null>(null)
  const { nowPlayingOpen: viewOpen, setNowPlayingOpen: setViewOpen } = player
  const closeView = useCallback(() => setViewOpen(false), [setViewOpen])
  const queueButton = useRef<HTMLButtonElement>(null)
  const location = useLocation()
  const track = player.current
  const queueOpen = openOn === location.pathname
  const setQueueOpen = (open: boolean) => setOpenOn(open ? location.pathname : null)

  if (!track && !player.onMusicPage) return null

  // Until the server has said how long the file is, the library's figure does.
  const duration = player.duration || track?.durationSeconds || 0
  const upcoming = Math.max(0, player.queue.length - player.index - 1)
  const podcast = track?.kind === 'PodcastEpisode'
  const book = isBook(track)
  const chapters = player.book?.chapters ?? []
  const chapter = chapters[chapterIndexAt(chapters, time)]
  const album = track?.parentId ? (podcast ? `/podcasts/${track.parentId}` : `/item/${track.parentId}`) : undefined
  // A track with an artist of its own (on a compilation, say) names them instead; they have no page to link to.
  const artist = podcast ? album : track?.seriesId && !track.trackArtist ? `/item/${track.seriesId}` : undefined
  const titleLink = book && track ? `/item/${track.id}` : podcast && track ? `/podcasts/episode/${track.id}` : album
  // Skips: a book's go by the profile's steps; a podcast's 15 back and 30 on.
  const skips = podcast || book
  const { back, forward } = player.skipSteps

  const closeQueue = () => {
    setQueueOpen(false)
    queueButton.current?.focus()
  }
  // The track's colour, for the bar and the queue alike.
  const accent = track?.accentColor ? ({ '--ambient': track.accentColor } as CSSProperties) : undefined

  return (
    <>
      {queueOpen && track && <QueuePanel onClose={closeQueue} style={accent} />}
      {viewOpen && track && <NowPlayingView onClose={closeView} />}
      {!viewOpen && <BookOffers />}
      <aside className="now-playing" aria-label="Now playing" style={accent}>
        <div className="now-playing-track">
          {track ? (
            <button
              className="now-playing-cover"
              onClick={() => {
                setQueueOpen(false)
                setViewOpen(true)
              }}
              aria-label="Open Now Playing"
              aria-haspopup="dialog"
            >
              {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 185)} alt="" />}
              <span className="now-playing-expand" aria-hidden="true">
                <Fullscreen />
              </span>
            </button>
          ) : (
            <div className="now-playing-cover" aria-hidden="true" />
          )}
          <div className="now-playing-text">
            {track ? (
              <>
                {titleLink ? (
                  <Link to={titleLink} className="now-playing-title">
                    {track.title}
                  </Link>
                ) : (
                  <strong className="now-playing-title">{track.title}</strong>
                )}
                {player.failure ? (
                  <span className="now-playing-artist now-playing-failure" role="status">
                    {player.failure}
                  </span>
                ) : book ? (
                  <span className="now-playing-artist">{chapter?.title ?? authorsLine(track)}</span>
                ) : artist ? (
                  <Link to={artist} className="now-playing-artist">
                    {track.seriesTitle}
                  </Link>
                ) : (
                  <span className="now-playing-artist">{track.trackArtist ?? track.seriesTitle}</span>
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
            {book && (
              <button className="icon-button now-playing-skip now-playing-chapter" onClick={player.previous} aria-label="Previous chapter" disabled={chapters.length === 0}>
                <Next style={{ transform: 'scaleX(-1)' }} />
              </button>
            )}
            {skips ? (
              <button className="icon-button now-playing-skip" onClick={() => player.skipBy(-back)} aria-label={`Back ${back} seconds`}>
                <SkipBack seconds={back} />
              </button>
            ) : (
              <button className="icon-button now-playing-skip" onClick={player.previous} aria-label="Previous track" disabled={!track}>
                <Next style={{ transform: 'scaleX(-1)' }} />
              </button>
            )}
            <button className="icon-button now-playing-toggle" onClick={player.toggle} aria-label={player.playing ? 'Pause' : 'Play'} disabled={!track}>
              {player.playing ? <Pause /> : <Play />}
            </button>
            {skips ? (
              <button className="icon-button now-playing-skip" onClick={() => player.skipBy(forward)} aria-label={`Forward ${forward} seconds`}>
                <SkipForward seconds={forward} />
              </button>
            ) : (
              <button className="icon-button now-playing-skip" onClick={player.next} aria-label="Next track" disabled={!track || upcoming === 0}>
                <Next />
              </button>
            )}
            {book && (
              <button className="icon-button now-playing-skip now-playing-chapter" onClick={player.next} aria-label="Next chapter" disabled={chapters.length === 0}>
                <Next />
              </button>
            )}
          </div>
          <div className="now-playing-seek">
            <span>{formatClock(scrub.shown)}</span>
            <input
              type="range"
              min={0}
              max={Math.max(1, Math.floor(duration))}
              value={Math.min(scrub.shown, Math.floor(duration))}
              {...scrub.inputProps}
              aria-label="Seek"
              disabled={!track}
            />
            {/* Time left, at the speed it plays at. */}
            <span>-{formatClock(Math.max(0, duration - scrub.shown) / (skips ? player.speed || 1 : 1))}</span>
          </div>
        </div>
        <div className="now-playing-tools">
          {track && (
            <>
              {podcast && <PodcastTools player={player} />}
              {book && <BookTools player={player} />}
              {!book && (
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
              )}
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
