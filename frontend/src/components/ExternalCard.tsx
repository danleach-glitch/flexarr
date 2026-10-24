import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, imageUrl, requestTitle, type ExternalTitle } from '../api/client'

/** A title from outside the library: a poster with one action, request it or open it. */
export function ExternalCard({ title }: { title: ExternalTitle }) {
  const [state, setState] = useState<ExternalTitle['availability'] | 'sending' | 'failed'>(title.availability)
  const [message, setMessage] = useState<string>()

  async function request() {
    setState('sending')
    try {
      await requestTitle(title.mediaType, title.tmdbId)
      setState('Requested')
    } catch (e) {
      setState('failed')
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The request didn’t go through.')
    }
  }

  const poster = (
    <div className="card-frame">
      <div className="artwork artwork-poster">
        {title.poster ? <img src={imageUrl(title.poster, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{title.title}</span>}
      </div>
    </div>
  )

  return (
    <div className="card external-card">
      {title.libraryItemId ? (
        <Link to={`/item/${title.libraryItemId}`} aria-label={`${title.title}, in your library`}>
          {poster}
        </Link>
      ) : (
        poster
      )}
      <span className="card-title">{title.title}</span>
      <span className="card-subtitle">{[title.mediaType === 'tv' ? 'Series' : 'Film', title.year].filter(Boolean).join(', ')}</span>
      {title.libraryItemId || state === 'Available' ? (
        <span className="external-state">In your library</span>
      ) : state === 'Requested' || state === 'PartlyAvailable' ? (
        <span className="external-state">{state === 'Requested' ? 'Requested' : 'Partly in your library'}</span>
      ) : (
        <button className="button small quiet" onClick={request} disabled={state === 'sending'} title={message}>
          {state === 'sending' ? 'Requesting…' : state === 'failed' ? 'Try again' : 'Request'}
        </button>
      )}
    </div>
  )
}
