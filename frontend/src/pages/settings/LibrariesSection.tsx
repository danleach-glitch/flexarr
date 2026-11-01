import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
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
import { useSession } from '../../hooks/sessionContext'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Film, Pencil, Picture, Refresh, Trash } from '../../components/icons'
import { libraryIcons } from '../../components/libraryIcons'
import { useApi } from '../../hooks/useApi'
import { libraryTypeLabel, relativeTime } from '../../lib/format'
import { BookDetailsGroup } from './BookDetailsGroup'
import { BookRequestSetupGroup } from './RequestLibrarySelect'
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
        Folders of one kind of media. Rescanned every 12 hours, and whenever Sonarr, Radarr or Lidarr imports something.
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
      {/* With no libraries yet, the form adding the first takes the list's place. */}
      {(managed.length > 0 || !adding) && (
        <div className="panel library-list">
          {managed.map((library) => (
            <LibraryRow key={library.id} library={library} onChange={reload} />
          ))}
          {managed.length === 0 && <p className="muted">No libraries yet. Add one to start.</p>}
        </div>
      )}
      {podcasts && <PodcastLibrary library={podcasts} onMoved={() => all.reload()} />}
      {managed.some((l) => l.type === 'Audiobooks') && <BookDetailsGroup />}
      {/* With an audiobooks library: the group shows itself once Readarr, Bookshelf or Chaptarr is connected. */}
      {managed.some((l) => l.type === 'Audiobooks') && <BookRequestSetupGroup />}
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
  const [renaming, setRenaming] = useState(false)
  // Once a profile has an age limit: everything in the library is fine for it, rated or not.
  const limits = useSession().profiles?.some((p) => p.maxRatingAge != null) === true
  const [fineForKids, setFineForKids] = useState(library.fineForKids ?? false)

  async function toggleKids(fine: boolean) {
    setFineForKids(fine)
    try {
      await updateLibrary(library.id, { name: library.name, type: library.type, folders: library.folders, fineForKids: fine })
    } catch (e) {
      setFineForKids(!fine)
      setMessage(errorText(e))
    }
  }

  async function remove() {
    if (!confirm(`Remove “${library.name}” from Flexarr? The files themselves stay where they are.`)) return
    await deleteLibrary(library.id)
    onChange()
  }

  const Icon = libraryIcons[library.type] ?? Film
  // Just added, its first scan is queued before it starts: scanning too.
  const scanning = !!library.scan || !library.lastScannedAt
  return (
    <div className="library-row">
      <div>
        {renaming ? (
          <RenameLibraryForm
            library={library}
            onDone={(renamed) => {
              setRenaming(false)
              if (renamed) onChange()
              requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-rename="${library.id}"]`)?.focus())
            }}
          />
        ) : (
          <h3 className="library-title">
            <Icon />
            {library.name}
          </h3>
        )}
        <p className="muted">
          {libraryTypeLabel[library.type]} · {library.itemCount.toLocaleString()} {library.type === 'Audiobooks' ? (library.itemCount === 1 ? 'book' : 'books') : library.itemCount === 1 ? 'title' : 'titles'} ·{' '}
          {scanning ? <span className="now-scanning">Now scanning…</span> : `scanned ${relativeTime(library.lastScannedAt)}`}
        </p>
        {library.folders.map((f) => (
          <code key={f} className="folder">
            {f}
          </code>
        ))}
        {/* Kids profiles never see audiobooks, so there's nothing to allow. */}
        {limits && library.fineForKids !== undefined && library.type !== 'Audiobooks' && (
          <label className="library-kids">
            <span>Fine for kids, rated or not</span>
            <input
              type="checkbox"
              role="switch"
              className="switch"
              aria-label={`${library.name}: fine for kids, rated or not`}
              checked={fineForKids}
              onChange={(e) => void toggleKids(e.target.checked)}
            />
          </label>
        )}
        {library.scan && <ScanProgress scan={library.scan} />}
        {message && <p className="muted">{message}</p>}
      </div>
      {!renaming && (
        <div className="library-row-actions">
          <IconButton label="Rename" icon={<Pencil />} data-rename={library.id} aria-label={`Rename ${library.name}`} onClick={() => setRenaming(true)} />
          <IconButton
            label="Scan for new and changed files"
            icon={<Refresh />}
            className="button quiet small icon scan-button"
            busy={scanning}
            onClick={() => scanLibrary(library.id).then(() => new Promise((done) => setTimeout(done, 2500))).then(onChange)}
          />
          <IconButton
            label="Refresh artwork and details"
            icon={<Picture />}
            onClick={() => scanLibrary(library.id, true).then(() => setMessage('Refreshing artwork and details from Sonarr and Radarr. This takes a few minutes.'))}
          />
          <IconButton label="Remove" icon={<Trash />} className="button quiet small icon danger-icon" onClick={remove} />
        </div>
      )}
    </div>
  )
}

/** In place of the library's title: the name it goes by in the sidebar and on every device. Escape puts it back. */
function RenameLibraryForm({ library, onDone }: { library: Library; onDone: (renamed: boolean) => void }) {
  const [name, setName] = useState(library.name)
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => {
    input.current?.focus()
    input.current?.select()
  }, [])

  async function save(e: FormEvent) {
    e.preventDefault()
    if (!name.trim() || name.trim() === library.name) return onDone(false)
    setSaving(true)
    try {
      await updateLibrary(library.id, { name: name.trim(), type: library.type, folders: library.folders })
      onDone(true)
    } catch (err) {
      setError(errorText(err))
      setSaving(false)
    }
  }

  return (
    <form className="library-rename" onSubmit={(e) => void save(e)} onKeyDown={(e) => e.key === 'Escape' && onDone(false)}>
      <label className="field">
        <span className="field-label">Name for {library.name}</span>
        <input ref={input} value={name} onChange={(e) => setName(e.target.value)} maxLength={80} autoComplete="off" />
      </label>
      <div className="form-actions">
        <button type="button" className="button quiet small" onClick={() => onDone(false)}>
          Cancel
        </button>
        <AsyncButton type="submit" className="button small" busy={saving}>
          Save
        </AsyncButton>
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </form>
  )
}

function AddLibraryForm({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState('')
  const [type, setType] = useState<LibraryType>('Movies')
  const [folder, setFolder] = useState<string>()
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaving(true)
    try {
      if (!folder) return
      await createLibrary({ name: name || libraryTypeLabel[type], type, folders: [folder] })
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
            <option value="HomeVideos">Home videos</option>
            <option value="Audiobooks">Audiobooks</option>
          </select>
        </label>
        <label>
          Name
          <input type="text" value={name} placeholder={libraryTypeLabel[type]} onChange={(e) => setName(e.target.value)} />
        </label>
      </div>
      <div className="field">
        <span className="field-label">Folder</span>
        <FolderPicker onOpen={setFolder} />
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
        <AsyncButton type="submit" className="button" busy={saving} disabled={!folder}>
          Add library
        </AsyncButton>
      </div>
    </form>
  )
}
