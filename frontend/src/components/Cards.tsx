import { useState, type CSSProperties, type ReactNode } from 'react'
import { Link } from 'react-router'
import type { ItemSummary } from '../api/types'
import { episodeCode, progressOf, year } from '../lib/format'
import { itemLink } from '../lib/links'
import { usePlay } from '../hooks/usePlay'
import { Artwork } from './Artwork'
import { Check, More } from './icons'
import { hasMenu, ItemMenu, type MenuPoint } from './ItemMenu'
import { WatchedToggle } from './WatchedToggle'

function Progress({ item }: { item: ItemSummary }) {
  const progress = progressOf(item)
  if (progress === null) return null
  return (
    <span className="progress" aria-label={`${Math.round(progress * 100)}% watched`}>
      <span style={{ width: `${progress * 100}%` }} />
    </span>
  )
}

const toggles = (item: ItemSummary) => item.kind === 'Movie' || item.kind === 'Episode'

/** Watched state for shows and seasons (films and episodes get a WatchedToggle instead). */
function WatchState({ item }: { item: ItemSummary }) {
  if (toggles(item)) return null
  if (item.userData.watched) {
    return (
      <span className="watched-mark" aria-label="Watched">
        <Check />
      </span>
    )
  }
  if (item.userData.unwatchedCount && item.kind !== 'Movie') {
    return <span className="unwatched-count">{item.userData.unwatchedCount}</span>
  }
  return null
}

function subtitle(item: ItemSummary): string | null {
  switch (item.kind) {
    case 'Series':
      return item.childCount === 1 ? '1 season' : `${item.childCount} seasons`
    case 'Season':
      return item.childCount === 1 ? '1 episode' : `${item.childCount} episodes`
    case 'Episode':
      return episodeCode(item)
    case 'Artist':
      return item.childCount === 1 ? '1 album' : `${item.childCount} albums`
    case 'Album':
      return [item.seriesTitle, item.year].filter(Boolean).join(', ')
    default:
      return year(item)?.toString() ?? null
  }
}

/**
 * A card: its artwork opens (or plays) the title, the text beneath links to each part of it, like Plex (an episode's
 * show, season and own page), and the ⋯ button or a right-click offers play and watched actions.
 */
/**
 * `playLabel` is for artwork that plays rather than opens; when the artwork opens the same page as the title link,
 * it's left out of the tab order and hidden from screen readers so each card has one link to its page.
 */
function CardShell({ item, to, className, art, playLabel, onOpen, children }: { item: ItemSummary; to: string; className: string; art: ReactNode; playLabel?: string; onOpen?: () => void; children?: ReactNode }) {
  const [menuAt, setMenuAt] = useState<MenuPoint | null>(null)
  const style = item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined
  const menu = hasMenu(item)
  return (
    <div
      className={`card card-shell ${className}`}
      style={style}
      onContextMenu={
        menu
          ? (e) => {
              e.preventDefault()
              setMenuAt({ x: e.clientX, y: e.clientY })
            }
          : undefined
      }
    >
      <Link
        to={to}
        className="card-art"
        onClick={
          onOpen
            ? (e) => {
                e.preventDefault()
                onOpen()
              }
            : undefined
        }
        {...(playLabel ? { 'aria-label': playLabel } : children ? { 'aria-hidden': true, tabIndex: -1 } : { 'aria-label': cardLabel(item) })}
      >
        {art}
      </Link>
      {children}
      {toggles(item) && !className.includes('top10') && <WatchedToggle key={`${item.id}-${item.userData.watched}`} item={item} />}
      {menu && (
        <button
          className="card-menu-button"
          aria-label={`More for ${cardLabel(item)}`}
          aria-haspopup="menu"
          aria-expanded={menuAt !== null}
          onClick={(e) => {
            const r = e.currentTarget.getBoundingClientRect()
            setMenuAt(menuAt ? null : { x: r.left, y: r.bottom + 6 })
          }}
        >
          <More />
        </button>
      )}
      {menuAt && <ItemMenu item={item} at={menuAt} onClose={() => setMenuAt(null)} />}
    </div>
  )
}

