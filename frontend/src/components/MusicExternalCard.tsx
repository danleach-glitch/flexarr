import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, getMusicRequestStatus, imageUrl, requestMusic, unfollowArtist, unrequestAlbum, type MusicExternal, type MusicRequest } from '../api/client'
import { useApi } from '../hooks/useApi'
import { musicLink } from '../lib/musicLinks'
import { AsyncButton } from './AsyncButton'
import { ConfirmDialog } from './ConfirmDialog'
import { icon } from './icons.generated'
import { undoFailure } from '../lib/musicUndo'
import { t } from '../i18n'

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
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : t('The request didn’t go through.'))
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
      setUndoError(undoFailure(e))
    }
  }

  // No Lidarr (or music requests off): nothing to offer.
  if (status.data?.configured === false) return null
  // A library artist can still be followed; a library album has nothing left to ask for.
  if (kind === 'album' && (libraryItemId || state === 'Available')) return <span className="external-state">{t('In your library')}</span>
  const className = size === 'small' ? 'button small quiet' : 'button quiet'
  // A grid of cards is a row of the same words to a screen reader: each says whose it is (a page's own needn't).
  const named = (action: 'following' | 'requested' | 'retry' | 'follow' | 'request') => {
    if (size !== 'small' || !title) return undefined
    switch (action) {
      case 'following':
        return t('Following {title}', { title })
      case 'requested':
        return t('Requested {title}', { title })
      case 'retry':
        return t('Try again {title}', { title })
      case 'follow':
        return t('Follow {title}', { title })
      default:
        return t('Request album {title}', { title })
    }
  }
  if (state === 'Requested' || state === 'PartlyAvailable') {
    const Icon = kind === 'artist' ? icon.following : icon.requested
    return (
      <>
        <AsyncButton
          className={className}
          aria-pressed
          aria-label={named(kind === 'artist' ? 'following' : 'requested')}
          onClick={() => setAsking(true)}
          disabled={offline}
          title={offline ? t('Lidarr isn’t answering.') : kind === 'artist' ? t('Stop following') : t('Unrequest')}
        >
          <Icon aria-hidden /> {kind === 'artist' ? t('Following') : t('Requested')}
        </AsyncButton>
        {offline && size === 'large' && <span className="external-state">{t('Lidarr isn’t answering.')}</span>}
        {asking && (
          <ConfirmDialog
            title={
              kind === 'artist'
                ? title
                  ? t('Stop following {name}?', { name: title })
                  : t('Stop following this artist?')
                : title
                  ? t('Unrequest {name}?', { name: title })
                  : t('Unrequest this album?')
            }
            confirmLabel={kind === 'artist' ? t('Stop following') : t('Unrequest')}
            cancelLabel={kind === 'artist' ? t('Keep following') : t('Keep it')}
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
        <span className="external-state">{t('Can’t request')}</span>
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
        aria-label={named(state === 'failed' ? 'retry' : kind === 'artist' ? 'follow' : 'request')}
        busy={state === 'sending'}
        disabled={offline}
        title={offline ? t('Lidarr isn’t answering.') : message}
      >
        <Icon aria-hidden /> {state === 'failed' ? t('Try again') : kind === 'artist' ? t('Follow') : t('Request album')}
      </AsyncButton>
      {offline && size === 'large' && <span className="external-state">{t('Lidarr isn’t answering.')}</span>}
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
        <Link to={to} aria-label={item.libraryItemId ? t('{title}, in your library', { title: item.title }) : t('{title}, more about it', { title: item.title })}>
          {art}
        </Link>
      ) : (
        art
      )}
      <span className="card-title">{item.title}</span>
      {subtitle && <span className="card-subtitle">{subtitle}</span>}
      <div className="external-card-actions">
        {/* A library artist can still be followed: servers that say whether they are (0.59 on) offer it. */}
        {(item.kind === 'album' || !item.libraryItemId || item.followed != null) && (
          <MusicRequestButton
            request={request}
            availability={item.kind === 'artist' && item.followed != null ? (item.followed ? 'Requested' : 'None') : item.availability}
            libraryItemId={item.libraryItemId}
            kind={item.kind}
            title={item.title}
            unanswered={unanswered}
          />
        )}
      </div>
    </div>
  )
}
