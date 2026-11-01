import { useState, type FormEvent } from 'react'
import { ApiError, getPersonCandidates, updatePerson, uploadPersonImage, type Person, type PersonUpdate } from '../api/client'
import { useApi } from '../hooks/useApi'
import { useModal } from '../hooks/useModal'
import { usePreviewUrl } from '../hooks/usePreviewUrl'
import { AsyncButton } from './AsyncButton'

const sourceNames: Record<string, string> = {
  folder: 'Folder',
  readarr: 'Readarr',
  audible: 'Audible',
  openlibrary: 'Open Library',
  tmdb: 'TMDB',
  tvmaze: 'TVmaze',
  upload: 'Uploaded',
  manual: 'Typed',
}

/** A source's name to show; one this app doesn't know yet shows as it comes. */
const sourceName = (source: string) => sourceNames[source] ?? source.charAt(0).toUpperCase() + source.slice(1)

/** The chosen file, rather than one of the server's photos. */
const FILE = ' file'

/**
 * An admin's fixes to a person: which photo (one the server found, or an upload), the biography, and whether it's
 * locked against refreshes. Choosing a photo or changing the biography locks it, as the server does.
 */
export function PersonEditor({ person, onSaved, onClose }: { person: Person; onSaved: (person: Person) => void; onClose: () => void }) {
  const candidates = useApi((signal) => getPersonCandidates(person.id, signal), [person.id])
  const current = person.photoSource ?? undefined
  const [chosen, setChosen] = useState(current)
  const [file, setFile] = useState<File>()
  const preview = usePreviewUrl(file)
  const original = person.biography ?? ''
  const [biography, setBiography] = useState(original)
  const [locked, setLocked] = useState(person.locked ?? false)
  // Once the admin sets the switch themselves, choosing a photo or writing no longer turns it on for them.
  const [lockSet, setLockSet] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string>()
  const dialog = useModal<HTMLFormElement>(onClose, !saving)

  const photos = candidates.data?.photos ?? []
  const biographies = (candidates.data?.biographies ?? []).filter((b) => b.text.trim() !== biography.trim())

  function choose(source: string) {
    setChosen(source)
    if (source !== current && !lockSet) setLocked(true)
  }

  function write(text: string) {
    setBiography(text)
    if (text.trim() !== original.trim() && !lockSet) setLocked(true)
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    const update: PersonUpdate = {}
    if (chosen && chosen !== FILE && chosen !== current) update.photo = chosen
    const text = biography.trim()
    if (text !== original.trim()) update.biography = text || null
    const lockChanged = locked !== (chosen === FILE || (person.locked ?? false))
    if (lockChanged || update.photo !== undefined || update.biography !== undefined) update.locked = locked
    if (chosen !== FILE && Object.keys(update).length === 0) return onClose()

    setSaving(true)
    setError(undefined)
    try {
      let saved = chosen === FILE && file ? await uploadPersonImage(person.id, file) : person
      if (Object.keys(update).length > 0) saved = await updatePerson(person.id, update)
      onSaved(saved)
      onClose()
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  const loadError = candidates.error instanceof ApiError && candidates.error.messages[0] ? candidates.error.messages[0] : candidates.error?.message

  return (
    <div className="dialog-backdrop" onClick={() => !saving && onClose()}>
      <form
        className="panel person-editor"
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="person-editor-title"
        onClick={(e) => e.stopPropagation()}
        onSubmit={save}
      >
        <h2 id="person-editor-title">Edit {person.name}</h2>

        <div className="person-editor-photos" role="radiogroup" aria-label="Photo" aria-busy={candidates.loading}>
          {candidates.loading && !candidates.data && <p className="muted">Loading…</p>}
          {loadError && <p className="error">{loadError}</p>}
          {candidates.data && photos.length === 0 && !file && <p className="muted">No photos found.</p>}
          {photos.map((p) => (
            <PhotoChoice key={p.source} image={p.image} label={sourceName(p.source)} current={p.source === current} checked={chosen === p.source} disabled={saving} onChoose={() => choose(p.source)} />
          ))}
          {preview && <PhotoChoice image={preview} label="Yours" current={false} checked={chosen === FILE} disabled={saving} onChoose={() => choose(FILE)} />}
          <label className="person-editor-upload file-button">
            Upload
            <input
              type="file"
              accept="image/jpeg,image/png,image/webp"
              disabled={saving}
              onChange={(e) => {
                const picked = e.target.files?.[0]
                if (!picked) return
                setFile(picked)
                choose(FILE)
              }}
            />
          </label>
        </div>

        <label className="field">
          <span className="field-label">Biography</span>
          <textarea value={biography} rows={8} onChange={(e) => write(e.target.value)} disabled={saving} />
        </label>
        {biographies.length > 0 && (
          <p className="person-editor-sources">
            <span className="muted">Use</span>
            {biographies.map((b) => (
              <button key={b.source} type="button" className="text-button" disabled={saving} onClick={() => write(b.text)}>
                {sourceName(b.source)}
              </button>
            ))}
          </p>
        )}

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

function PhotoChoice({
  image,
  label,
  current,
  checked,
  disabled,
  onChoose,
}: {
  image: string
  label: string
  current: boolean
  checked: boolean
  disabled: boolean
  onChoose: () => void
}) {
  return (
    <button type="button" role="radio" aria-checked={checked} className="person-editor-photo" disabled={disabled} onClick={onChoose}>
      <img src={image} alt="" />
      <span>
        {label}
        {current && <span className="tag keep">Current</span>}
      </span>
    </button>
  )
}
