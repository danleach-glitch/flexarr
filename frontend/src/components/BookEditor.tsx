import { useId, useState, type FormEvent, type KeyboardEvent } from 'react'
import { ApiError, editBook, type ItemDetail } from '../api/client'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'

/** The list with `name` added, unless it's empty or already there (in any case). */
const withName = (list: string[], name: string) => (name.trim() && !list.some((n) => n.toLowerCase() === name.trim().toLowerCase()) ? [...list, name.trim()] : list)

/**
 * The main profile's fixes to a book: its title, people, series, genres, description and year, and whether they're
 * locked so rescans and refreshes keep them. Changing anything locks it, unless the switch was set by hand.
 */
export function BookEditor({ detail, onSaved, onClose }: { detail: ItemDetail; onSaved: () => void; onClose: () => void }) {
  const { item } = detail
  const people = (role: string) => detail.credits.filter((c) => c.role === role).map((c) => c.name)
  const firstSeries = detail.book?.series[0]
  const [title, setTitle] = useState(item.title)
  const [subtitle, setSubtitle] = useState(detail.book?.subtitle ?? '')
  const [authors, setAuthors] = useState(people('Author'))
  const [narrators, setNarrators] = useState(people('Narrator'))
  const [series, setSeries] = useState(firstSeries?.name ?? '')
  const [sequence, setSequence] = useState(firstSeries?.sequence ?? '')
  const [genres, setGenres] = useState(detail.genres)
  // What's typed in each list's box and not yet added: saving adds it too.
  const [typed, setTyped] = useState({ authors: '', narrators: '', genres: '' })
  const [overview, setOverview] = useState(detail.overview ?? '')
  const [year, setYear] = useState(item.year?.toString() ?? '')
  const [locked, setLocked] = useState(detail.matchLocked)
  const [lockSet, setLockSet] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string>()
  const dialog = useModal<HTMLFormElement>(onClose, !saving)

  /** A change: what's typed, and the lock turned on unless it was set by hand. */
  const change =
    <T,>(set: (value: T) => void) =>
    (value: T) => {
      set(value)
      if (!lockSet) setLocked(true)
    }

  async function save(e?: FormEvent) {
    e?.preventDefault()
    if (!title.trim()) return setError('A book needs a title.')
    const parsedYear = year.trim() ? Number(year) : null
    if (parsedYear !== null && !(Number.isInteger(parsedYear) && parsedYear > 0 && parsedYear <= 3000)) return setError('A year such as 2016.')
    // Other series it's in stay as they are; the first is the one edited here.
    const others = (detail.book?.series ?? []).slice(1).map((s) => ({ name: s.name, sequence: s.sequence }))
    setSaving(true)
    setError(undefined)
    try {
      await editBook(item.id, {
        title: title.trim(),
        subtitle: subtitle.trim() || null,
        authors: withName(authors, typed.authors),
        narrators: withName(narrators, typed.narrators),
        series: [...(series.trim() ? [{ name: series.trim(), sequence: sequence.trim() || null }] : []), ...others],
        genres: withName(genres, typed.genres),
        overview: overview.trim() || null,
        year: parsedYear,
        locked,
      })
      onSaved()
      onClose()
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  return (
    <div className="dialog-backdrop" onClick={() => !saving && onClose()}>
      <form className="panel book-editor" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="book-editor-title" onClick={(e) => e.stopPropagation()} onSubmit={save}>
        <h2 id="book-editor-title">Edit {item.title}</h2>
        <div className="book-editor-body">
          <div className="book-editor-fields">
            <label className="field wide">
              <span className="field-label">Title</span>
              <input type="text" value={title} onChange={(e) => change(setTitle)(e.target.value)} disabled={saving} autoFocus required maxLength={500} />
            </label>
            <label className="field">
              <span className="field-label">Subtitle</span>
              <input type="text" value={subtitle} onChange={(e) => change(setSubtitle)(e.target.value)} disabled={saving} maxLength={500} />
            </label>
            <label className="field narrow">
              <span className="field-label">Year</span>
              <input type="text" value={year} onChange={(e) => change(setYear)(e.target.value)} disabled={saving} inputMode="numeric" maxLength={4} />
            </label>
            <NameList label="Authors" names={authors} typed={typed.authors} disabled={saving} onType={(t) => setTyped({ ...typed, authors: t })} onChange={change(setAuthors)} />
            <NameList label="Narrators" names={narrators} typed={typed.narrators} disabled={saving} onType={(t) => setTyped({ ...typed, narrators: t })} onChange={change(setNarrators)} />
            <label className="field">
              <span className="field-label">Series</span>
              <input type="text" value={series} onChange={(e) => change(setSeries)(e.target.value)} disabled={saving} maxLength={200} />
            </label>
            <label className="field narrow">
              <span className="field-label">Number</span>
              <input type="text" value={sequence} onChange={(e) => change(setSequence)(e.target.value)} disabled={saving || !series.trim()} inputMode="decimal" maxLength={20} />
            </label>
            <NameList label="Genres" names={genres} typed={typed.genres} disabled={saving} onType={(t) => setTyped({ ...typed, genres: t })} onChange={change(setGenres)} />
            <label className="field wide">
              <span className="field-label">Description</span>
              <textarea value={overview} rows={6} onChange={(e) => change(setOverview)(e.target.value)} disabled={saving} />
            </label>
          </div>

          <label className="person-editor-lock">
            <span>Locked</span>
            <input
              type="checkbox"
              role="switch"
              className="switch"
              checked={locked}
              disabled={saving}
              onChange={(e) => {
                setLocked(e.target.checked)
                setLockSet(true)
              }}
            />
          </label>
        </div>

        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button type="button" className="button quiet" onClick={onClose} disabled={saving}>
            Cancel
          </button>
          <AsyncButton className="button" type="submit" busy={saving}>
            Save
          </AsyncButton>
        </div>
      </form>
    </div>
  )
}

/**
 * Names as chips, each with a way to remove it, and a box to add one (Enter or Tab adds it; saving adds what's left
 * typed). Names can hold commas ("Martin Luther King, Jr."), so nothing is split.
 */
function NameList({ label, names, typed, disabled, onType, onChange }: { label: string; names: string[]; typed: string; disabled: boolean; onType: (text: string) => void; onChange: (names: string[]) => void }) {
  const id = useId()
  function add() {
    const next = withName(names, typed)
    if (next !== names) onChange(next)
    onType('')
  }
  function key(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter' && typed.trim()) {
      e.preventDefault()
      add()
    }
  }
  const one = label === 'Genres' ? 'genre' : label === 'Authors' ? 'author' : 'narrator'
  const article = one === 'author' ? 'an' : 'a'
  return (
    <div className="field wide">
      <span className="field-label" id={id}>
        {label}
      </span>
      <div className="book-editor-names" role="group" aria-labelledby={id}>
        {names.map((n) => (
          <button key={n} type="button" className="chip" disabled={disabled} aria-label={`Remove ${n}`} onClick={() => onChange(names.filter((x) => x !== n))}>
            {n} ×
          </button>
        ))}
        <input type="text" value={typed} onChange={(e) => onType(e.target.value)} onKeyDown={key} onBlur={add} disabled={disabled} placeholder={`Add ${names.length ? 'another' : article} ${one}`} aria-label={`Add ${article} ${one}`} maxLength={200} />
      </div>
    </div>
  )
}
