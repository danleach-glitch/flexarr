import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, getMusicRequestStatus, imageUrl, requestMusic, unfollowArtist, unrequestAlbum, type MusicExternal, type MusicRequest } from '../api/client'
import { useApi } from '../hooks/useApi'
import { musicLink } from '../lib/musicLinks'
import { AsyncButton } from './AsyncButton'
import { ConfirmDialog } from './ConfirmDialog'
import { icon } from './icons.generated'

type State = MusicExternal['availability'] | 'sending' | 'failed' | 'refused'

/**
 * Request an album (just it) or follow an artist (their new releases) through Lidarr, or say where it stands, as a
 * labelled button with its icon. An artist "Requested" is one Lidarr already follows. Pressing Following or Requested
 * asks, then takes it back, for the household. Hidden without Lidarr; greyed while it isn't answering.
 */
export function MusicRequestButton({
  request,
  availability,
  libraryItemId,
  kind,
  size = 'small',
  title,
  onUndone,
  unanswered = false,
}: {
  request: MusicRequest
  availability: MusicExternal['availability']
  libraryItemId?: string | null
  kind: 'album' | 'artist'
  size?: 'small' | 'large'
  /** The album's title or the artist's name, for the question before taking it back. */
  title?: string
  /** After Following or Requested was taken back. */
  onUndone?: () => void
  /** Lidarr didn't answer for this page: what it says couldn't be checked, so nothing is offered. */
  unanswered?: boolean
}) {
  // What this button did, if anything: otherwise it says what the server says, which can change under it.
  // 'refused' is Lidarr saying no (it can't find it): asking again won't change that, so no Try again.
  const [sent, setSent] = useState<'sending' | 'failed' | 'refused' | 'Requested' | 'None'>()
  const [message, setMessage] = useState<string>()
  const [asking, setAsking] = useState(false)
  const [undoError, setUndoError] = useState<string>()
  const state: State = sent ?? availability
  // Lidarr not answering: say so on the page, and don't offer what can't work. (Cached: every card asks.)
  const status = useApi(getMusicRequestStatus, [])
  const offline = status.data?.reachable === false || unanswered

  async function send() {
    setSent('sending')
    setMessage(undefined)
    try {
      await requestMusic(request)
      setSent('Requested')
    } catch (e) {
      setSent(e instanceof ApiError && e.status === 409 ? 'refused' : 'failed')
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The request didn’t go through.')
    }
  }

  async function undo() {
    setUndoError(undefined)
    const { kind: _kind, ...body } = request
    try {
      await (kind === 'artist' ? unfollowArtist(body) : unrequestAlbum(body))
      setSent('None')
      setAsking(false)
      onUndone?.()
    } catch (e) {
      // A server from before undo (it answers 404 to any of these) can't do it.
      setUndoError(
        e instanceof ApiError && (e.status === 404 || e.status === 405)
          ? 'Update the server to undo this.'
          : e instanceof ApiError && e.messages[0]
            ? e.messages[0]
            : 'That didn’t work. Try again.',
      )
    }
  }

  // No Lidarr (or music requests off): nothing to offer.
  if (status.data?.configured === false) return null
  // A library artist can still be followed; a library album has nothing left to ask for.
  if (kind === 'album' && (libraryItemId || state === 'Available')) return <span className="external-state">In your library</span>
  const className = size === 'small' ? 'button small quiet' : 'button quiet'
  if (state === 'Requested' || state === 'PartlyAvailable') {
    const Icon = kind === 'artist' ? icon.following : icon.requested
    return (
      <>
        <AsyncButton
          className={className}
          aria-pressed
          onClick={() => setAsking(true)}
          disabled={offline}
          title={offline ? 'Lidarr isn’t answering.' : kind === 'artist' ? 'Stop following' : 'Unrequest'}
        >
          <Icon aria-hidden /> {kind === 'artist' ? 'Following' : 'Requested'}
        </AsyncButton>
        {asking && (
          <ConfirmDialog
            title={kind === 'artist' ? `Stop following ${title ?? 'this artist'}?` : `Unrequest ${title ?? 'this album'}?`}
            confirmLabel={kind === 'artist' ? 'Stop following' : 'Unrequest'}
            cancelLabel={kind === 'artist' ? 'Keep following' : 'Keep it'}
            danger
            error={undoError}
            onConfirm={undo}
            onClose={() => {
              setAsking(false)
              setUndoError(undefined)
            }}
          />
        )}
      </>
    )
  }
  if (state === 'refused')
    return (
      <>
        <span className="external-state">Can’t request</span>
        {message && (
          <span className="error" role="alert">
            {message}
          </span>
        )}
      </>
    )
  const Icon = kind === 'artist' ? icon.follow : icon.request
  return (
    <>
      <AsyncButton
        className={className}
        onClick={send}
        busy={state === 'sending'}
        disabled={offline}
        title={offline ? 'Lidarr isn’t answering.' : message}
      >
        <Icon aria-hidden /> {state === 'failed' ? 'Try again' : kind === 'artist' ? 'Follow' : 'Request album'}
      </AsyncButton>
      {state === 'failed' && message && (
        <span className="error" role="alert">
          {message}
        </span>
      )}
    </>
  )
}

/** An artist (round photo) or album (square cover) from outside the library, with its request button. */
export function MusicExternalCard({ item, showArtist = true, unanswered = false }: { item: MusicExternal; showArtist?: boolean; unanswered?: boolean }) {
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
          <MusicRequestButton request={request} availability={item.availability} libraryItemId={item.libraryItemId} kind={item.kind} title={item.title} unanswered={unanswered} />
        )}
      </div>
    </div>
  )
}
