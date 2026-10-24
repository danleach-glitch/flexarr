import { useState } from 'react'
import { ApiError, requestTitle, type ExternalTitle } from '../api/client'

/** Request a title through Seerr, or say where it stands if it's already in hand. */
export function RequestButton({ title, size = 'small' }: { title: Pick<ExternalTitle, 'mediaType' | 'tmdbId' | 'availability' | 'libraryItemId'>; size?: 'small' | 'large' }) {
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

  if (title.libraryItemId || state === 'Available') return <span className="external-state">In your library</span>
  if (state === 'Requested' || state === 'PartlyAvailable') return <span className="external-state">{state === 'Requested' ? 'Requested' : 'Partly in your library'}</span>
  return (
    <>
      <button className={size === 'small' ? 'button small quiet' : 'button'} onClick={request} disabled={state === 'sending'} title={message}>
        {state === 'sending' ? 'Requesting…' : state === 'failed' ? 'Try again' : 'Request'}
      </button>
      {size === 'large' && state === 'failed' && message && (
        <span className="error" role="alert">
          {message}
        </span>
      )}
    </>
  )
}
