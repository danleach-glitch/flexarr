import { Fragment, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import {
  ApiError,
  createLibrary,
  deleteLibrary,
  getAllLibraries,
  getLibraryHealthScore,
  getPodcastCountries,
  getSettings,
  saveSettings,
  scanLibrary,
  updateLibrary,
  type FolderWatch,
  type Library,
  type LibraryType,
} from '../../api/client'
import { FolderPicker } from '../../components/FolderPicker'
import { ScanProgress } from '../../components/ScanProgress'
import { useLibraries } from '../../hooks/librariesContext'
import { useSession } from '../../hooks/sessionContext'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Film, Pencil, Picture, Refresh } from '../../components/icons'
import { icon } from '../../components/icons.generated'
import { libraryIcons } from '../../components/libraryIcons'
import { useApi } from '../../hooks/useApi'
import { libraryTypeLabel, relativeTime } from '../../lib/format'
import { BookDetailsGroup } from './BookDetailsGroup'
import { SaveStatus, SettingLink, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'
import { t, Words } from '../../i18n'

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
  // Watching folders for new files (servers before 0.64 don't say, and don't watch).
  const settings = useApi((signal) => getSettings(signal), [])
  const [watchError, setWatchError] = useState<string>()
  const timers = useRef<ReturnType<typeof setTimeout>[]>([])
  useEffect(() => () => timers.current.forEach(clearTimeout), [])

  async function watch(on: boolean) {
    setWatchError(undefined)
    try {
      await saveSettings({ watchFolders: on })
      settings.reload()
      // The server starts or stops watching within a few seconds, longer if a big folder takes a while to walk.
      timers.current.forEach(clearTimeout)
      timers.current = [3000, 10000, 30000].map((after) => setTimeout(() => all.reload(), after))
    } catch (e) {
      setWatchError(errorText(e))
    }
  }

  return (
    <section>
      <SettingsHeader
        title={t(Words.libraries)}
        action={
          !adding && (
            <button className="button small" onClick={() => setAdding(true)}>
              {t('Add library')}
            </button>
          )
        }
      >
        {t('Folders of one kind of media.')}
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
          {managed.length === 0 && <p className="muted">{t('No libraries yet. Add one to start.')}</p>}
        </div>
      )}
      {managed.length > 0 && (
        <WatchSwitch
          watching={settings.data?.watchFolders}
          loading={settings.loading && !settings.data}
          error={watchError ?? (settings.error && errorText(settings.error))}
          onChange={(on) => void watch(on)}
        />
      )}
      {podcasts && <PodcastLibrary library={podcasts} onMoved={() => all.reload()} />}
      {managed.some((l) => l.type === 'Audiobooks') && <BookDetailsGroup />}
      <LibraryTools />
    </section>
  )
}

/**
 * Notice new files straight away. Takes its place while the settings load, so nothing below it jumps, and says so if they
 * can't be read. Not at all for servers before 0.64, which don't watch.
 */
export function WatchSwitch({ watching, loading, error, onChange }: { watching?: boolean | null; loading: boolean; error?: string; onChange: (on: boolean) => void }) {
  if (watching == null && !loading) return error ? <SaveStatus error={error} /> : null
  return (
    <SettingsGroup note={error && <SaveStatus error={error} />}>
      <ToggleRow
        label={t('Notice new files straight away')}
        help={t('Watches these folders, so new files show up within a minute and drives are woken less.')}
        checked={watching ?? true}
        disabled={loading}
        onChange={onChange}
      />
    </SettingsGroup>
  )
}

