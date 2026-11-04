import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { getRadio, getTracks, imageUrl, imageWidths, type ItemSummary } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { usePlay } from '../hooks/usePlay'
import { episodeHeading, formatDuration, year } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { hasScores, scoresOf } from '../lib/scores'
import { shuffled } from '../lib/shuffle'
import { Info } from './icons'
import { icon } from './icons.generated'
import { ScoreChips } from './ScoreChips'
import { t } from '../i18n'

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
      <span className="visually-hidden">{t('Rated')} </span>
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
  const book = item.kind === 'Audiobook'
  const style = item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined

  return (
    <section
      className={book ? 'hero hero-book' : 'hero'}
      style={style}
      aria-roledescription="carousel"
      aria-label={t('Featured')}
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
            it.kind === 'Audiobook' ? (
              // Books have no backdrops: the cover, blurred into a wash of its colours.
              <img
                key={it.id}
                src={imageUrl(it.artwork.poster, 300)}
                fetchPriority={i === 0 ? 'high' : 'low'}
                alt=""
                className={['blurred', i === index ? 'shown' : ''].filter(Boolean).join(' ')}
              />
            ) : (
              <img
                key={it.id}
                src={imageUrl(it.artwork.backdrop, 1920)}
                srcSet={imageWidths(it.artwork.backdrop, BACKDROP_WIDTHS)}
                sizes="100vw"
                fetchPriority={i === 0 ? 'high' : 'low'}
                alt=""
                className={i === index ? 'shown' : undefined}
              />
            )
          ) : null,
        )}
      </div>
      {book && <img className="hero-cover" src={imageUrl(item.artwork.poster, 600)} alt="" aria-hidden="true" />}
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
        {book ? (
          <p className="facts">
            {item.book?.authors && <span>{item.book.authors}</span>}
            {item.book?.narrator && <span>Read by {item.book.narrator}</span>}
            {formatDuration(item.book?.durationSeconds ?? item.durationSeconds) && <span>{formatDuration(item.book?.durationSeconds ?? item.durationSeconds)}</span>}
          </p>
        ) : music ? (
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
          {book ? (
            <BookPlay item={item} />
          ) : music ? (
            <MusicActions item={item} resuming={resuming} />
          ) : (
            <AsyncButton className="button" onClick={() => (resuming ? navigate(`/play/${resuming.id}`) : play(item))}>
              <icon.play /> {resuming ? (episode ? `Resume ${episode}` : 'Resume') : 'Play'}
            </AsyncButton>
          )}
          <Link className="button quiet" to={`/item/${item.id}`}>
            <Info /> {t('More info')}
          </Link>
        </div>
      </div>
      {items.length > 1 && (
        <div className="hero-pager" role="tablist" aria-label={t('Featured titles')}>
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

/**
 * A music library's hero buttons: an album carries on from its resume track (Play from the start otherwise); an
 * artist plays shuffled, or as radio. Their own component, so only they follow the player.
 */
function MusicActions({ item, resuming }: { item: ItemSummary; resuming: ItemSummary | undefined }) {
  const player = useAudioPlayer()
  const playMusic = async (radio = false) => {
    const tracks = radio ? await getRadio(item.id) : await getTracks(item.id)
    if (tracks.length === 0) return
    if (item.kind === 'Artist' && !radio) return player.play(shuffled(tracks), 0, { openNowPlaying: true })
    const found = resuming ? tracks.findIndex((t) => t.id === resuming.id) : -1
    // Part way in only on the track it belongs to (a track left half way); otherwise from the start of it.
    const at = found >= 0 && resuming && !resuming.userData.watched ? resuming.userData.positionSeconds : 0
    player.play(tracks, Math.max(0, found), { openNowPlaying: true, at })
  }
  return (
    <>
      <AsyncButton className="button" onClick={() => playMusic()}>
        <icon.play /> {resuming ? 'Resume' : 'Play'}
      </AsyncButton>
      {item.kind === 'Artist' && (
        <AsyncButton className="button quiet" onClick={() => playMusic(true)}>
          <icon.radio /> {t('Radio')}
        </AsyncButton>
      )}
    </>
  )
}

/** A book's hero button: Resume part way through, otherwise Play. In the audio player, as its own page plays it. */
function BookPlay({ item }: { item: ItemSummary }) {
  const player = useAudioPlayer()
  const resumable = item.userData.positionSeconds > 0 && !item.userData.watched
  return (
    <AsyncButton className="button" onClick={() => player.play([item])}>
      <icon.play /> {resumable ? 'Resume' : 'Play'}
    </AsyncButton>
  )
}
