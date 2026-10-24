import type { CSSProperties } from 'react'
import { Link } from 'react-router'
import type { ItemSummary } from '../api/types'
import { episodeCode, progressOf, year } from '../lib/format'
import { itemLink } from '../lib/links'
import { Artwork } from './Artwork'
import { Check } from './icons'

function Progress({ item }: { item: ItemSummary }) {
  const progress = progressOf(item)
  if (progress === null) return null
  return (
    <span className="progress" aria-label={`${Math.round(progress * 100)}% watched`}>
      <span style={{ width: `${progress * 100}%` }} />
    </span>
  )
}

function WatchState({ item }: { item: ItemSummary }) {
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

export function PosterCard({ item }: { item: ItemSummary }) {
  return (
    <Link to={itemLink(item)} className="card poster-card" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="card-frame">
        <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} shape={item.kind === 'Artist' || item.kind === 'Album' ? 'square' : 'poster'} />
        <WatchState item={item} />
        <Progress item={item} />
      </div>
      <span className="card-title">{item.title}</span>
      <span className="card-subtitle">{subtitle(item)}</span>
    </Link>
  )
}

/** 16:9 card for episodes and Continue Watching: the still (or backdrop) with the show's name beneath. */
export function WideCard({ item, showSeries = true }: { item: ItemSummary; showSeries?: boolean }) {
  const series = item.seriesTitle
  const image = item.artwork.thumb ?? item.artwork.backdrop
  const isEpisode = item.kind === 'Episode'
  return (
    <Link
      to={isEpisode || item.kind === 'Movie' ? `/play/${item.id}` : itemLink(item)}
      className="card wide-card"
      style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}
    >
      <div className="card-frame">
        <Artwork src={image} title={item.title} width={500} shape="wide" accent={item.accentColor} />
        <Progress item={item} />
      </div>
      {isEpisode && showSeries ? (
        <>
          <span className="card-title">{series ?? item.title}</span>
          <span className="card-subtitle">
            {episodeCode(item)} {item.title}
          </span>
        </>
      ) : (
        <>
          <span className="card-title">{item.title}</span>
          <span className="card-subtitle">{isEpisode ? episodeCode(item) : year(item)}</span>
        </>
      )}
    </Link>
  )
}

/** A ranked poster: the numeral is real information here, so it's set big in the marquee face. */
export function Top10Card({ item, rank }: { item: ItemSummary; rank: number }) {
  return (
    <Link to={itemLink(item)} className="card top10-card" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <span className="top10-rank" aria-hidden="true">
        {rank}
      </span>
      <div className="card-frame">
        <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} />
      </div>
      <span className="visually-hidden">
        Number {rank}: {item.title}
      </span>
    </Link>
  )
}