/** Library health and the Plex import, each a page of its own, with how things stand now. */
function LibraryTools() {
  const health = useApi(getLibraryHealthScore, [])
  const score = health.data
  return (
    <SettingsGroup title={t('Tools')}>
      <SettingLink
        to="/settings/health"
        label={t('Library health')}
        help={
          score
            ? score.withProblems === 0
              ? t('Score {score}. Every movie and episode is in good shape.', { score: score.score })
              : t('Score {score}. {count, plural, one {# title needs} other {# titles need}} a look.', { score: score.score, count: score.withProblems })
            : t('Spare copies, unreadable files, movies only in SD and wrong matches.')
        }
      />
      <SettingLink to="/settings/import" label={t('Bring over from Plex')} help={t('Watch history, where you stopped, and profile pictures.')} />
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
    <SettingsGroup title={t(Words.podcasts)} note={error ? <SaveStatus error={error} /> : t('Built in, for everyone. Who follows what is up to each profile.')}>
      <SettingRow as="div" label={t('Episodes folder')} help={<code className="folder">{library.folders[0]}</code>}>
        {!choosing && (
          <button className="button quiet small" onClick={() => setChoosing(true)}>
            {t('Change')}
          </button>
        )}
      </SettingRow>
      {choosing && (
        <div className="podcast-folder-picker">
          <p className="muted">{t('Episodes wait here on their way to phones, and each is deleted once the phone has it.')}</p>
          <FolderPicker onPick={(path) => void move(path)} />
          <button type="button" className="text-button" onClick={() => setChoosing(false)}>
            {t(Words.cancel)}
          </button>
        </div>
      )}
      <SettingRow label={t('Charts and search')} help={t('Whose Apple Podcasts charts and directory Browse shows.')}>
        <select value={chosen} onChange={(e) => void chooseCountry(e.target.value)} disabled={!settings.data || !countries.data}>
          <option value="">{!chosen && regionName ? t('This server’s region ({region})', { region: regionName }) : t('This server’s region')}</option>
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
    if (!confirm(t('Remove “{name}” from Flexarr? The files themselves stay where they are.', { name: library.name }))) return
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
          {libraryTypeLabel[library.type]}{'\u2003'}
          {library.type === 'Audiobooks'
            ? t('{count, plural, one {# book} other {# books}}', { count: library.itemCount })
            : t('{count, plural, one {# title} other {# titles}}', { count: library.itemCount })}
          {'\u2003'}
          {scanning ? <span className="now-scanning">{t('Now scanning…')}</span> : t('scanned {time}', { time: relativeTime(library.lastScannedAt) })}
        </p>
        {library.folders.map((f) => (
          <Fragment key={f}>
            <code className="folder">{f}</code>
            <FolderWatchNote watch={library.folderWatch?.find((w) => w.path === f)} />
          </Fragment>
        ))}
        {/* Kids profiles never see audiobooks, so there's nothing to allow. */}
        {limits && library.fineForKids !== undefined && library.type !== 'Audiobooks' && (
          <label className="library-kids">
            <span>{t('Fine for kids, rated or not')}</span>
            <input
              type="checkbox"
              role="switch"
              className="switch"
              aria-label={t('{name}: fine for kids, rated or not', { name: library.name })}
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
          <IconButton label={t('Rename')} icon={<Pencil />} data-rename={library.id} aria-label={t('Rename {name}', { name: library.name })} onClick={() => setRenaming(true)} />
          <IconButton
            label={t('Scan for new and changed files')}
            icon={<Refresh />}
            className="button quiet small icon scan-button"
            busy={scanning}
            onClick={() => scanLibrary(library.id).then(() => new Promise((done) => setTimeout(done, 2500))).then(onChange)}
          />
          <IconButton
            label={t('Refresh artwork and details')}
            icon={<Picture />}
            onClick={() => scanLibrary(library.id, true).then(() => setMessage(t('Refreshing artwork and details from Sonarr and Radarr. This takes a few minutes.')))}
          />
          <IconButton label={t('Remove')} icon={<icon.delete />} className="button quiet small icon danger-icon" onClick={remove} />
        </div>
      )}
    </div>
  )
}

/** Under a library folder that isn't being watched, and why. Nothing for one that is, or while watching is off: the switch says so. */
export function FolderWatchNote({ watch }: { watch?: FolderWatch }) {
  const text = !watch
    ? null
    : watch.state === 'Share'
      ? t('Network share: checked every 12 hours')
      : watch.state !== 'Unavailable'
        ? null
        : watch.problem === 'Missing'
          ? t('Folder not found')
          : watch.problem === 'Limit'
            ? t('Watch limit reached: checked every 12 hours')
            : t('Can’t be watched: checked every 12 hours')
  return text && <span className="folder-watch">{text}</span>
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
        <span className="field-label">{t('Name for {name}', { name: library.name })}</span>
        <input ref={input} value={name} onChange={(e) => setName(e.target.value)} maxLength={80} autoComplete="off" />
      </label>
      <div className="form-actions">
        <button type="button" className="button quiet small" onClick={() => onDone(false)}>
          {t(Words.cancel)}
        </button>
        <AsyncButton type="submit" className="button small" busy={saving}>
          {t('Save')}
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
    <form className="panel add-library" onSubmit={submit} aria-label={t('Add library')}>
      <div className="field-row">
        <label>
          {t('Kind')}
          <select value={type} onChange={(e) => setType(e.target.value as LibraryType)}>
            <option value="Movies">{t(Words.movies)}</option>
            <option value="TvShows">{t('TV shows')}</option>
            <option value="Music">{t(Words.music)}</option>
            <option value="HomeVideos">{t('Home videos')}</option>
            <option value="Audiobooks">{t(Words.audiobooks)}</option>
          </select>
        </label>
        <label>
          {t('Name')}
          <input type="text" value={name} placeholder={libraryTypeLabel[type]} onChange={(e) => setName(e.target.value)} />
        </label>
      </div>
      <div className="field">
        <span className="field-label">{t('Folder')}</span>
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
          {t(Words.cancel)}
        </button>
        <AsyncButton type="submit" className="button" busy={saving} disabled={!folder}>
          {t('Add library')}
        </AsyncButton>
      </div>
    </form>
  )
}
