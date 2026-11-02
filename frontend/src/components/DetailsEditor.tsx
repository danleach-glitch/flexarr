import { useState, type FormEvent } from 'react'
import { ApiError, editAlbum, editDetails, resetDetails, type ItemDetail } from '../api/client'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'
import { NameList } from './NameList'
import { automaticSort, withName } from '../lib/names'

/** An album's kinds of release, as its page names them, with a plain album first. */
const albumTypes: [string, string][] = [
  ['album', 'Album'],
  ['ep', 'EP'],
  ['single', 'Single'],
  ['live', 'Live'],
  ['compilation', 'Compilation'],
  ['soundtrack', 'Soundtrack'],
  ['other', 'Other'],
]

/**
 * The main profile's fixes to a film's, show's or album's details, and whether they're locked so refreshes keep
 * them. Changing anything locks it, unless the switch was set by hand. When they're locked, **Back to automatic**
 * hands them back to the sources. The age rating isn't here: kids access is Allow and Block for kids.
 */
export function DetailsEditor({ detail, onSaved, onClose }: { detail: ItemDetail; onSaved: () => void; onClose: () => void }) {
  const { item } = detail
  const album = item.kind === 'Album'
  const [title, setTitle] = useState(item.title)
  // Only a sort title typed by hand shows: the one worked out from the title is the box's placeholder.
  const [sortTitle, setSortTitle] = useState(detail.sortTitle && detail.sortTitle !== automaticSort(item.title) ? detail.sortTitle : '')
  const [originalTitle, setOriginalTitle] = useState(album ? '' : (detail.originalTitle ?? ''))
  const [year, setYear] = useState(item.year?.toString() ?? '')
  const [studio, setStudio] = useState(detail.studio ?? '')
  const [albumType, setAlbumType] = useState<string>(item.albumType ?? 'album')
  const [genres, setGenres] = useState(detail.genres)
  const [typedGenre, setTypedGenre] = useState('')
  const [overview, setOverview] = useState(detail.overview ?? '')
  const [locked, setLocked] = useState(detail.detailsLocked === true)
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

  async function run(work: () => Promise<unknown>) {
    setSaving(true)
    setError(undefined)
    try {
      await work()
      onSaved()
      onClose()
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  function save(e?: FormEvent) {
    e?.preventDefault()
    if (!title.trim()) return setError(`${album ? 'An album' : item.kind === 'Movie' ? 'A movie' : 'A show'} needs a title.`)
    const parsedYear = year.trim() ? Number(year) : null
    if (parsedYear !== null && !(Number.isInteger(parsedYear) && parsedYear >= 1800 && parsedYear <= 3000)) return setError('A year such as 1999.')
    const allGenres = withName(genres, typedGenre)
    void run(() =>
      album
        ? editAlbum(item.id, { title: title.trim(), year: parsedYear, genres: allGenres, albumType, locked })
        : editDetails(item.id, {
            title: title.trim(),
            sortTitle: sortTitle.trim() || null,
            originalTitle: originalTitle.trim() || null,
            year: parsedYear,
            overview: overview.trim() || null,
            genres: allGenres,
            studio: studio.trim() || null,
            locked,
          }),
    )
  }

  return (
    <div className="dialog-backdrop" onClick={() => !saving && onClose()}>
      <form className="panel book-editor" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="details-editor-title" onClick={(e) => e.stopPropagation()} onSubmit={save}>
        <h2 id="details-editor-title">
          Edit {item.title}
          {detail.detailsLocked && <span className="tag keep details-edited">Edited</span>}
        </h2>
        <div className="book-editor-body">
          <div className="book-editor-fields">
            <label className="field wide">
              <span className="field-label">Title</span>
              <input type="text" value={title} onChange={(e) => change(setTitle)(e.target.value)} disabled={saving} autoFocus required maxLength={500} />
            </label>
            {!album && (
              <>
                <label className="field">
                  <span className="field-label">Sort as</span>
                  <input type="text" value={sortTitle} onChange={(e) => change(setSortTitle)(e.target.value)} disabled={saving} maxLength={500} placeholder="From the title" />
                </label>
                <label className="field">
                  <span className="field-label">Original title</span>
                  <input type="text" value={originalTitle} onChange={(e) => change(setOriginalTitle)(e.target.value)} disabled={saving} maxLength={500} />
                </label>
              </>
            )}
            <label className="field narrow">
              <span className="field-label">Year</span>
              <input type="text" value={year} onChange={(e) => change(setYear)(e.target.value)} disabled={saving} inputMode="numeric" maxLength={4} />
            </label>
            {album ? (
              <label className="field">
                <span className="field-label">Type</span>
                <select value={albumType} onChange={(e) => change(setAlbumType)(e.target.value)} disabled={saving}>
                  {albumTypes.map(([value, label]) => (
                    <option key={value} value={value}>
                      {label}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <label className="field">
                <span className="field-label">{item.kind === 'Movie' ? 'Studio' : 'Network'}</span>
                <input type="text" value={studio} onChange={(e) => change(setStudio)(e.target.value)} disabled={saving} maxLength={200} />
              </label>
            )}
            <NameList label="Genres" names={genres} typed={typedGenre} disabled={saving} onType={setTypedGenre} onChange={change(setGenres)} />
            {!album && (
              <label className="field wide">
                <span className="field-label">Description</span>
                <textarea value={overview} rows={6} onChange={(e) => change(setOverview)(e.target.value)} disabled={saving} />
              </label>
            )}
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
          {detail.detailsLocked && (
            <AsyncButton type="button" className="text-button fix-match-reset" disabled={saving} onClick={() => run(() => resetDetails(item.id))}>
              Back to automatic
            </AsyncButton>
          )}
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
