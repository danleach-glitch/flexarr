import { Link } from 'react-router'
import type { ItemSummary } from '../api/types'
import { episodeCode } from '../lib/format'
import { Artwork } from './Artwork'

function subtitle(item: ItemSummary): string | null {
  switch (item.kind) {
    case 'Series':
      return item.childCount === 1 ? '1 season' : `${item.childCount} seasons`
    case 'Season':
      return item.childCount === 1 ? '1 episode' : `${item.childCount} episodes`
    case 'Episode':
      return episodeCode(item)
    default:
      return item.year ? String(item.year) : null
  }
}

export function PosterCard({ item }: { item: ItemSummary }) {
  return (
    <Link to={`/item/${item.id}`} className="poster-card">
      <div className="poster-frame">
        <Artwork title={item.title} />
        {item.resolution && <span className="badge">{item.resolution}</span>}
      </div>
      <div className="poster-title" title={item.title}>
        {item.title}
      </div>
      <div className="poster-subtitle">{subtitle(item)}</div>
    </Link>
  )
}
