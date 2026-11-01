import { useEffect, useState, type FormEvent, type KeyboardEvent, type ReactNode } from 'react'
import { ApiError, getPersonCandidates, updatePerson, uploadPersonImage, type Person, type PersonUpdate } from '../api/client'
import { useApi } from '../hooks/useApi'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'
import { safeImageSrc } from '../lib/safeImageSrc'
import { FilePreview } from './FilePreview'

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

/** The server keeps the photos it found for ten minutes; a dialog open longer asks again before they're gone. */
const LOOK_AGAIN_MS = 9 * 60_000

/**
 * An admin's fixes to a person: which photo (one the server found, or an upload), the biography, and whether it's
 * locked against refreshes. Choosing a photo or changing the biography locks it, as the server does.
 */
export function PersonEditor({ person, onSaved, onClose }: { person: Person; onSaved: (person: Person) => void; onClose: () => void }) {
  const candidates = useApi((signal) => getPersonCandidates(person.id, signal), [person.id])
  // The photo shown now: the one the server marks, or (before it marked them) the one from the person's photo source.
  const current = candidates.data?.photos.find((p) => p.current)?.source ?? person.photoSource ?? undefined
  const [picked, setPicked] = useState<string>()
  const chosen = picked ?? current
  const [file, setFile] = useState<File>()
  // The file once it's saved, so a save that then fails on the rest doesn't send it again.
  const [uploaded, setUploaded] = useState<File>()
  const original = person.biography ?? ''
  const [biography, setBiography] = useState(original)
  const [locked, setLocked] = useState(person.locked ?? false)
  // Once the admin sets the switch themselves, choosing a photo or writing no longer turns it on for them.
  const [lockSet, setLockSet] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string>()
  const dialog = useModal<HTMLFormElement>(onClose, !saving)
  // Photos that didn't load (the source has gone, or the server forgot them): hidden, so they can't be chosen.
  const [broken, setBroken] = useState<ReadonlySet<string>>(new Set())

  const { reload } = candidates
  useEffect(() => {
    const timer = setInterval(() => {
      setBroken(new Set())
      reload()
    }, LOOK_AGAIN_MS)
    return () => clearInterval(timer)
  }, [reload])

  const photos = (candidates.data?.photos ?? []).filter((p) => !broken.has(p.source))
  const biographies = (candidates.data?.biographies ?? []).filter((b) => b.text.trim() !== biography.trim())

  function choose(source: string) {
    setPicked(source)
    if (source !== current && !lockSet) setLocked(true)
  }

  // One Tab stop for the photos (the chosen one, else the first); the arrow keys move between them and choose, as radios do.
  const chosenShown = chosen === FILE ? !!file : photos.some((p) => p.source === chosen)
  function moveChoice(e: KeyboardEvent<HTMLDivElement>) {
    const step = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key]
    const radios = [...e.currentTarget.querySelectorAll<HTMLButtonElement>('[role="radio"]:not(:disabled)')]
    const at = radios.indexOf(document.activeElement as HTMLButtonElement)
    if (at < 0 || radios.length === 0) return
    const to = e.key === 'Home' ? 0 : e.key === 'End' ? radios.length - 1 : step ? (at + step + radios.length) % radios.length : -1
    if (to < 0) return
    e.preventDefault()
    radios[to].focus()
    radios[to].click()
  }

  function failed(source: string) {
    setBroken((b) => new Set(b).add(source))
    if (picked === source) setPicked(undefined)
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
    const upload = chosen === FILE && file && file !== uploaded ? file : undefined
    if (!upload && Object.keys(update).length === 0) return onClose()

    setSaving(true)
    setError(undefined)
    try {
      if (upload) {
        const saved = await uploadPersonImage(person.id, upload)
        setUploaded(upload)
        // The page behind shows the new photo even if the rest fails.
        onSaved(saved)
      }
      if (Object.keys(update).length > 0) onSaved(await updatePerson(person.id, update))
      onClose()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // The photo chosen has gone from the server's memory: show what it finds now.
        setError('That photo’s gone. Choose again.')
        setPicked(undefined)
        setBroken(new Set())
        reload()
      } else setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
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

        <div className="person-editor-photos">
          {candidates.loading && !candidates.data && <p className="muted">Loading…</p>}
          {loadError && <p className="error">{loadError}</p>}
          {candidates.data && photos.length === 0 && !file && <p className="muted">No photos found.</p>}
          <div className="person-editor-choices" role="radiogroup" aria-label="Photo" aria-busy={candidates.loading} onKeyDown={moveChoice}>
            {photos.map((p, i) => (
              <PhotoChoice
                key={p.source}
                picture={<img src={safeImageSrc(p.image)} alt="" onError={() => failed(p.source)} />}
                label={sourceName(p.source)}
                current={p.source === current}
                checked={chosen === p.source}
                tabbable={chosen === p.source || (!chosenShown && i === 0)}
                disabled={saving}
                onChoose={() => choose(p.source)}
              />
            ))}
            {file && (
              <PhotoChoice picture={<FilePreview file={file} />} label="Yours" current={false} checked={chosen === FILE} tabbable={chosen === FILE || (!chosenShown && photos.length === 0)} disabled={saving} onChoose={() => choose(FILE)} />
            )}
          </div>
          <label className="person-editor-upload file-button">
            Upload
            <input
              type="file"
              accept="image/jpeg,image/png,image/webp"
              disabled={saving}
              onChange={(e) => {
                const upload = e.target.files?.[0]
                if (!upload) return
                setFile(upload)
                choose(FILE)
              }}
            />
          </label>
        </div>

        <label className="field">
          <span className="field-label">Biography</span>
          <textarea value={biography} rows={8} autoFocus onChange={(e) => write(e.target.value)} disabled={saving} />
        </label>
        {biographies.length > 0 && (
          <p className="person-editor-sources">
            <span className="muted">Use</span>
            {biographies.map((b) => (
              <button key={b.source} type="button" className="text-button" disabled={saving} aria-label={`Use ${sourceName(b.source)}’s biography`} onClick={() => write(b.text)}>
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
  picture,
  label,
  current,
  checked,
  tabbable,
  disabled,
  onChoose,
}: {
  picture: ReactNode
  label: string
  current: boolean
  checked: boolean
  tabbable: boolean
  disabled: boolean
  onChoose: () => void
}) {
  return (
    <button type="button" role="radio" aria-checked={checked} tabIndex={tabbable ? 0 : -1} className="person-editor-photo" disabled={disabled} onClick={onChoose}>
      {picture}
      <span>
        {label}
        {current && <span className="tag keep">Current</span>}
      </span>
    </button>
  )
}
