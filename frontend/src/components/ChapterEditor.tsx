import { useState } from 'react'
import { ApiError, saveChapters, shiftChapters, takeAudibleChapters, type BookChapter } from '../api/client'
import { useModal } from '../hooks/useModal'
import { parseClock } from '../lib/books'
import { formatClock } from '../lib/format'
import { AsyncButton } from './AsyncButton'
import { Close, Plus } from './icons'

interface Draft {
  key: number
  title: string
  start: string
}

/**
 * A book's chapters for the main profile to put right: rename them, move where each starts, add or remove one, shift
 * them all by some seconds (the first stays at the start), or take Audible's. Saved chapters stay through rescans.
 */
export function ChapterEditor({
  bookId,
  chapters,
  hasAsin,
  onSaved,
  onClose,
}: {
  bookId: string
  chapters: BookChapter[]
  hasAsin: boolean
  onSaved: () => void
  onClose: () => void
}) {
  const [drafts, setDrafts] = useState<Draft[]>(() => chapters.map((c, i) => ({ key: i, title: c.title, start: formatClock(c.startSeconds) })))
  const [shift, setShift] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const dialog = useModal<HTMLDivElement>(onClose, !busy)

  const reset = (saved: BookChapter[]) => setDrafts(saved.map((c, i) => ({ key: i, title: c.title, start: formatClock(c.startSeconds) })))
  const update = (key: number, change: Partial<Draft>) => setDrafts((d) => d.map((x) => (x.key === key ? { ...x, ...change } : x)))

  async function run(work: () => Promise<BookChapter[]>) {
    setBusy(true)
    setError(undefined)
    try {
      reset(await work())
      onSaved()
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 404 && !e.messages[0]
          ? 'This server can’t edit chapters yet.'
          : e instanceof ApiError && e.messages[0]
            ? e.messages[0]
            : String(e),
      )
    } finally {
      setBusy(false)
    }
  }

  const parsed = drafts.map((d) => ({ title: d.title.trim(), startSeconds: parseClock(d.start) }))
  const invalid = parsed.some((p) => !p.title || p.startSeconds === null) || new Set(parsed.map((p) => p.startSeconds)).size !== parsed.length
  const seconds = Number(shift)
  const canShift = shift.trim() !== '' && Number.isFinite(seconds) && seconds !== 0 && Math.abs(seconds) <= 600

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel chapter-editor" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="chapter-editor-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="chapter-editor-title">Chapters</h2>
        <ol className="chapter-edit-list">
          {drafts.map((d, i) => (
            <li key={d.key}>
              <span className="muted chapter-edit-number">{i + 1}</span>
              <input type="text" aria-label={`Chapter ${i + 1} title`} value={d.title} disabled={busy} onChange={(e) => update(d.key, { title: e.target.value })} />
              <input
                type="text"
                inputMode="numeric"
                className="chapter-edit-start"
                aria-label={`Chapter ${i + 1} start`}
                aria-invalid={parseClock(d.start) === null || undefined}
                value={d.start}
                disabled={busy}
                onChange={(e) => update(d.key, { start: e.target.value })}
              />
              <button
                type="button"
                className="icon-button"
                aria-label={`Remove chapter ${i + 1}`}
                disabled={busy || drafts.length === 1}
                onClick={() => setDrafts((all) => all.filter((x) => x.key !== d.key))}
              >
                <Close />
              </button>
            </li>
          ))}
        </ol>
        <button
          type="button"
          className="text-button"
          disabled={busy}
          onClick={() => setDrafts((all) => [...all, { key: Math.max(-1, ...all.map((x) => x.key)) + 1, title: `Chapter ${all.length + 1}`, start: all.at(-1)?.start ?? '0:00' }])}
        >
          <Plus /> Add a chapter
        </button>

        <div className="chapter-edit-tools">
          <label>
            Shift all by
            <input type="number" min={-600} max={600} step={0.5} value={shift} placeholder="seconds" disabled={busy} onChange={(e) => setShift(e.target.value)} />
          </label>
          <AsyncButton className="button quiet small" disabled={!canShift || busy} onClick={() => run(() => shiftChapters(bookId, seconds))}>
            Shift
          </AsyncButton>
          {hasAsin && (
            <AsyncButton className="button quiet small" disabled={busy} onClick={() => run(() => takeAudibleChapters(bookId, canShift ? seconds : 0))}>
              Use Audible’s
            </AsyncButton>
          )}
        </div>

        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose} disabled={busy}>
            Done
          </button>
          <AsyncButton
            className="button"
            disabled={invalid || busy}
            onClick={() => run(() => saveChapters(bookId, parsed.map((p) => ({ title: p.title, startSeconds: p.startSeconds! }))))}
          >
            Save
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}
