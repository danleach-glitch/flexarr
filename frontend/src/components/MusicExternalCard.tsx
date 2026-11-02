import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, imageUrl, requestMusic, type MusicExternal, type MusicRequest } from '../api/client'
import { musicLink } from '../lib/musicLinks'
import { AsyncButton } from './AsyncButton'

type State = MusicExternal['availability'] | 'sending' | 'failed'

/**
 * Request an album (just it) or follow an artist (their new releases) through Lidarr, or say where it stands. An
 * artist "Requested" is one Lidarr already follows.
 */
export function MusicRequestButton({
  request,
  availability,
  libraryItemId,
  kind,
  size = 'small',
}: {
  request: MusicRequest
  availability: MusicExternal['availability']
  libraryItemId?: string | null
  kind: 'album' | 'artist'
  size?: 'small' | 'large'
}) {
  const [state, setState] = useState<State>(availability)
  const [message, setMessage] = useState<string>()

  async function send() {
    setState('sending')
    try {
      await requestMusic(request)
      setState('Requested')
    } catch (e) {
      setState('failed')
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The request didn’t go through.')
    }
  }

  // A library artist can still be followed; a library album has nothing left to ask for.
  if (kind === 'album' && (libraryItemId || state === 'Available')) return <span className="external-state">In your library</span>
  if (state === 'Requested' || state === 'PartlyAvailable') return <span className="external-state">{kind === 'artist' ? 'Following' : 'Requested'}</span>
  return (
    <>
      <AsyncButton className={size === 'small' ? 'button small quiet' : 'button'} onClick={send} busy={state === 'sending'} title={message}>
        {state === 'failed' ? 'Try again' : kind === 'artist' ? 'Follow artist' : 'Request album'}
      </AsyncButton>
      {size === 'large' && state === 'failed' && message && (
        <span className="error" role="alert">
          {message}
        </span>
      )}
    </>
  )
}

/** An artist (round photo) or album (square cover) from outside the library, with its request button. */
export function MusicExternalCard({ item, showArtist = true }: { item: MusicExternal; showArtist?: boolean }) {
  const to = musicLink(item)
  const art = (
    <div className="card-frame">
      <div className={`artwork artwork-square${item.kind === 'artist' ? ' artwork-round' : ''}`}>
        {item.image ? <img src={imageUrl(item.image, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{item.title}</span>}
      </div>
    </div>
  )
  const subtitle = item.kind === 'artist' ? null : [showArtist ? item.artist : null, item.year].filter(Boolean).join(', ')
  const request: MusicRequest =
    item.kind === 'artist'
      ? { kind: 'artist', deezerId: item.deezerId }
      : item.deezerId > 0
        ? { kind: 'album', deezerId: item.deezerId }
        : { kind: 'album', musicBrainzId: item.musicBrainzId ?? undefined }

  return (
    <div className={`card external-card music-external-card music-external-${item.kind}`}>
      {to ? (
        <Link to={to} aria-label={item.libraryItemId ? `${item.title}, in your library` : `${item.title}, more about it`}>
          {art}
        </Link>
      ) : (
        art
      )}
      <span className="card-title">{item.title}</span>
      {subtitle && <span className="card-subtitle">{subtitle}</span>}
      <div className="external-card-actions">
        {(item.kind === 'album' || !item.libraryItemId) && (
          <MusicRequestButton request={request} availability={item.availability} libraryItemId={item.libraryItemId} kind={item.kind} />
        )}
      </div>
    </div>
  )
}
