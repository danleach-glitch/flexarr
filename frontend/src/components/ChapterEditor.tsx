import { useState } from 'react'
import { ApiError, audibleChapters, saveChapters, type BookChapter } from '../api/client'
import { useModal } from '../hooks/useModal'
import { clockText, parseClock } from '../lib/books'
import { AsyncButton } from './AsyncButton'
import { Close, Plus } from './icons'

interface Draft {
  key: number
  title: string
  start: string
}

const draftsOf = (chapters: BookChapter[]): Draft[] => chapters.map((c, i) => ({ key: i, title: c.title, start: clockText(c.startSeconds) }))

/** Every chapter but the first (which starts the book) moved by `seconds`, none before the start. */
function shiftDrafts(drafts: Draft[], seconds: number): Draft[] {
  const first = drafts.reduce<Draft | null>((min, d) => (min === null || (parseClock(d.start) ?? Infinity) < (parseClock(min.start) ?? Infinity) ? d : min), null)
  return drafts.map((d) => {
    const start = parseClock(d.start)
    return d === first || start === null ? d : { ...d, start: clockText(Math.max(0, start + seconds)) }
  })
}

/**
 * A book's chapters for the main profile to put right: rename them, move where each starts, add or remove one, shift
 * them all by some seconds (the first stays at the start), or fill in Audible's, all saved together with Save.
 * Saved chapters stay through rescans.
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
  const [drafts, setDrafts] = useState<Draft[]>(() => draftsOf(chapters))
  // What the server has, to tell edits from it.
  const [saved, setSaved] = useState<Draft[]>(() => draftsOf(chapters))
  const [shift, setShift] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [leaving, setLeaving] = useState(false)

  const changed = JSON.stringify(drafts.map(({ title, start }) => [title, start])) !== JSON.stringify(saved.map(({ title, start }) => [title, start]))
  // Done (or Escape, or a click outside) with edits not saved asks first.
  const close = () => {
    if (busy) return
    if (changed && !leaving) setLeaving(true)
    else onClose()
  }
  const dialog = useModal<HTMLDivElement>(close, !busy)

  const update = (key: number, change: Partial<Draft>) => {
    setLeaving(false)
    setDrafts((d) => d.map((x) => (x.key === key ? { ...x, ...change } : x)))
  }

  async function run(work: () => Promise<BookChapter[]>) {
    setBusy(true)
    setError(undefined)
    try {
      const result = draftsOf(await work())
      setDrafts(result)
      setSaved(result)
      setLeaving(false)
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

  // Audible's chapters fill the form, in place of what's there; Save sends them, and Discard changes gets these back.
  async function fillFromAudible() {
    setBusy(true)
    setError(undefined)
    try {
      const chapters = await audibleChapters(bookId)
      setLeaving(false)
      setDrafts(draftsOf(chapters).map((d, i) => ({ ...d, key: Math.max(-1, ...drafts.map((x) => x.key)) + 1 + i })))
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  const parsed = drafts.map((d) => ({ title: d.title.trim(), startSeconds: parseClock(d.start) }))
  const starts = parsed.map((p) => p.startSeconds)
  const repeated = (start: number | null) => start !== null && starts.filter((s) => s === start).length > 1
  const anyRepeated = starts.some(repeated)
  const invalid = parsed.some((p) => !p.title || p.startSeconds === null) || anyRepeated
  const seconds = Number(shift)
  const canShift = shift.trim() !== '' && Number.isFinite(seconds) && seconds !== 0 && Math.abs(seconds) <= 600 && starts.every((s) => s !== null)

  return (
    <div className="dialog-backdrop" onClick={close}>
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
                aria-invalid={parseClock(d.start) === null || repeated(parseClock(d.start)) || undefined}
                value={d.start}
                disabled={busy}
                onChange={(e) => update(d.key, { start: e.target.value })}
              />
              <button
                type="button"
                className="icon-button"
                aria-label={`Remove chapter ${i + 1}`}
                disabled={busy || drafts.length === 1}
                onClick={() => {
                  setLeaving(false)
                  setDrafts((all) => all.filter((x) => x.key !== d.key))
                }}
              >
                <Close />
              </button>
            </li>
          ))}
        </ol>
        {anyRepeated && <p className="error">Two chapters start at the same time.</p>}
        <button
          type="button"
          className="text-button"
          disabled={busy}
          onClick={() => {
            setLeaving(false)
            setDrafts((all) => [...all, { key: Math.max(-1, ...all.map((x) => x.key)) + 1, title: `Chapter ${all.length + 1}`, start: all.at(-1)?.start ?? '0:00' }])
          }}
        >
          <Plus /> Add a chapter
        </button>

        <div className="chapter-edit-tools">
          <label>
            Shift all by
            <input type="number" min={-600} max={600} step={0.5} value={shift} placeholder="seconds" disabled={busy} onChange={(e) => setShift(e.target.value)} />
          </label>
          <button
            type="button"
            className="button quiet small"
            disabled={!canShift || busy}
            onClick={() => {
              setLeaving(false)
              setDrafts((all) => shiftDrafts(all, seconds))
              setShift('')
            }}
          >
            Shift
          </button>
          {hasAsin && (
            <AsyncButton className="button quiet small" disabled={busy} onClick={fillFromAudible}>
              Use Audible’s
            </AsyncButton>
          )}
        </div>

        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {leaving && (
          <p className="error" role="alert">
            Your changes aren’t saved.
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={close} disabled={busy}>
            {leaving ? 'Discard changes' : 'Done'}
          </button>
          <AsyncButton
            className="button"
            disabled={invalid || busy || !changed}
            onClick={() => run(() => saveChapters(bookId, parsed.map((p) => ({ title: p.title, startSeconds: p.startSeconds! }))))}
          >
            Save
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}
