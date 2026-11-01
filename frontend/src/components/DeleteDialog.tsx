import { useState } from 'react'
import { ApiError, deleteItem, getDeletion, type ItemSummary } from '../api/client'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { arrFor } from '../lib/apps'
import { episodeCode } from '../lib/format'
import { useModal } from '../hooks/useModal'

/**
 * Confirms deleting a film, show, episode or book for good. Radarr or Sonarr delete the files from disk when they manage it;
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
      ? { name: item.title, noun: 'film', detail: 'Radarr deletes the film’s files from disk and stops tracking it.' }
      : item.kind === 'Audiobook'
        ? { name: item.title, noun: 'book', detail: '' } // no app manages books: the file list below says it all
      : item.kind === 'Series'
        ? { name: item.title, noun: 'show', detail: 'Sonarr deletes the files of every episode from disk and stops tracking the show.' }
        : {
            name: [item.seriesTitle, episodeCode(item), item.title].filter(Boolean).join(' '),
            noun: 'episode',
            detail: 'Sonarr deletes this episode’s file from disk and stops downloading it.',
          }

  async function confirm() {
    setBusy(true)
    setError(undefined)
    try {
      await deleteItem(item.id)
      onDeleted()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      setBusy(false)
    }
  }

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel confirm-dialog" ref={dialog} role="alertdialog" aria-modal="true" aria-labelledby="delete-title" aria-describedby="delete-detail" onClick={(e) => e.stopPropagation()}>
        <h2 id="delete-title">Delete {what.name}?</h2>
        <div id="delete-detail" className="confirm-dialog-detail">
          {plan.loading && !plan.data && <p>Checking how to delete it…</p>}
          {plan.error && <p className="error">Couldn’t check how to delete it: {plan.error instanceof ApiError && plan.error.messages[0] ? plan.error.messages[0] : plan.error.message}</p>}
          {plan.data?.managedBy && <p>{what.detail} You can’t undo this.</p>}
          {plan.data && !plan.data.managedBy && (
            <>
              <p>
                {app ? `${app} doesn’t manage this ${what.noun}, so Flexarr deletes` : 'Flexarr deletes'}{' '}
                {plan.data.files.length === 0 ? 'it from the library: there are no files on disk.' : plan.data.files.length === 1 ? 'its file from disk:' : `its ${plan.data.files.length} files from disk:`}
              </p>
              {plan.data.files.length > 0 && (
                <ul className="delete-files">
                  {plan.data.files.slice(0, 20).map((f) => (
                    <li key={f}>
                      <code className="confirm-dialog-path">{f}</code>
                    </li>
                  ))}
                  {plan.data.files.length > 20 && <li>and {plan.data.files.length - 20} more</li>}
                </ul>
              )}
              <p>Folders it leaves empty go too. You can’t undo this.</p>
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
            Keep it
          </button>
          <AsyncButton className="button danger" onClick={confirm} busy={busy} disabled={!plan.data}>
            {`Delete ${what.noun}`}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}
