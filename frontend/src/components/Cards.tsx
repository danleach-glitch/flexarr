import { memo, useState, type CSSProperties, type ReactNode } from 'react'
import { Link } from 'react-router'
import type { ItemSummary } from '../api/types'
import { episodeCode, episodeHeading, episodeName, progressOf, year } from '../lib/format'
import { authorsLine, isBook } from '../lib/books'
import { itemLink } from '../lib/links'
import { getTracks, imageUrl } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { usePlay } from '../hooks/usePlay'
import { Artwork } from './Artwork'
import { AsyncButton } from './AsyncButton'
import { Check, More, Play } from './icons'
import { hasMenu, ItemMenu, type MenuPoint } from './ItemMenu'
import { WatchedToggle } from './WatchedToggle'

function Progress({ item }: { item: ItemSummary }) {
  const progress = progressOf(item)
  if (progress === null) return null
  return (
    <span className="progress" aria-label={`${Math.round(progress * 100)}% ${listensTo(item) ? 'listened' : 'watched'}`}>
      <span style={{ width: `${progress * 100}%` }} />
    </span>
  )
}

/** Books and podcast episodes play in the audio player rather than the video one. */
const listensTo = (item: ItemSummary) => item.kind === 'Audiobook' || item.kind === 'PodcastEpisode'

/** Films, episodes and books get a tick of their own (a book's says finished). */
const toggles = (item: ItemSummary) => item.kind === 'Movie' || item.kind === 'Episode' || item.kind === 'Audiobook'

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
      if (item.homeVideo) return item.childCount === 1 ? '1 folder' : `${item.childCount} folders`
      return item.childCount === 1 ? '1 season' : `${item.childCount} seasons`
    case 'Season':
      if (item.homeVideo) return item.childCount === 1 ? '1 video' : `${item.childCount} videos`
      return item.childCount === 1 ? '1 episode' : `${item.childCount} episodes`
    case 'Episode':
      return episodeCode(item)
    case 'Artist':
      return item.childCount === 1 ? '1 album' : `${item.childCount} albums`
    case 'Album':
      return [item.seriesTitle, item.year].filter(Boolean).join(', ')
    case 'Audiobook':
      return authorsLine(item) ?? year(item)?.toString() ?? null
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
function CardShell({ item, to, className, art, playLabel, onOpen, overlay, continueWatching, children }: { item: ItemSummary; to: string; className: string; art: ReactNode; playLabel?: string; onOpen?: () => void; overlay?: ReactNode; continueWatching?: boolean; children?: ReactNode }) {
  const [menuAt, setMenuAt] = useState<MenuPoint | null>(null)
  const style = item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined
  const menu = hasMenu(item, continueWatching)
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
      {/* A control over the artwork (an album's play button) sits beside the link, not inside it: the link is hidden
          from assistive technology when the text beneath links to the same page, and a button inside it would be too. */}
      <div className="card-art-wrap">
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
        {overlay}
      </div>
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
      {menuAt && <ItemMenu item={item} at={menuAt} onClose={() => setMenuAt(null)} continueWatching={continueWatching} />}
    </div>
  )
}

const cardLabel = (item: ItemSummary) => (item.kind === 'Episode' ? episodeName(item) : item.title)

/** An episode's show, then its season-and-number and title, each linking where you'd expect. */
function EpisodeText({ item }: { item: ItemSummary }) {
  const show = item.seriesId
  return (
    <>
      <span className="card-title">{show ? <Link to={`/item/${show}`}>{item.seriesTitle ?? item.title}</Link> : (item.seriesTitle ?? item.title)}</span>
      <span className="card-subtitle">{show ? <Link to={`/item/${show}?season=${item.seasonNumber}`}>{episodeHeading(item)}</Link> : episodeHeading(item)}</span>
      <span className="card-subtitle">
        <Link to={`/item/${item.id}`}>{item.title}</Link>
      </span>
    </>
  )
}

/**
 * `playLoneEpisode`: a show with just one unwatched episode plays it from the poster (Latest unwatched rows).
 * Memoised: a grid can hold thousands, and they needn't re-render when the page around them does.
 */
