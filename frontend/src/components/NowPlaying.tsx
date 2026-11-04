import { useCallback, useRef, useState, type CSSProperties } from 'react'
import { Link, useLocation } from 'react-router'
import { imageUrl } from '../api/client'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { useScrub } from '../hooks/useScrub'
import { formatClock } from '../lib/format'
import { authorsLine, chapterIndexAt, isBook } from '../lib/books'
import { BookOffers } from './BookOffers'
import { BookTools } from './BookTools'
import { Close, Fullscreen, Next, Queue, SkipBack, SkipForward } from './icons'
import { icon } from './icons.generated'
import { NowPlayingView } from './NowPlayingView'
import { PodcastTools } from './PodcastTools'
import { QueuePanel } from './QueuePanel'
import { t } from '../i18n'

/**
 * The music bar along the bottom, there while something is queued. The cover opens Now Playing over the whole window;
 * title and artist link to their pages; the queue opens in a panel above it. A podcast episode gets 15 seconds back and
 * 30 on instead of previous and next, a speed and a sleep timer.
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

  if (!track) return null

  // Until the server has said how long the file is, the library's figure does.
  const duration = player.duration || track.durationSeconds || 0
  const upcoming = Math.max(0, player.queue.length - player.index - 1)
  const podcast = track.kind === 'PodcastEpisode'
  const book = isBook(track)
  const chapters = player.book?.chapters ?? []
  const chapter = chapters[chapterIndexAt(chapters, time)]
  const album = track.parentId ? (podcast ? `/podcasts/${track.parentId}` : `/item/${track.parentId}`) : undefined
  // A track with an artist of its own (on a compilation, say) names them instead; they have no page to link to.
  const artist = podcast ? album : track.seriesId && !track.trackArtist ? `/item/${track.seriesId}` : undefined
  const titleLink = book ? `/item/${track.id}` : podcast ? `/podcasts/episode/${track.id}` : album
  // Skips: a book's go by the profile's steps; a podcast's 15 back and 30 on.
  const skips = podcast || book
  const { back, forward } = player.skipSteps

  const closeQueue = () => {
    setQueueOpen(false)
    queueButton.current?.focus()
  }
  // The track's colour, for the bar and the queue alike.
  const accent = track.accentColor ? ({ '--ambient': track.accentColor } as CSSProperties) : undefined

  return (
    <>
      {queueOpen && <QueuePanel onClose={closeQueue} style={accent} />}
      {viewOpen && <NowPlayingView onClose={closeView} />}
      {!viewOpen && <BookOffers />}
      <aside className="now-playing" aria-label={t('Now playing')} style={accent}>
        <div className="now-playing-track">
          <button
            className="now-playing-cover"
            onClick={() => {
              setQueueOpen(false)
              setViewOpen(true)
            }}
            aria-label={t('Open Now Playing')}
            aria-haspopup="dialog"
          >
            {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 185)} alt="" />}
            <span className="now-playing-expand" aria-hidden="true">
              <Fullscreen />
            </span>
          </button>
          <div className="now-playing-text">
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
          </div>
        </div>
        <div className="now-playing-centre">
          <div className="now-playing-controls">
            {book && (
              <button className="icon-button now-playing-skip now-playing-chapter" onClick={player.previous} aria-label={t('Previous chapter')} disabled={chapters.length === 0}>
                <Next style={{ transform: 'scaleX(-1)' }} />
              </button>
            )}
            {skips ? (
              <button className="icon-button now-playing-skip" onClick={() => player.skipBy(-back)} aria-label={t('Back {seconds} seconds', { seconds: back })}>
                <SkipBack seconds={back} />
              </button>
            ) : (
              <button className="icon-button now-playing-skip" onClick={player.previous} aria-label={t('Previous track')}>
                <Next style={{ transform: 'scaleX(-1)' }} />
              </button>
            )}
            <button className="icon-button now-playing-toggle" onClick={player.toggle} aria-label={player.playing ? t('Pause') : t('Play')}>
              {player.playing ? <icon.pause /> : <icon.play />}
            </button>
            {skips ? (
              <button className="icon-button now-playing-skip" onClick={() => player.skipBy(forward)} aria-label={t('Forward {seconds} seconds', { seconds: forward })}>
                <SkipForward seconds={forward} />
              </button>
            ) : (
              <button className="icon-button now-playing-skip" onClick={player.next} aria-label={t('Next track')} disabled={upcoming === 0}>
                <Next />
              </button>
            )}
            {book && (
              <button className="icon-button now-playing-skip now-playing-chapter" onClick={player.next} aria-label={t('Next chapter')} disabled={chapters.length === 0}>
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
              aria-label={t('Seek')}
            />
            {/* Time left, at the speed it plays at. */}
            <span>-{formatClock(Math.max(0, duration - scrub.shown) / (skips ? player.speed || 1 : 1))}</span>
          </div>
        </div>
        <div className="now-playing-tools">
          {podcast && <PodcastTools player={player} />}
          {book && <BookTools player={player} />}
          {!book && (
            <button
              ref={queueButton}
              className="icon-button now-playing-queue"
              aria-label={upcoming === 0 ? t('Queue') : t('{count, plural, one {Queue, # track to come} other {Queue, # tracks to come}}', { count: upcoming })}
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
            aria-label={t('Stop and close')}
          >
            <Close />
          </button>
        </div>
      </aside>
    </>
  )
}
