import { useEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { getPodcast, getPodcastChapters, getPodcastEpisode, imageUrl, setWatched } from '../api/client'
import type { Podcast, PodcastChapter, PodcastEpisode } from '../api/types'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { useScrub } from '../hooks/useScrub'
import { formatClock, formatDuration } from '../lib/format'
import { useMusicSettings } from '../lib/musicSettings'
import { coverPalette, fillPalette, glow, parseHex, type Rgb } from '../lib/palette'
import { chapterAt, episodeDate, previousChapterStart } from '../lib/podcasts'
import { Aurora } from './Aurora'
import { EpisodeNotes } from './EpisodeNotes'
import { ChevronDown, ChevronLeft, ChevronRight, Forward30, More, Next, Notes, Pause, Play, Replay15 } from './icons'
import { Menu, type MenuPoint } from './ItemMenu'
import { PodcastTools } from './PodcastTools'

/**
 * Now Playing over the whole window: the cover large, the track's name, where it's got to and the controls, with light
 * in the cover's colours moving to the music behind it (unless the visualiser is turned off in Settings). Opened from
 * the music bar's cover; Escape, the close button or following a link puts it away.
 *
 * A podcast episode adds its chapter (with the ones either side a click away, and a mark on the scrubber where each
 * starts), when it'll end, its speed and sleep timer, and a menu. Its Notes button swaps the cover for the episode's
 * chapters and notes, whose times jump there.
 */
export function NowPlayingView({ onClose }: { onClose: () => void }) {
  const player = useAudioPlayer()
  const time = useAudioTime()
  const scrub = useScrub(time, player.seek)
  const { visualiser } = useMusicSettings()
  const closeButton = useRef<HTMLButtonElement>(null)
  const location = useLocation()
  const openedAt = useRef(location.pathname)
  const track = player.current
  const poster = track?.artwork.poster
  const [found, setFound] = useState<{ poster: string; colours: Rgb[] } | null>(null)
  const podcast = track?.kind === 'PodcastEpisode'
  const details = usePlayingEpisode(podcast ? track?.id : undefined)
  const chapters = details?.chapters ?? []
  const [showingNotes, setShowingNotes] = useState(false)
  const [menuAt, setMenuAt] = useState<MenuPoint | null>(null)
  const navigate = useNavigate()

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
      // Escape in a menu closes the menu, not the view.
      if (e.key === 'Escape' && !e.defaultPrevented) onClose()
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
  const chapter = chapterAt(chapters, time)
  const toggleMenu = (e: MouseEvent<HTMLButtonElement>) => {
    const r = e.currentTarget.getBoundingClientRect()
    setMenuAt(menuAt ? null : { x: r.right - 200, y: r.bottom + 6 })
  }
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
        {podcast && showingNotes ? (
          <EpisodeDetails episode={details?.episode ?? null} chapters={chapters} current={chapter} playing={player.playing} onSeek={player.seek} />
        ) : (
          <div className="np-view-cover">{poster && <img src={imageUrl(poster, 780)} alt="" />}</div>
        )}

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

          {chapters.length > 0 && (
            <div className="np-view-chapter">
              <button className="icon-button" onClick={() => player.seek(previousChapterStart(chapters, time))} aria-label="Previous chapter">
                <ChevronLeft />
              </button>
              <button className="np-view-chapter-name" onClick={() => setShowingNotes(true)} title="Show the chapters">
                {chapters[chapter ?? 0].title}
              </button>
              <button
                className="icon-button"
                onClick={() => player.seek(chapters[(chapter ?? -1) + 1].startSeconds)}
                disabled={(chapter ?? -1) + 1 >= chapters.length}
                aria-label="Next chapter"
              >
                <ChevronRight />
              </button>
            </div>
          )}

          <div className="np-view-seek">
            {duration > 0 && chapters.length > 0 && (
              <div className="np-view-marks" aria-hidden="true">
                {chapters
                  .filter((c) => c.startSeconds > 0 && c.startSeconds < duration)
                  .map((c) => (
                    <span key={c.startSeconds} style={{ left: `${(c.startSeconds / duration) * 100}%` }} />
                  ))}
              </div>
            )}
            <input
              type="range"
              min={0}
              max={Math.max(1, Math.floor(duration))}
              value={Math.min(scrub.shown, Math.floor(duration))}
              {...scrub.inputProps}
              aria-label="Seek"
              className={scrub.scrubbing ? 'scrubbing' : undefined}
              style={{ '--progress': `${duration > 0 ? (Math.min(scrub.shown, duration) / duration) * 100 : 0}%` } as CSSProperties}
            />
            <div className="np-view-times">
              <span>{formatClock(scrub.shown)}</span>
              {podcast && duration > 0 && <EndsAt remaining={Math.max(0, duration - scrub.shown) / player.speed} />}
              <span>-{formatClock(Math.max(0, duration - scrub.shown))}</span>
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

          {podcast && (
            <div className="np-view-tools">
              <PodcastTools player={player} />
              <button
                className={showingNotes ? 'icon-button np-view-tool active' : 'icon-button np-view-tool'}
                onClick={() => setShowingNotes(!showingNotes)}
                aria-pressed={showingNotes}
                aria-label="Notes"
                title={showingNotes ? 'Show the cover' : 'Chapters and notes'}
              >
                <Notes />
              </button>
              <button className="icon-button np-view-tool" onClick={toggleMenu} aria-label="More" aria-haspopup="menu" aria-expanded={menuAt !== null}>
                <More />
              </button>
              {menuAt && (
                <Menu
                  label={track.title}
                  at={menuAt}
                  actions={[
                    ...(track.parentId ? [{ label: 'Go to podcast', run: () => navigate(`/podcasts/${track.parentId}`) }] : []),
                    {
                      label: 'Mark as played',
                      run: async () => {
                        await setWatched(track.id, true)
                        // Played: on to what comes next, as if it had finished.
                        player.seek(Math.max(0, duration - 0.5))
                      },
                    },
                    ...(details?.podcast?.link ? [{ label: 'Share podcast', run: () => sharePodcast(details.podcast!) }] : []),
                  ]}
                  onClose={() => setMenuAt(null)}
                />
              )}
            </div>
          )}

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

/** When it'll finish ("Ends 11:19"), at the speed it's playing; the clock it counts from moves on every few seconds. */
function EndsAt({ remaining }: { remaining: number }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 5000)
    return () => window.clearInterval(timer)
  }, [])
  return <span>Ends {new Date(now + remaining * 1000).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}</span>
}

