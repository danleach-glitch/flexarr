import { errorText } from '../lib/errorText'
import { useState } from 'react'
import { ApiError, deleteItem, getDeletion, type ItemSummary } from '../api/client'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { arrFor } from '../lib/apps'
import { episodeCode } from '../lib/format'
import { useModal } from '../hooks/useModal'
import { t } from '../i18n'

/**
 * Confirms deleting a film, show, episode, book, album or artist for good. Radarr, Sonarr or Lidarr delete the files from disk when they manage it;
 * otherwise (and always for books) Flexarr does, and the dialog lists the files first.
 */
export function DeleteDialog({ item, onDeleted, onClose }: { item: ItemSummary; onDeleted: () => void; onClose: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const plan = useApi((signal) => getDeletion(item.id, signal), [item.id])

  const dialog = useModal<HTMLDivElement>(onClose, !busy)

  const app = arrFor(item.kind)
  const what =
    item.kind === 'Movie'
      ? { name: item.title, noun: 'movie', detail: t('Radarr deletes the movie’s files from disk and stops tracking it.') }
      : item.kind === 'Audiobook'
        ? { name: item.title, noun: 'book', detail: '' } // no app manages books: the file list below says it all
      : item.kind === 'Artist'
        ? { name: item.title, noun: 'artist', detail: t('Lidarr deletes the artist and all their files from disk.') }
      : item.kind === 'Album'
        ? { name: item.title, noun: 'album', detail: t('Lidarr deletes the album’s files from disk.') }
      : item.kind === 'Series'
        ? { name: item.title, noun: 'show', detail: t('Sonarr deletes the files of every episode from disk and stops tracking the show.') }
        : {
            name: [item.seriesTitle, episodeCode(item), item.title].filter(Boolean).join(' '),
            noun: 'episode',
            detail: t('Sonarr deletes this episode’s file from disk and stops downloading it.'),
          }

  async function confirm() {
    setBusy(true)
    setError(undefined)
    try {
      await deleteItem(item.id, (plan.data?.copies?.length ?? 0) > 1)
      onDeleted()
    } catch (e) {
      setError(errorText(e))
      setBusy(false)
      plan.reload() // Some copies may have gone: it lists what's left, and deleting again finishes the job.
    }
  }

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel confirm-dialog" ref={dialog} role="alertdialog" aria-modal="true" aria-labelledby="delete-title" aria-describedby="delete-detail" onClick={(e) => e.stopPropagation()}>
        <h2 id="delete-title">{t('Delete {name}?', { name: what.name })}</h2>
        <div id="delete-detail" className="confirm-dialog-detail">
          {plan.loading && !plan.data && <p>{t('Checking how to delete it…')}</p>}
          {plan.error && <p className="error">{t('Couldn’t check how to delete it: {reason}', { reason: plan.error instanceof ApiError && plan.error.messages[0] ? plan.error.messages[0] : plan.error.message })}</p>}
          {plan.data?.copies && plan.data.copies.length > 1 && (
            <>
              <p>{t('This deletes every copy, in every library:')}</p>
              <ul className="delete-files">
                {plan.data.copies.map((c) => (
                  <li key={c.itemId}>
                    <strong>{c.library || t('A hidden library')}</strong> {c.managedBy && t('{app} deletes it', { app: c.managedBy })}
                    {c.managedBy && c.files.length > 0 && <div className="muted">{t('Flexarr deletes these too:')}</div>}
                    {c.files.slice(0, 5).map((f) => (
                      <code key={f} className="confirm-dialog-path">
                        {f}
                      </code>
                    ))}
                    {c.files.length > 5 && <div className="muted">{t('and {count} more', { count: c.files.length - 5 })}</div>}
                  </li>
                ))}
              </ul>
              <p>{plan.data.copies.some((c) => !c.managedBy && c.files.length > 0) ? t('Folders they leave empty go too. You can’t undo this.') : t('You can’t undo this.')}</p>
            </>
          )}
          {!(plan.data?.copies && plan.data.copies.length > 1) && plan.data?.managedBy && (
            <>
              <p>{t('{detail} You can’t undo this.', { detail: what.detail })}</p>
              {plan.data.files.length > 0 && (
                <>
                  <p>{t('{app} doesn’t track these files, so Flexarr deletes them too:', { app: plan.data.managedBy })}</p>
                  <ul className="delete-files">
                    {plan.data.files.map((f) => (
                      <li key={f}>
                        <code className="confirm-dialog-path">{f}</code>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </>
          )}
          {plan.data && !plan.data.managedBy && !(plan.data.copies && plan.data.copies.length > 1) && (
            <>
              <p>
                {app
                  ? t('{app} doesn’t manage this {kind, select, movie {movie} book {book} artist {artist} album {album} show {show} other {episode}}, so Flexarr deletes {count, plural, =0 {it from the library: there are no files on disk.} one {its file from disk:} other {its # files from disk:}}', { app, kind: what.noun, count: plan.data.files.length })
                  : t('Flexarr deletes {count, plural, =0 {it from the library: there are no files on disk.} one {its file from disk:} other {its # files from disk:}}', { count: plan.data.files.length })}
              </p>
              {plan.data.files.length > 0 && (
                <ul className="delete-files">
                  {plan.data.files.slice(0, 20).map((f) => (
                    <li key={f}>
                      <code className="confirm-dialog-path">{f}</code>
                    </li>
                  ))}
                  {plan.data.files.length > 20 && <li>{t('and {count} more', { count: plan.data.files.length - 20 })}</li>}
                </ul>
              )}
              <p>{t('Folders it leaves empty go too. You can’t undo this.')}</p>
            </>
          )}
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose} disabled={busy} autoFocus>
            {t('Keep it')}
          </button>
          <AsyncButton className="button danger" onClick={confirm} busy={busy} disabled={!plan.data}>
            {t('{kind, select, movie {Delete movie} book {Delete book} artist {Delete artist} album {Delete album} show {Delete show} other {Delete episode}}', { kind: what.noun })}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}
