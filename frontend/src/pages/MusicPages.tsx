import type { CSSProperties } from 'react'
import { Link } from 'react-router'
import { imageUrl, type ItemDetail } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/Cards'
import { Play } from '../components/icons'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { formatClock, formatDuration } from '../lib/format'

/** An album: cover, facts and its tracklist. Discs are separated when there's more than one. */
export function AlbumPage({ detail }: { detail: ItemDetail }) {
  const player = useAudioPlayer()
  const { item, children: tracks } = detail
  const artist = detail.ancestors[0]
  const total = tracks.reduce((sum, t) => sum + (t.durationSeconds ?? 0), 0)
  const discs = new Set(tracks.map((t) => t.discNumber ?? 1)).size

  return (
    <article className="music-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{item.title}</h1>
          <p className="facts">
            {artist && (
              <Link to={`/item/${artist.id}`} className="music-artist-link">
                {artist.title}
              </Link>
            )}
            {item.year && <span>{item.year}</span>}
            <span>{tracks.length === 1 ? '1 track' : `${tracks.length} tracks`}</span>
            {total > 0 && <span>{formatDuration(total)}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => player.play(tracks, 0)} disabled={tracks.length === 0}>
              <Play /> Play
            </button>
            <button className="button quiet" onClick={() => player.play([...tracks].sort(() => Math.random() - 0.5), 0)} disabled={tracks.length < 2}>
              Shuffle
            </button>
          </div>
        </div>
      </header>

      <ol className="tracklist">
        {tracks.map((track, i) => {
          const isCurrent = player.current?.id === track.id
          const newDisc = discs > 1 && (i === 0 || (tracks[i - 1].discNumber ?? 1) !== (track.discNumber ?? 1))
          return (
            <li key={track.id}>
              {newDisc && <h2 className="tracklist-disc">Disc {track.discNumber ?? 1}</h2>}
              <button className={isCurrent ? 'track current' : 'track'} onClick={() => player.play(tracks, i)} aria-current={isCurrent || undefined}>
                <span className="track-number">{isCurrent && player.playing ? <Play /> : (track.trackNumber ?? i + 1)}</span>
                <span className="track-title">{track.title}</span>
                <span className="track-time">{track.durationSeconds ? formatClock(track.durationSeconds) : ''}</span>
              </button>
            </li>
          )
        })}
      </ol>
    </article>
  )
}

/** An artist and their albums, newest first. */
export function ArtistPage({ detail }: { detail: ItemDetail }) {
  const { item, children: albums } = detail
  return (
    <article className="music-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{item.title}</h1>
          <p className="facts">
            <span>{albums.length === 1 ? '1 album' : `${albums.length} albums`}</span>
          </p>
        </div>
      </header>
      <div className="poster-grid music-albums">
        {albums.map((album) => (
          <PosterCard key={album.id} item={album} />
        ))}
      </div>
    </article>
  )
}