/** The episode playing's whole notes, its chapters and its show, once they've come. */
function usePlayingEpisode(id: string | undefined) {
  const [found, setFound] = useState<{ id: string; episode: PodcastEpisode | null; chapters: PodcastChapter[]; podcast: Podcast | null } | null>(null)

  useEffect(() => {
    if (!id) return
    const abort = new AbortController()
    void (async () => {
      const [episode, chapters] = await Promise.all([
        getPodcastEpisode(id, abort.signal).catch(() => null),
        getPodcastChapters(id, abort.signal).catch(() => []),
      ])
      const podcast = episode ? await getPodcast(episode.podcastId, abort.signal).catch(() => null) : null
      if (!abort.signal.aborted) setFound({ id, episode, chapters, podcast })
    })()
    return () => abort.abort()
  }, [id])

  return found && found.id === id ? found : null
}

/** Shares the show's own page (or, where the browser can't share, copies its address). */
function sharePodcast(podcast: Podcast) {
  if (!podcast.link) return
  if (navigator.share) void navigator.share({ title: podcast.title, url: podcast.link }).catch(() => undefined)
  else void navigator.clipboard?.writeText(podcast.link)
}

/** The episode's chapters (clicking one goes there) and notes, in the cover's place. */
function EpisodeDetails({
  episode,
  chapters,
  current,
  playing,
  onSeek,
}: {
  episode: PodcastEpisode | null
  chapters: PodcastChapter[]
  current: number | null
  playing: boolean
  onSeek: (seconds: number) => void
}) {
  const list = useRef<HTMLOListElement>(null)

  // Opened part way through, the chapter playing is in view.
  useEffect(() => {
    list.current?.querySelector('[aria-current="true"]')?.scrollIntoView({ block: 'center' })
  }, [])

  return (
    <div className="np-view-details">
      {chapters.length > 0 && (
        <section>
          <h3>Chapters</h3>
          <ol ref={list} className="np-view-chapters">
            {chapters.map((c, i) => (
              <li key={c.startSeconds}>
                <button
                  className={i === current ? 'current' : current !== null && i < current ? 'heard' : undefined}
                  aria-current={i === current}
                  onClick={() => onSeek(c.startSeconds)}
                >
                  <span className="np-view-chapter-time">{formatClock(c.startSeconds)}</span>
                  <span className="np-view-chapter-title">{c.title}</span>
                  {i === current && <span className={playing ? 'np-view-chapter-live playing' : 'np-view-chapter-live'} aria-hidden="true" />}
                </button>
              </li>
            ))}
          </ol>
        </section>
      )}
      <section>
        <h3>Episode notes</h3>
        {episode ? (
          <>
            <p className="np-view-facts">
              {[episodeDate(episode.publishedAt), formatDuration(episode.durationSeconds ?? 0), episode.explicit ? 'Explicit' : null].filter(Boolean).join(' · ')}
            </p>
            {episode.overview ? (
              <EpisodeNotes text={episode.overview} duration={episode.durationSeconds} onSeek={onSeek} />
            ) : (
              <p className="np-view-facts">This episode has no notes.</p>
            )}
          </>
        ) : (
          <p className="np-view-facts">Loading…</p>
        )}
      </section>
    </div>
  )
}
