import { useMemo, useState, type FormEvent } from 'react'
import {
  ApiError,
  createLibrary,
  deleteLibrary,
  getAllLibraries,
  getLibraryHealth,
  getPodcastCountries,
  getSettings,
  saveSettings,
  scanLibrary,
  updateLibrary,
  type Library,
  type LibraryType,
} from '../../api/client'
import { FolderPicker } from '../../components/FolderPicker'
import { ScanProgress } from '../../components/ScanProgress'
import { useLibraries } from '../../hooks/librariesContext'
import { Film } from '../../components/icons'
import { libraryIcons } from '../../components/libraryIcons'
import { useApi } from '../../hooks/useApi'
import { libraryTypeLabel, relativeTime } from '../../lib/format'
import { SaveStatus, SettingLink, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText } from './useSettings'

export function LibrariesSection() {
  // The nav's list (which polls during scans) leaves out libraries hidden from this profile; Settings shows them all.
  const { libraries: visible, reload: reloadVisible } = useLibraries()
  // Fetch the full list again when libraries come and go or a scan finishes; counts and progress during a scan come
  // from the nav's polled list, so its every poll doesn't send another request.
  const all = useApi(getAllLibraries, [visible.map((l) => `${l.id}:${l.lastScannedAt}`).join()])
  const libraries = useMemo(() => {
    const live = new Map(visible.map((l) => [l.id, l]))
    return (all.data ?? visible).map((l) => {
      const polled = live.get(l.id)
      return polled ? { ...l, itemCount: polled.itemCount, scan: polled.scan, lastScannedAt: polled.lastScannedAt } : l
    })
  }, [all.data, visible])
  // Podcasts are built in: their folder can move, but they can't be removed or rescanned like the rest.
  const podcasts = libraries.find((l) => l.type === 'Podcasts')
  const managed = libraries.filter((l) => l.type !== 'Podcasts')
  const reload = () => {
    reloadVisible()
    all.reload()
  }
  const [adding, setAdding] = useState(false)

  return (
    <section>
      <SettingsHeader
        title="Libraries"
        action={
          !adding && (
            <button className="button small" onClick={() => setAdding(true)}>
              Add library
            </button>
          )
        }
      >
        Each library is a set of folders holding one kind of media. Flexarr rescans them every 12 hours, and whenever Sonarr, Radarr or Lidarr imports something.
      </SettingsHeader>
      {adding && (
        <AddLibraryForm
          onDone={() => {
            setAdding(false)
            reload()
          }}
          onCancel={() => setAdding(false)}
        />
      )}
      <div className="panel library-list">
        {managed.map((library) => (
          <LibraryRow key={library.id} library={library} onChange={reload} />
        ))}
        {managed.length === 0 && !adding && <p className="muted">No libraries yet. Add one to start.</p>}
      </div>
      {podcasts && <PodcastLibrary library={podcasts} onMoved={() => all.reload()} />}
      <LibraryTools />
    </section>
  )
}

/** Library health and the Plex import, each a page of its own, with how things stand now. */
function LibraryTools() {
  const health = useApi(getLibraryHealth, [])
  const score = health.data?.score
  return (
    <SettingsGroup title="Tools">
      <SettingLink
        to="/settings/health"
        label="Library health"
        help={
          score
            ? score.withProblems === 0
              ? `Score ${score.score}. Every film and episode is in good shape.`
              : `Score ${score.score}. ${score.withProblems.toLocaleString()} ${score.withProblems === 1 ? 'title needs' : 'titles need'} a look.`
            : 'Spare copies, unreadable files, films only in SD and wrong matches.'
        }
      />
      <SettingLink to="/settings/import" label="Bring over from Plex" help="Watch history, where you stopped, and profile pictures." />
    </SettingsGroup>
  )
}

/** The built-in Podcasts library: where episodes wait on their way to phones, and whose charts Browse shows. */
function PodcastLibrary({ library, onMoved }: { library: Library; onMoved: () => void }) {
  const [choosing, setChoosing] = useState(false)
  const [error, setError] = useState<string>()
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const chosen = settings.data?.podcasts?.country ?? ''
  const regionName = countries.data?.find((c) => c.code === settings.data?.podcasts?.resolvedCountry)?.name

  async function move(path: string) {
    try {
      await updateLibrary(library.id, { name: library.name, type: library.type, folders: [path] })
      setChoosing(false)
      setError(undefined)
      onMoved()
    } catch (e) {
      setError(errorText(e))
    }
  }

  async function chooseCountry(country: string) {
    setError(undefined)
    try {
      await saveSettings({ podcasts: { country } })
      settings.reload()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <SettingsGroup title="Podcasts" note={error ? <SaveStatus error={error} /> : 'Built in, for everyone. Who follows what is up to each profile.'}>
      <SettingRow as="div" label="Episodes folder" help={<code className="folder">{library.folders[0]}</code>}>
        {!choosing && (
          <button className="button quiet small" onClick={() => setChoosing(true)}>
            Change
          </button>
        )}
      </SettingRow>
      {choosing && (
        <div className="podcast-folder-picker">
          <p className="muted">Episodes wait here on their way to phones, and each is deleted once the phone has it.</p>
          <FolderPicker onPick={(path) => void move(path)} />
          <button type="button" className="text-button" onClick={() => setChoosing(false)}>
            Cancel
          </button>
        </div>
      )}
      <SettingRow label="Charts and search" help="Whose Apple Podcasts charts and directory Browse shows.">
        <select value={chosen} onChange={(e) => void chooseCountry(e.target.value)} disabled={!settings.data || !countries.data}>
          <option value="">{`This server’s region${!chosen && regionName ? ` (${regionName})` : ''}`}</option>
          {countries.data?.map((c) => (
            <option key={c.code} value={c.code}>
              {c.name}
            </option>
          ))}
        </select>
      </SettingRow>
    </SettingsGroup>
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
    <div className="library-row">
      <div>
        <h3 className="library-title">
          <Icon />
          {library.name}
        </h3>
        <p className="muted">
          {libraryTypeLabel[library.type]} · {library.itemCount.toLocaleString()} {library.itemCount === 1 ? 'title' : 'titles'} · scanned {relativeTime(library.lastScannedAt)}
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
        <button
          className="button quiet small"
          title="Looks for files added, changed or removed"
          disabled={!!library.scan}
          onClick={() => scanLibrary(library.id).then(() => setTimeout(onChange, 2500))}
        >
          Scan
        </button>
        <button
          className="button quiet small"
          title="Fetches artwork and details again"
          onClick={() => scanLibrary(library.id, true).then(() => setMessage('Refreshing artwork and details from Sonarr and Radarr. This takes a few minutes.'))}
        >
          Refresh details
        </button>
        <button className="text-button danger-text" onClick={remove}>
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
