import { Link } from 'react-router'
import { imageUrl, type PlaylistSummary } from '../api/client'
import { formatDuration } from '../lib/format'
import { Playlist } from './icons'

/**
 * A playlist's artwork: the cover chosen for it, else a mosaic of its first four tracks' album covers (one, when there
 * aren't four), else a note on a plain square.
 */
export function PlaylistCover({ playlist, width = 342 }: { playlist: Pick<PlaylistSummary, 'cover' | 'covers' | 'name'>; width?: number }) {
  if (playlist.cover) return <img className="playlist-cover" src={imageUrl(playlist.cover, width)} alt="" loading="lazy" />
  const shown = playlist.covers.length >= 4 ? playlist.covers.slice(0, 4) : playlist.covers.slice(0, 1)
  if (shown.length === 0) {
    return (
      <div className="collage square collage-empty playlist-cover-empty" aria-hidden="true">
        <Playlist />
      </div>
    )
  }
  return (
    <div className={shown.length === 4 ? 'collage square collage-four' : 'collage square'} aria-hidden="true">
      {shown.map((p, i) => (
        <img key={i} src={imageUrl(p, shown.length === 4 ? width / 2 : width)} alt="" loading="lazy" />
      ))}
    </div>
  )
}

/** "12 tracks, 48m". */
export function describePlaylist(p: PlaylistSummary) {
  const count = `${p.trackCount} ${p.trackCount === 1 ? 'track' : 'tracks'}`
  const length = formatDuration(p.durationSeconds)
  return length ? `${count}, ${length}` : count
}

/** A playlist in a row or grid, like an album card; it opens the playlist's page. */
export function PlaylistCard({ playlist }: { playlist: PlaylistSummary }) {
  return (
    <Link to={`/playlist/${playlist.id}`} className="card playlist-card">
      <div className="card-frame">
        <PlaylistCover playlist={playlist} />
      </div>
      <span className="card-title">{playlist.name}</span>
      <span className="card-subtitle">{describePlaylist(playlist)}</span>
    </Link>
  )
}
