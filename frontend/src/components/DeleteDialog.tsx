import { useEffect, useState } from 'react'
import { ApiError, deleteItem, type ItemSummary } from '../api/client'
import { episodeCode } from '../lib/format'

/** Confirms deleting a film, show or episode for good: Radarr or Sonarr delete the files from disk. */
export function DeleteDialog({ item, onDeleted, onClose }: { item: ItemSummary; onDeleted: () => void; onClose: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

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
        <p id="delete-detail">
          {what.detail} You can’t undo this.
        </p>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose} disabled={busy} autoFocus>
            Keep it
          </button>
          <button className="button danger" onClick={confirm} disabled={busy}>
            {busy ? 'Deleting…' : `Delete ${what.noun}`}
          </button>
        </div>
      </div>
    </div>
  )
}
