import { Link } from 'react-router'
import type { MixSummary } from '../api/client'
import { PlaylistCover } from './PlaylistCover'

/** A "Made for you" mix in a row, like a playlist card: a mosaic of its covers, its name and what it's made of. */
export function MixCard({ mix }: { mix: MixSummary }) {
  return (
    <Link to={`/mix/${encodeURIComponent(mix.id)}`} className="card playlist-card mix-card">
      <div className="card-frame">
        <PlaylistCover playlist={{ cover: null, covers: mix.covers, name: mix.name }} />
      </div>
      <span className="card-title">{mix.name}</span>
      <span className="card-subtitle">{mix.description}</span>
    </Link>
  )
}
