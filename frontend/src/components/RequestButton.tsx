import { useState } from 'react'
import { ApiError, getRequestStatusCached, requestTitle, unrequestTitle, type ExternalTitle } from '../api/client'
import { useApi } from '../hooks/useApi'
import { undoFailure } from '../lib/musicUndo'
import { AsyncButton } from './AsyncButton'
import { ConfirmDialog } from './ConfirmDialog'
import { icon } from './icons.generated'
import { t } from '../i18n'

/**
 * Request a film or show (or some of a show's seasons) through Seerr, or say where it stands, as music's request
 * button does: Requested is a pressed button that asks, then takes the request back for the household. Greyed while
 * Seerr isn't answering; a request Seerr can't take says Can't request, with why.
 */
export function RequestButton({
  title,
  size = 'small',
  seasons,
  label,
  name,
  onUndone,
}: {
  title: Pick<ExternalTitle, 'mediaType' | 'tmdbId' | 'availability' | 'libraryItemId'> & { title?: string }
  size?: 'small' | 'large'
  /** For a show, request (and take back) just these seasons. */
  seasons?: number[]
  /** What the button says to a screen reader, e.g. "Request season 2". */
  label?: string
  /** What the question before taking it back calls it, e.g. "Season 2"; the title's own name otherwise. */
  name?: string
  /** After Requested was taken back. */
  onUndone?: () => void
}) {
  // What this button did, if anything: otherwise it says what the server says. 'refused' is Seerr (or the server)
  // saying it can't be had: asking again won't change that, so no Try again.
  const [sent, setSent] = useState<'sending' | 'failed' | 'refused' | 'Requested' | 'None'>()
  const [message, setMessage] = useState<string>()
  const [asking, setAsking] = useState(false)
  const [undoError, setUndoError] = useState<string>()
  const state = sent ?? title.availability
  // Seerr not answering: say so, and don't offer what can't work. (Cached: every card asks.)
  const status = useApi(getRequestStatusCached, [])
  const offline = status.data?.reachable === false
  const what = name ?? title.title

  async function send() {
    setSent('sending')
    setMessage(undefined)
    try {
      await requestTitle(title.mediaType, title.tmdbId, seasons)
      setSent('Requested')
    } catch (e) {
      setSent(e instanceof ApiError && (e.status === 409 || e.status === 403) ? 'refused' : 'failed')
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'The request didn’t go through.')
    }
  }

  async function undo() {
    setUndoError(undefined)
    try {
      await unrequestTitle(title.mediaType, title.tmdbId, seasons)
      setSent('None')
      setAsking(false)
      onUndone?.()
    } catch (e) {
      setUndoError(undoFailure(e))
    }
  }

  if (title.libraryItemId || state === 'Available') return <span className="external-state">{t('In your library')}</span>
  if (state === 'PartlyAvailable') return <span className="external-state">{t('Partly in your library')}</span>
  const className = size === 'small' ? 'button small quiet' : 'button quiet'
  const offlineNote = offline && size === 'large' && <span className="external-state">{t('Seerr isn’t answering.')}</span>
  if (state === 'Requested') {
    return (
      <>
        <AsyncButton
          className={className}
          aria-pressed
          onClick={() => setAsking(true)}
          disabled={offline}
          title={offline ? 'Seerr isn’t answering.' : 'Unrequest'}
          aria-label={label ? label.replace(/^Request\b/, 'Unrequest') : undefined}
        >
          <icon.requested aria-hidden /> {t('Requested')}
        </AsyncButton>
        {offlineNote}
        {asking && (
          <ConfirmDialog
            title={`Unrequest ${what ?? (title.mediaType === 'movie' ? 'this movie' : 'this show')}?`}
            confirmLabel="Unrequest"
            cancelLabel="Keep it"
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
  return (
    <>
      <AsyncButton
        className={className}
        onClick={send}
        busy={state === 'sending'}
        disabled={offline}
        title={offline ? 'Seerr isn’t answering.' : message}
        aria-label={label}
      >
        <icon.request aria-hidden /> {state === 'failed' ? 'Try again' : 'Request'}
      </AsyncButton>
      {offlineNote}
      {size === 'large' && state === 'failed' && message && (
        <span className="error" role="alert">
          {message}
        </span>
      )}
    </>
  )
}
