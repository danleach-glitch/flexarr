import { useState, type FormEvent } from 'react'
import { ApiError, createLibrary, deleteLibrary, scanLibrary, type Library, type LibraryType } from '../api/client'
import { FolderPicker } from '../components/FolderPicker'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { libraryTypeLabel, relativeTime } from '../lib/format'

export function SettingsPage() {
  const { libraries, reload } = useLibraries()
  const [adding, setAdding] = useState(false)

  return (
    <>
      <h1>Settings</h1>
      <section>
        <header className="page-header">
          <h2 className="section-title">Libraries</h2>
          {!adding && (
            <button className="button" onClick={() => setAdding(true)}>
              Add library
            </button>
          )}
        </header>

        {adding && (
          <AddLibraryForm
            onDone={() => {
              setAdding(false)
              reload()
            }}
            onCancel={() => setAdding(false)}
          />
        )}

        {libraries.map((library) => (
          <LibraryRow key={library.id} library={library} onChange={reload} />
        ))}
        {libraries.length === 0 && !adding && <p className="muted">No libraries yet.</p>}
      </section>
    </>
  )
}

function LibraryRow({ library, onChange }: { library: Library; onChange: () => void }) {
  async function remove() {
    if (!confirm(`Remove “${library.name}” from Flexarr? Your files are not touched.`)) return
    await deleteLibrary(library.id)
    onChange()
  }

  return (
    <div className="card library-row">
      <div className="library-row-main">
        <h3>{library.name}</h3>
        <p className="muted">
          {libraryTypeLabel[library.type]} · {library.itemCount.toLocaleString()} items · scanned {relativeTime(library.lastScannedAt)}
        </p>
        {library.folders.map((f) => (
          <code key={f} className="folder">
            {f}
          </code>
        ))}
        {library.scan && <ScanProgress scan={library.scan} />}
      </div>
      <div className="library-row-actions">
        <button className="button secondary" disabled={!!library.scan} onClick={() => scanLibrary(library.id).then(() => setTimeout(onChange, 2500))}>
          Scan
        </button>
        <button className="button danger" onClick={remove}>
          Remove
        </button>
      </div>
    </div>
  )
}

function AddLibraryForm({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState('')
  const [type, setType] = useState<LibraryType>('Movies')
  const [folders, setFolders] = useState<string[]>([])
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaving(true)
    try {
      await createLibrary({ name: name || libraryTypeLabel[type], type, folders })
      onDone()
    } catch (e) {
      setErrors(e instanceof ApiError && e.messages.length ? e.messages : [e instanceof Error ? e.message : String(e)])
    } finally {
      setSaving(false)
    }
  }

  return (
    <form className="card add-library" onSubmit={submit} aria-label="Add library">
      <div className="field-row">
        <label>
          Type
          <select value={type} onChange={(e) => setType(e.target.value as LibraryType)}>
            <option value="Movies">Movies</option>
            <option value="TvShows">TV Shows</option>
          </select>
        </label>
        <label>
          Name
          <input value={name} placeholder={libraryTypeLabel[type]} onChange={(e) => setName(e.target.value)} />
        </label>
      </div>

      <div className="field">
        <span className="field-label">Folders</span>
        {folders.map((f) => (
          <div key={f} className="chosen-folder">
            <code>{f}</code>
            <button type="button" className="link-button" onClick={() => setFolders(folders.filter((x) => x !== f))}>
              remove
            </button>
          </div>
        ))}
        <FolderPicker onPick={(path) => setFolders((current) => (current.includes(path) ? current : [...current, path]))} />
      </div>

      {errors.length > 0 && (
        <ul className="error" role="alert">
          {errors.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}

      <div className="form-actions">
        <button type="button" className="button secondary" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="button" disabled={saving || folders.length === 0}>
          {saving ? 'Adding…' : 'Add library'}
        </button>
      </div>
    </form>
  )
}
