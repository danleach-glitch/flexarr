import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { getRadio, getTracks, imageUrl, imageWidths, type ItemSummary } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { usePlay } from '../hooks/usePlay'
import { episodeHeading, formatDuration, year } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { hasScores, scoresOf } from '../lib/scores'
import { shuffled } from '../lib/shuffle'
import { Info, Play, Radio } from './icons'
import { ScoreChips } from './ScoreChips'

const ROTATE_MS = 10_000
const BACKDROP_WIDTHS = [780, 1280, 1920]

/** Facts as separate quiet items (no middle-dot strings). */
export function Facts({ item, extra = [] }: { item: ItemSummary; extra?: (string | null | undefined)[] }) {
  const facts = [
    year(item)?.toString(),
    item.kind === 'Series' ? (item.childCount === 1 ? '1 season' : `${item.childCount} seasons`) : formatDuration(item.durationSeconds ?? (item.runtimeMinutes ?? 0) * 60),
    ...extra,
  ].filter(Boolean)
  return (
    <p className="facts">
      {item.contentRating && <span className="tag">{item.contentRating}</span>}
      {facts.map((f) => (
        <span key={f}>{f}</span>
      ))}
      <Scores item={item} />
    </p>
  )
}

/**
 * IMDb and Rotten Tomatoes beside their marks. Without an IMDb score the plain one stays (usually IMDb's, or a show's
 * TVDB score) before the tomato. Hidden for kids (app.css).
 */
function Scores({ item }: { item: ItemSummary }) {
  const scores = scoresOf(item)
  return (
    <>
      {scores.imdb === null && item.communityRating ? <PlainScore value={item.communityRating} /> : null}
      {hasScores(scores) && <ScoreChips scores={scores} />}
    </>
  )
}

/** A score out of 10 from no particular source: "7.4/10". */
export function PlainScore({ value }: { value: number }) {
  return (
    <span className="score">
      <span className="visually-hidden">Rated </span>
      {value.toFixed(1)}
      <span className="muted">/10</span>
    </span>
  )
}

/**
 * `resumes` is what each part-played title picks up from, by its id: the episode for a show you're partway through.
 * `resume` is the first title's alone, from servers that don't send `resumes`.
 */
export function Hero({ items, overviews, resume, resumes, top }: {
  items: ItemSummary[]
  overviews?: Record<string, string | null>
  resume?: ItemSummary
  resumes?: Record<string, ItemSummary> | null
  top?: ReactNode
}) {
  // The one fading out is kept until the next change, so the cross-fade has something to fade from.
  const [{ index, previous }, setShown] = useState({ index: 0, previous: -1 })
  const setIndex = (next: number) => setShown((s) => (s.index === next ? s : { index: next, previous: s.index }))
  const [paused, setPaused] = useState(false)
  const play = usePlay()
  const navigate = useNavigate()
  const player = useAudioPlayer()
  const item = items[index]
  const resuming = resumes ? resumes[item?.id ?? ''] : index === 0 ? resume : undefined
  const episode = resuming?.kind === 'Episode' ? episodeHeading(resuming) : undefined

  useEffect(() => {
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    if (items.length < 2 || paused || reduced) return
    const timer = setInterval(() => setShown((s) => ({ index: (s.index + 1) % items.length, previous: s.index })), ROTATE_MS)
    return () => clearInterval(timer)
  }, [items.length, paused])

  if (!item) return null
  const music = item.kind === 'Album' || item.kind === 'Artist'
  // A music library's hero: an album carries on from its resume track (Play from the start otherwise); an artist
  // plays shuffled, or as radio.
  const playMusic = async (radio = false) => {
    const tracks = radio ? await getRadio(item.id) : await getTracks(item.id)
    if (tracks.length === 0) return
    if (item.kind === 'Artist' && !radio) return player.play(shuffled(tracks), 0, { openNowPlaying: true })
    const start = resuming ? Math.max(0, tracks.findIndex((t) => t.id === resuming.id)) : 0
    player.play(tracks, start, { openNowPlaying: true })
  }
  const style = item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined

  return (
    <section
      className="hero"
      style={style}
      aria-roledescription="carousel"
      aria-label="Featured"
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
      {top && <div className="hero-top">{top}</div>}
      <div className="hero-art" aria-hidden="true">
        {/* Only the backdrops on screen or next up are loaded, sized for the screen; the first one comes first. */}
        {items.map((it, i) =>
          i === index || i === previous || i === (index + 1) % items.length ? (
            <img
              key={it.id}
              src={imageUrl(it.artwork.backdrop, 1920)}
              srcSet={imageWidths(it.artwork.backdrop, BACKDROP_WIDTHS)}
              sizes="100vw"
              fetchPriority={i === 0 ? 'high' : 'low'}
              alt=""
              className={i === index ? 'shown' : undefined}
            />
          ) : null,
        )}
      </div>
      <div className="hero-content" aria-live="polite">
        {item.kind === 'Album' ? (
          // An album's slide: its artist above it, since the backdrop is theirs.
          <>
            {item.seriesTitle && <p className="hero-eyebrow">{item.seriesTitle}</p>}
            <h1 className="hero-title">{item.title}</h1>
          </>
        ) : item.artwork.logo ? (
          <h1 className="hero-logo">
            <img src={imageUrl(item.artwork.logo, 780)} alt={item.title} />
          </h1>
        ) : (
          <h1 className="hero-title">{item.title}</h1>
        )}
        {music ? (
          <p className="facts">
            {item.kind === 'Album' && item.year && <span>{item.year}</span>}
            {item.childCount > 0 && <span>{item.childCount} {item.kind === 'Album' ? (item.childCount === 1 ? 'track' : 'tracks') : item.childCount === 1 ? 'album' : 'albums'}</span>}
          </p>
        ) : (
          <Facts item={item} />
        )}
        {resuming && episode && (
          <p className="hero-resume">
            {episode} {resuming.title}
          </p>
        )}
        {overviews?.[item.id] && <p className="hero-overview">{overviews[item.id]}</p>}
        <div className="hero-actions">
          <AsyncButton className="button" onClick={() => (music ? playMusic() : resuming ? navigate(`/play/${resuming.id}`) : play(item))}>
            <Play /> {resuming ? (episode ? `Resume ${episode}` : 'Resume') : 'Play'}
          </AsyncButton>
          {item.kind === 'Artist' && (
            <AsyncButton className="button quiet" onClick={() => playMusic(true)}>
              <Radio /> Radio
            </AsyncButton>
          )}
          <Link className="button quiet" to={`/item/${item.id}`}>
            <Info /> More info
          </Link>
        </div>
      </div>
      {items.length > 1 && (
        <div className="hero-pager" role="tablist" aria-label="Featured titles">
          {items.map((it, i) => (
            <button key={it.id} role="tab" aria-selected={i === index} aria-label={it.title} onClick={() => setIndex(i)}>
              <span />
            </button>
          ))}
        </div>
      )}
    </section>
  )
}