const cardLabel = (item: ItemSummary) => (item.kind === 'Episode' && item.seriesTitle ? `${item.seriesTitle} ${episodeCode(item)}` : item.title)

/** An episode's show, then its season-and-number and title, each linking where you'd expect. */
function EpisodeText({ item }: { item: ItemSummary }) {
  const show = item.seriesId
  return (
    <>
      <span className="card-title">{show ? <Link to={`/item/${show}`}>{item.seriesTitle ?? item.title}</Link> : (item.seriesTitle ?? item.title)}</span>
      <span className="card-subtitle">
        {show ? <Link to={`/item/${show}?season=${item.seasonNumber}`}>{episodeCode(item)}</Link> : episodeCode(item)}{' '}
        <Link to={`/item/${item.id}`}>{item.title}</Link>
      </span>
    </>
  )
}

/** `playLoneEpisode`: a show with just one unwatched episode plays it from the poster (Latest unwatched rows). */
export function PosterCard({ item, playLoneEpisode = false }: { item: ItemSummary; playLoneEpisode?: boolean }) {
  const play = usePlay()
  const lone = playLoneEpisode && item.kind === 'Series' && item.userData.unwatchedCount === 1
  return (
    <CardShell
      item={item}
      to={itemLink(item)}
      onOpen={lone ? () => play(item) : undefined}
      playLabel={lone ? `Play the new episode of ${item.title}` : undefined}
      className="poster-card"
      art={
        <div className="card-frame">
          <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} shape={item.kind === 'Artist' || item.kind === 'Album' ? 'square' : 'poster'} />
          <WatchState item={item} />
          <Progress item={item} />
        </div>
      }
    >
      {item.kind === 'Episode' ? (
        <EpisodeText item={item} />
      ) : (
        <>
          <span className="card-title">
            <Link to={itemLink(item)}>{item.title}</Link>
          </span>
          <span className="card-subtitle">
            {item.kind === 'Season' && item.seriesId ? <Link to={`/item/${item.seriesId}`}>{item.seriesTitle}</Link> : subtitle(item)}
          </span>
        </>
      )}
    </CardShell>
  )
}

/** 16:9 card for episodes and Continue Watching: the still (or backdrop) with the show's name beneath. */
export function WideCard({ item, showSeries = true }: { item: ItemSummary; showSeries?: boolean }) {
  const image = item.artwork.thumb ?? item.artwork.backdrop
  const isEpisode = item.kind === 'Episode'
  return (
    <CardShell
      item={item}
      to={isEpisode || item.kind === 'Movie' ? `/play/${item.id}` : itemLink(item)}
      playLabel={isEpisode || item.kind === 'Movie' ? `${progressOf(item) !== null ? 'Resume' : 'Play'} ${cardLabel(item)}` : undefined}
      className="wide-card"
      art={
        <div className="card-frame">
          <Artwork src={image} title={item.title} width={500} shape="wide" accent={item.accentColor} />
          <Progress item={item} />
        </div>
      }
    >
      {isEpisode && showSeries ? (
        <EpisodeText item={item} />
      ) : (
        <>
          <span className="card-title">
            <Link to={`/item/${item.id}`}>{item.title}</Link>
          </span>
          <span className="card-subtitle">{isEpisode ? episodeCode(item) : year(item)}</span>
        </>
      )}
    </CardShell>
  )
}

/** A ranked poster: the numeral is real information here, so it's set big in the marquee face. */
export function Top10Card({ item, rank }: { item: ItemSummary; rank: number }) {
  return (
    <CardShell
      item={item}
      to={itemLink(item)}
      className="top10-card"
      art={
        <>
          <span className="top10-rank" aria-hidden="true">
            {rank}
          </span>
          <div className="card-frame">
            <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} />
          </div>
          <span className="visually-hidden">
            Number {rank}: {item.title}
          </span>
        </>
      }
    />
  )
}
