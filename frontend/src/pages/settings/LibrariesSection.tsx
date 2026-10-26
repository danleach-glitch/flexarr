import { useMemo, useState, type FormEvent } from 'react'
import { ApiError, createLibrary, deleteLibrary, getAllLibraries, scanLibrary, type Library, type LibraryType } from '../../api/client'
import { FolderPicker } from '../../components/FolderPicker'
import { ScanProgress } from '../../components/ScanProgress'
import { useLibraries } from '../../hooks/librariesContext'
import { Film } from '../../components/icons'
import { libraryIcons } from '../../components/libraryIcons'
import { useApi } from '../../hooks/useApi'
import { libraryTypeLabel, relativeTime } from '../../lib/format'

export function LibrariesSection() {
  // The nav's list (which polls during scans) leaves out libraries hidden from this profile; Settings shows them all.
  const { libraries: visible, reload: reloadVisible } = useLibraries()
  // Fetch the full list again when libraries come and go or a scan finishes; counts and progress during a scan come
  // from the nav's polled list, so its every poll doesn't send another request.
  const all = useApi(getAllLibraries, [visible.map((l) => `${l.id}:${l.lastScannedAt}`).join()])
  const libraries = useMemo(() => {
    const live = new Map(visible.map((l) => [l.id, l]))
    // Podcasts aren't a library you manage here: Settings › Podcasts has their folder.
    return (all.data ?? visible).filter((l) => l.type !== 'Podcasts').map((l) => {
      const polled = live.get(l.id)
      return polled ? { ...l, itemCount: polled.itemCount, scan: polled.scan, lastScannedAt: polled.lastScannedAt } : l
    })
  }, [all.data, visible])
  const reload = () => {
    reloadVisible()
    all.reload()
  }
  const [adding, setAdding] = useState(false)

  return (
    <section>
      <header className="settings-header">
        <h2>Libraries</h2>
        {!adding && (
          <button className="button small" onClick={() => setAdding(true)}>
            Add library
          </button>
        )}
      </header>
      <p className="settings-lede">Each library is a set of folders holding one kind of media. Flexarr rescans them every 12 hours and whenever Sonarr or Radarr imports something.</p>
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
      {libraries.length === 0 && !adding && <p className="muted">No libraries yet. Add one to start.</p>}
    </section>
  )
}

function LibraryRow({ library, onChange }: { library: Library; onChange: () => void }) {
  const [message, setMessage] = useState<string>()

  async function remove() {
    if (!confirm(`Remove “${library.name}” from Flexarr? The files themselves stay where they are.`)) return
    await deleteLibrary(library.id)
    onChange()
  }

  const Icon = libraryIcons[library.type] ?? Film
  return (
    <div className="panel library-row">
      <div>
        <h3 className="library-title">
          <Icon />
          {library.name}
        </h3>
        <p className="muted">
          {libraryTypeLabel[library.type]}, {library.itemCount.toLocaleString()} titles, last scanned {relativeTime(library.lastScannedAt)}
        </p>
        {library.folders.map((f) => (
          <code key={f} className="folder">
            {f}
          </code>
        ))}
        {library.scan && <ScanProgress scan={library.scan} />}
        {message && <p className="muted">{message}</p>}
      </div>
      <div className="library-row-actions">
        <button className="button quiet small" disabled={!!library.scan} onClick={() => scanLibrary(library.id).then(() => setTimeout(onChange, 2500))}>
          Scan for changes
        </button>
        <button
          className="button quiet small"
          onClick={() => scanLibrary(library.id, true).then(() => setMessage('Refreshing artwork and details from Sonarr and Radarr. This takes a few minutes.'))}
        >
          Refresh metadata
        </button>
        <button className="button danger small" onClick={remove}>
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
    <form className="panel add-library" onSubmit={submit} aria-label="Add library">
      <div className="field-row">
        <label>
          Kind
          <select value={type} onChange={(e) => setType(e.target.value as LibraryType)}>
            <option value="Movies">Films</option>
            <option value="TvShows">TV shows</option>
            <option value="Music">Music</option>
          </select>
        </label>
        <label>
          Name
          <input type="text" value={name} placeholder={libraryTypeLabel[type]} onChange={(e) => setName(e.target.value)} />
        </label>
      </div>
      <div className="field">
        <span className="field-label">Folders</span>
        {folders.map((f) => (
          <div key={f} className="chosen-folder">
            <code>{f}</code>
            <button type="button" className="text-button" onClick={() => setFolders(folders.filter((x) => x !== f))}>
              Remove
            </button>
          </div>
        ))}
        <FolderPicker onPick={(path) => setFolders((current) => (current.includes(path) ? current : [...current, path]))} />
      </div>
      {errors.length > 0 && (
        <ul className="error form-errors" role="alert">
          {errors.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
      <div className="form-actions">
        <button type="button" className="button quiet" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="button" disabled={saving || folders.length === 0}>
          {saving ? 'Adding…' : 'Add library'}
        </button>
      </div>
    </form>
  )
}
