import { useState } from 'react'
import { Link } from 'react-router'
import { imageUrl, type ExternalTitle } from '../api/client'
import { RequestButton } from './RequestButton'
import { WatchlistButton } from './WatchlistButton'

/** A title from outside the library: its poster opens it (in Flexarr if you have it), with request and watchlist buttons. */
export function ExternalCard({ title }: { title: ExternalTitle }) {
  const [requested, setRequested] = useState(false)
  const poster = (
    <div className="card-frame">
      <div className="artwork artwork-poster">
        {title.poster ? <img src={imageUrl(title.poster, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{title.title}</span>}
      </div>
    </div>
  )

  return (
    <div className="card external-card">
      <Link
        to={title.libraryItemId ? `/item/${title.libraryItemId}` : `/discover/${title.mediaType}/${title.tmdbId}`}
        aria-label={title.libraryItemId ? `${title.title}, in your library` : `${title.title}, more about it`}
      >
        {poster}
      </Link>
      <span className="card-title">{title.title}</span>
      <span className="card-subtitle">{[title.mediaType === 'tv' ? 'Series' : 'Movie', title.year].filter(Boolean).join(', ')}</span>
      <div className="external-card-actions">
        <RequestButton key={requested ? 'requested' : 'asked'} title={requested ? { ...title, availability: 'Requested' } : title} />
        <WatchlistButton
          compact
          titleKey={title.libraryItemId ? { libraryItemId: title.libraryItemId } : { mediaType: title.mediaType, tmdbId: title.tmdbId }}
          title={title.title}
          on={title.onWatchlist ?? false}
          onRequested={() => setRequested(true)}
        />
      </div>
    </div>
  )
}