export const PosterCard = memo(function PosterCard({ item, playLoneEpisode = false }: { item: ItemSummary; playLoneEpisode?: boolean }) {
  const play = usePlay()
  const player = useAudioPlayer()
  const lone = playLoneEpisode && item.kind === 'Series' && item.userData.unwatchedCount === 1
  const music = item.kind === 'Artist' || item.kind === 'Album'
  const book = isBook(item)
  return (
    <CardShell
      item={item}
      to={itemLink(item)}
      onOpen={lone ? () => play(item) : undefined}
      playLabel={lone ? `Play the new episode of ${item.title}` : undefined}
      className="poster-card"
      overlay={
        book ? (
          <button className="card-play" aria-label={`${progressOf(item) !== null ? 'Resume' : 'Play'} ${item.title}`} onClick={() => player.play([item])}>
            <Play />
          </button>
        ) : music ? (
          // Hovering the artwork shows a play button, as on Spotify; the artwork itself still opens the page.
          <AsyncButton
            className="card-play"
            aria-label={`Play ${item.title}`}
            onClick={() =>
              getTracks(item.id).then((tracks) => {
                if (tracks.length > 0) player.play(tracks, 0, { openNowPlaying: true })
              })
            }
          >
            <Play />
          </AsyncButton>
        ) : undefined
      }
      art={
        <div className="card-frame">
          <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} shape={music || book ? 'square' : 'poster'} />
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
})

/** 16:9 card for episodes and Continue Watching (`continueWatching`): the still (or backdrop) with the show's name beneath. */
export function WideCard({ item, showSeries = true, continueWatching = false }: { item: ItemSummary; showSeries?: boolean; continueWatching?: boolean }) {
  if (listensTo(item)) return <ListenCard item={item} continueWatching={continueWatching} />
  const image = item.artwork.thumb ?? item.artwork.backdrop
  const isEpisode = item.kind === 'Episode'
  return (
    <CardShell
      item={item}
      to={isEpisode || item.kind === 'Movie' ? `/play/${item.id}` : itemLink(item)}
      playLabel={isEpisode || item.kind === 'Movie' ? `${progressOf(item) !== null ? 'Resume' : 'Play'} ${cardLabel(item)}` : undefined}
      className="wide-card"
      continueWatching={continueWatching}
      art={
        <div className="card-frame">
          <Artwork src={image} fallback={item.artwork.backdrop} title={item.title} width={500} shape="wide" accent={item.accentColor} />
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

/**
 * A book or podcast episode in a wide row (Continue watching or listening): its square cover centred over a blurred,
 * darkened copy of itself. The cover resumes it in the audio player, as the book's page and the episode's play button do.
 */
function ListenCard({ item, continueWatching }: { item: ItemSummary; continueWatching: boolean }) {
  const player = useAudioPlayer()
  const book = isBook(item)
  const page = book ? itemLink(item) : `/podcasts/episode/${item.id}`
  const podcast = item.seriesId ?? item.parentId
  const cover = book ? (item.artwork.poster ?? item.artwork.thumb) : (item.artwork.thumb ?? item.artwork.poster)
  const resumable = progressOf(item) !== null && !item.userData.watched
  return (
    <CardShell
      item={item}
      to={page}
      onOpen={() => player.play([item])}
      playLabel={`${resumable ? 'Resume' : 'Play'} ${item.title}`}
      className="wide-card listen-card"
      continueWatching={continueWatching}
      art={
        <div className="card-frame listen-frame">
          {cover && <img className="listen-backdrop" src={imageUrl(cover, 185)} alt="" aria-hidden="true" loading="lazy" />}
          <div className="listen-cover">
            <Artwork src={cover} title={book ? item.title : (item.seriesTitle ?? item.title)} width={342} shape="square" accent={item.accentColor} />
          </div>
          <Progress item={item} />
        </div>
      }
    >
      <span className="card-title">
        <Link to={page}>{item.title}</Link>
      </span>
      <span className="card-subtitle">{book ? authorsLine(item) : podcast && item.seriesTitle ? <Link to={`/podcasts/${podcast}`}>{item.seriesTitle}</Link> : item.seriesTitle}</span>
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

/**
 * A track in a row (Most played): its cover plays it, and the rest of the row follows on, like pressing play on a
 * playlist. The text links to its album and its artist, since a track has no page of its own.
 */
export function TrackCard({ item, queue, index }: { item: ItemSummary; queue: ItemSummary[]; index: number }) {
  const player = useAudioPlayer()
  const album = item.parentId ? `/item/${item.parentId}` : itemLink(item)
  const current = player.current?.id === item.id
  return (
    <CardShell
      item={item}
      to={album}
      onOpen={() => player.play(queue, index)}
      playLabel={`Play ${item.title}`}
      className={current ? 'track-card current' : 'track-card'}
      art={
        <div className="card-frame">
          <Artwork src={item.artwork.poster} title={item.title} width={342} accent={item.accentColor} shape="square" />
        </div>
      }
    >
      <span className="card-title">
        <Link to={album}>{item.title}</Link>
      </span>
      <span className="card-subtitle">{item.seriesId ? <Link to={`/item/${item.seriesId}`}>{item.seriesTitle}</Link> : item.seriesTitle}</span>
    </CardShell>
  )
}
