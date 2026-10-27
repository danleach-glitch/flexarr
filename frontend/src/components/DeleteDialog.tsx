import { useEffect, useState } from 'react'
import { ApiError, deleteItem, getDeletion, type ItemSummary } from '../api/client'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { episodeCode } from '../lib/format'

/**
 * Confirms deleting a film, show or episode for good. Radarr or Sonarr delete the files from disk when they manage it;
 * otherwise Flexarr does, and the dialog lists the files first.
 */
export function DeleteDialog({ item, onDeleted, onClose }: { item: ItemSummary; onDeleted: () => void; onClose: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const plan = useApi((signal) => getDeletion(item.id, signal), [item.id])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && !busy && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [busy, onClose])

  const what =
    item.kind === 'Movie'
      ? { name: item.title, noun: 'film', detail: 'Radarr deletes the film’s files from disk and stops tracking it.' }
      : item.kind === 'Series'
        ? { name: item.title, noun: 'show', detail: 'Sonarr deletes the files of every episode from disk and stops tracking the show.' }
        : {
            name: `${item.seriesTitle ?? ''} ${episodeCode(item)} ${item.title}`.trim(),
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
      <div className="panel confirm-dialog" role="alertdialog" aria-modal="true" aria-labelledby="delete-title" aria-describedby="delete-detail" onClick={(e) => e.stopPropagation()}>
        <h2 id="delete-title">Delete {what.name}?</h2>
        <div id="delete-detail" className="confirm-dialog-detail">
          {plan.loading && !plan.data && <p>Checking how to delete it…</p>}
          {plan.error && <p className="error">Couldn’t check how to delete it: {plan.error instanceof ApiError && plan.error.messages[0] ? plan.error.messages[0] : plan.error.message}</p>}
          {plan.data?.managedBy && <p>{what.detail} You can’t undo this.</p>}
          {plan.data && !plan.data.managedBy && (
            <>
              <p>
                {item.kind === 'Movie' ? 'Radarr' : 'Sonarr'} doesn’t manage this {what.noun}, so Flexarr deletes{' '}
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
