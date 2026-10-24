import { Link } from 'react-router'
import { imageUrl, type UpcomingItem } from '../api/client'
import { dayLabel } from '../lib/dates'
import { episodeCode } from '../lib/format'

/** An episode airing or a film releasing soon, from Sonarr and Radarr. */
export function UpcomingCard({ item }: { item: UpcomingItem }) {
  const body = (
    <>
      <div className="card-frame">
        <div className="artwork artwork-wide">
          {item.still ? <img src={imageUrl(item.still, 500)} alt="" loading="lazy" /> : <span className="artwork-fallback">{item.seriesTitle ?? item.title}</span>}
        </div>
        <span className="upcoming-when">{dayLabel(item.date)}</span>
        {item.download && (
          <span className="progress" aria-label={`Downloading, ${Math.round(item.download.progress * 100)}%`}>
            <span style={{ width: `${item.download.progress * 100}%` }} />
          </span>
        )}
      </div>
      <span className="card-title">{item.seriesTitle ?? item.title}</span>
      <span className="card-subtitle">
        {item.kind === 'Episode' ? `${episodeCode({ seasonNumber: item.seasonNumber, episodeNumber: item.episodeNumber, episodeNumberEnd: null, airDate: null })} ${item.title}` : item.when}
      </span>
    </>
  )
  return item.libraryItemId ? (
    <Link to={`/item/${item.libraryItemId}`} className="card wide-card upcoming-card">
      {body}
    </Link>
  ) : (
    <div className="card wide-card upcoming-card">{body}</div>
  )
}
