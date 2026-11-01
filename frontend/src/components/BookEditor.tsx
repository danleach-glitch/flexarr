import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { ApiError, editBook, type ItemDetail } from '../api/client'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'

/** "Richard Osman, Lesley Manville" → each name once, as typed. */
const names = (text: string) => [...new Set(text.split(',').map((n) => n.trim()).filter(Boolean))]

/**
 * The main profile's fixes to a book: its title, people, series, genres, description and year, and whether they're
 * locked so rescans and refreshes keep them. Changing anything locks it, unless the switch was set by hand.
 */
export function BookEditor({ detail, onSaved, onClose }: { detail: ItemDetail; onSaved: () => void; onClose: () => void }) {
  const { item } = detail
  const people = (role: string) => detail.credits.filter((c) => c.role === role).map((c) => c.name).join(', ')
  const firstSeries = detail.book?.series[0]
  const [title, setTitle] = useState(item.title)
  const [subtitle, setSubtitle] = useState(detail.book?.subtitle ?? '')
  const [authors, setAuthors] = useState(people('Author'))
  const [narrators, setNarrators] = useState(people('Narrator'))
  const [series, setSeries] = useState(firstSeries?.name ?? '')
  const [sequence, setSequence] = useState(firstSeries?.sequence ?? '')
  const [genres, setGenres] = useState(detail.genres)
  const [genre, setGenre] = useState('')
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

  function addGenre() {
    const name = genre.trim()
    if (name && !genres.some((g) => g.toLowerCase() === name.toLowerCase())) change(setGenres)([...genres, name])
    setGenre('')
  }

  function genreKey(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault()
      addGenre()
    }
  }

  async function save(e?: FormEvent) {
    e?.preventDefault()
    if (!title.trim()) return setError('A book needs a title.')
    const parsedYear = year.trim() ? Number(year) : null
    if (parsedYear !== null && !(Number.isInteger(parsedYear) && parsedYear > 0 && parsedYear <= 3000)) return setError('A year such as 2016.')
    // Other series it's in stay as they are; the first is the one edited here.
    const others = (detail.book?.series ?? []).slice(1).map((s) => ({ name: s.name, sequence: s.sequence }))
    const pending = genre.trim() && !genres.some((g) => g.toLowerCase() === genre.trim().toLowerCase()) ? [...genres, genre.trim()] : genres
    setSaving(true)
    setError(undefined)
    try {
      await editBook(item.id, {
        title: title.trim(),
        subtitle: subtitle.trim() || null,
        authors: names(authors),
        narrators: names(narrators),
        series: [...(series.trim() ? [{ name: series.trim(), sequence: sequence.trim() || null }] : []), ...others],
        genres: pending,
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
        <div className="book-editor-fields">
          <label className="field wide">
            <span className="field-label">Title</span>
            <input value={title} onChange={(e) => change(setTitle)(e.target.value)} disabled={saving} autoFocus required maxLength={500} />
          </label>
          <label className="field wide">
            <span className="field-label">Subtitle</span>
            <input value={subtitle} onChange={(e) => change(setSubtitle)(e.target.value)} disabled={saving} maxLength={500} />
          </label>
          <label className="field wide">
            <span className="field-label">Authors</span>
            <input value={authors} onChange={(e) => change(setAuthors)(e.target.value)} disabled={saving} placeholder="Separated by commas" />
          </label>
          <label className="field wide">
            <span className="field-label">Narrators</span>
            <input value={narrators} onChange={(e) => change(setNarrators)(e.target.value)} disabled={saving} placeholder="Separated by commas" />
          </label>
          <label className="field">
            <span className="field-label">Series</span>
            <input value={series} onChange={(e) => change(setSeries)(e.target.value)} disabled={saving} maxLength={200} />
          </label>
          <label className="field narrow">
            <span className="field-label">Number</span>
            <input value={sequence} onChange={(e) => change(setSequence)(e.target.value)} disabled={saving || !series.trim()} inputMode="decimal" maxLength={20} />
          </label>
          <div className="field wide">
            <span className="field-label" id="book-editor-genres">
              Genres
            </span>
            <div className="book-editor-genres" role="group" aria-labelledby="book-editor-genres">
              {genres.map((g) => (
                <button key={g} type="button" className="chip" disabled={saving} aria-label={`Remove ${g}`} onClick={() => change(setGenres)(genres.filter((x) => x !== g))}>
                  {g} ×
                </button>
              ))}
              <input value={genre} onChange={(e) => setGenre(e.target.value)} onKeyDown={genreKey} onBlur={addGenre} disabled={saving} placeholder="Add a genre" aria-label="Add a genre" maxLength={200} />
            </div>
          </div>
          <label className="field wide">
            <span className="field-label">Description</span>
            <textarea value={overview} rows={6} onChange={(e) => change(setOverview)(e.target.value)} disabled={saving} />
          </label>
          <label className="field narrow">
            <span className="field-label">Year</span>
            <input value={year} onChange={(e) => change(setYear)(e.target.value)} disabled={saving} inputMode="numeric" maxLength={4} />
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
