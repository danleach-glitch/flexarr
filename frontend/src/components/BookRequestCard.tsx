import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, requestBook, type BookRequestResult } from '../api/client'
import { AsyncButton } from './AsyncButton'

/** A book a request search found: in the library already (its cover opens it), requested, or a Request button. */
export function BookRequestCard({ book, query }: { book: BookRequestResult; query: string }) {
  const [state, setState] = useState<BookRequestResult['availability'] | 'sending' | 'failed'>(book.availability)
  const [message, setMessage] = useState<string>()

  async function request() {
    setState('sending')
    try {
      await requestBook(book.foreignBookId, query)
      setState('Requested')
    } catch (e) {
      setState('failed')
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The request didn’t go through.')
    }
  }

  const cover = (
    <div className="card-frame">
      <div className="artwork artwork-square">
        {book.cover ? <img src={book.cover} alt="" loading="lazy" referrerPolicy="no-referrer" /> : <span className="artwork-fallback">{book.title}</span>}
      </div>
    </div>
  )

  return (
    <div className="card external-card" title={book.overview ?? undefined}>
      {book.itemId ? (
        <Link to={`/item/${book.itemId}`} aria-label={`${book.title}, in your library`}>
          {cover}
        </Link>
      ) : (
        cover
      )}
      <span className="card-title">{book.title}</span>
      <span className="card-subtitle">{[book.author, book.year].filter(Boolean).join(', ')}</span>
      <div className="external-card-actions">
        {state === 'InLibrary' || book.itemId ? (
          <span className="external-state">In your library</span>
        ) : state === 'Requested' ? (
          <span className="external-state">Requested</span>
        ) : (
          <AsyncButton className="button small quiet" onClick={request} busy={state === 'sending'} title={message}>
            {state === 'failed' ? 'Try again' : 'Request'}
          </AsyncButton>
        )}
      </div>
      {state === 'failed' && message && (
        <span className="error card-subtitle" role="alert">
          {message}
        </span>
      )}
    </div>
  )
}
