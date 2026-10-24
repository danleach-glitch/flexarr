import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { imageUrl, type ItemSummary } from '../api/client'
import { usePlay } from '../hooks/usePlay'
import { episodeHeading, formatDuration, year } from '../lib/format'
import { Info, Play } from './icons'

const ROTATE_MS = 10_000

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
      {item.communityRating && (
        <span>
          <span className="visually-hidden">Rated </span>
          {item.communityRating.toFixed(1)}
          <span className="muted">/10</span>
        </span>
      )}
      {item.criticRating !== null && item.criticRating !== undefined && <span>{item.criticRating}% critics</span>}
    </p>
  )
}

/** `resume`, when given, is what the first title picks up from: the episode for a show you're partway through. */
export function Hero({ items, overviews, resume, top }: { items: ItemSummary[]; overviews?: Record<string, string | null>; resume?: ItemSummary; top?: ReactNode }) {
  const [index, setIndex] = useState(0)
  const [paused, setPaused] = useState(false)
  const play = usePlay()
  const navigate = useNavigate()
  const item = items[index]
  const resuming = index === 0 ? resume : undefined
  const episode = resuming?.kind === 'Episode' ? episodeHeading(resuming) : undefined

  useEffect(() => {
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
    if (items.length < 2 || paused || reduced) return
    const timer = setInterval(() => setIndex((i) => (i + 1) % items.length), ROTATE_MS)
    return () => clearInterval(timer)
  }, [items.length, paused])

  if (!item) return null
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
        {items.map((it, i) => (
          <img key={it.id} src={imageUrl(it.artwork.backdrop, 1920)} alt="" className={i === index ? 'shown' : undefined} />
        ))}
      </div>
      <div className="hero-content" aria-live="polite">
        {item.artwork.logo ? (
          <h1 className="hero-logo">
            <img src={imageUrl(item.artwork.logo, 780)} alt={item.title} />
          </h1>
        ) : (
          <h1 className="hero-title">{item.title}</h1>
        )}
        <Facts item={item} />
        {resuming && episode && (
          <p className="hero-resume">
            {episode} {resuming.title}
          </p>
        )}
        {overviews?.[item.id] && <p className="hero-overview">{overviews[item.id]}</p>}
        <div className="hero-actions">
          <button className="button" onClick={() => (resuming ? navigate(`/play/${resuming.id}`) : play(item))}>
            <Play /> {resuming ? (episode ? `Resume ${episode}` : 'Resume') : 'Play'}
          </button>
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
