import { useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, NavLink, useParams } from 'react-router'
import {
  ApiError,
  askForProfile,
  createLibrary,
  deleteLibrary,
  getActivity,
  getMe,
  revealApiKey,
  rotateApiKey,
  rotateWebhookSecret,
  signIn,
  UnauthorizedError,
  getArrStatus,
  getRequestStatus,
  getLibraryHealth,
  confirmHeldRemoval,
  createProfile,
  deleteProfile,
  getPlexSource,
  getRemoteAccess,
  keepMatch,
  linkTailscale,
  setTailscaleAccess,
  getAllLibraries,
  removeDuplicates,
  deleteCopy,
  getChangelog,
  getSystemStatus,
  getHealthCheck,
  startHealthCheck,
  getLayout,
  saveLayout,
  type ItemSummary,
  type LibraryHealth,
  type HeldFolder,
  type Layout,
  type RowChoice,
  importPlexAvatars,
  removeProfileImage,
  uploadProfileImage,
  type PlexAvatarResult,
  getProfiles,
  selectProfile,
  updateProfile,
  getSettings,
  importFromPlex,
  saveSettings,
  scanLibrary,
  searchAgain,
  testArr,
  type ArrKind,
  type ArrStatus,
  type HardwareAcceleration,
  type Library,
  type PlexImportResult,
  type RemoteAccess,
  type Profile,
  type LibraryType,
  type Settings,
} from '../api/client'
import { Artwork } from '../components/Artwork'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ConnectTv } from '../components/ConnectTv'
import { CopyButton } from '../components/CopyButton'
import { FixMatchDialog } from '../components/FixMatchDialog'
import { FolderPicker } from '../components/FolderPicker'
import { Avatar } from '../components/ProfilePicker'
import { ScanProgress } from '../components/ScanProgress'
import { SignIns } from '../components/SignIns'
import { useLibraries } from '../hooks/librariesContext'
import { useSession } from '../hooks/sessionContext'
import { ChevronDown, ChevronUp } from '../components/icons'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { episodeCode, formatClock, formatSize, libraryTypeLabel, relativeTime } from '../lib/format'

const sections = [
  { id: 'libraries', label: 'Libraries' },
  { id: 'profiles', label: 'Profiles' },
  { id: 'playback', label: 'Playback' },
  { id: 'layout', label: 'Home and Browse' },
  { id: 'connections', label: 'Connections' },
  { id: 'remote', label: 'Remote access' },
  { id: 'activity', label: 'Activity' },
  { id: 'health', label: 'Library health' },
  { id: 'import', label: 'Bring over from Plex' },
  { id: 'tv', label: 'Connect a TV' },
  { id: 'server', label: 'Server' },
  { id: 'changelog', label: 'Changelog' },
] as const

export function SettingsPage() {
  const { section = 'libraries' } = useParams()
  const me = useApi(getMe, [])

  // Settings are the main profile's. Another profile chosen on this browser has given that up until the main
  // profile is chosen again (with its PIN).
  if (me.data && !me.data.isAdmin) {
    return (
      <div className="settings">
        {me.data.method === 'tailscale' && me.data.profileId === 'owner' ? <TailscaleAdminNote /> : <MainProfileNote />}
      </div>
    )
  }

  return (
    <div className="settings">
      <aside className="settings-nav">
        <h1>Settings</h1>
        <nav aria-label="Settings">
          {sections.map((s) => (
            <NavLink key={s.id} to={`/settings/${s.id}`} className={({ isActive }) => (isActive || (section === s.id) ? 'active' : undefined)}>
              {s.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <div className="settings-content">
        {section === 'libraries' && <LibrariesSection />}
        {section === 'profiles' && <ProfilesSection />}
        {section === 'playback' && <PlaybackSection />}
        {section === 'layout' && <LayoutSection />}
        {section === 'tv' && <ConnectTv />}
        {section === 'changelog' && <ChangelogSection />}
        {section === 'connections' && <ConnectionsSection />}
        {section === 'remote' && <RemoteAccessSection />}
        {section === 'activity' && <ActivitySection />}
        {section === 'server' && <ServerSection />}
        {section === 'import' && <ImportSection />}
        {section === 'health' && <HealthSection />}
      </div>
    </div>
  )
}

/** Asks for the main profile (its PIN, if it has one), then comes back to this page of Settings. */
function switchToMainProfile(main: Profile | undefined) {
  askForProfile(main?.id)
  window.location.reload()
}

/** Another profile is chosen on this browser: the way back to the main one, and to this page. */
function MainProfileNote() {
  const { profiles } = useSession()
  const main = profiles?.find((p) => p.isMain)
  const [refused, setRefused] = useState(false)

  return (
    <div className="page-message">
      <p className="muted">Settings are for the main profile.</p>
      {refused ? (
        <p className="muted">Ask whoever manages Flexarr to give the main profile a PIN.</p>
      ) : (
        <p>
          <button
            className="button"
            onClick={async () => {
              if (main && !main.hasPin) {
                // Nothing to ask for: switch straight to it, unless the server won't (over Tailscale, without a PIN).
                try {
                  await selectProfile(main.id)
                  sessionStorage.setItem('flexarr.profileChosen', '1')
                  window.location.reload()
                } catch {
                  setRefused(true)
                }
                return
              }
              switchToMainProfile(main)
            }}
          >
            Switch to the main profile
          </button>
        </p>
      )}
    </div>
  )
}

/**
 * Over Tailscale the owner's account watches as the main profile but doesn't manage the server on its own: another
 * program on the server could pose as it. The main profile's PIN, or the API key, proves it's really them.
 */
function TailscaleAdminNote() {
  const [error, setError] = useState<string>()
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const mainHasPin = main?.hasPin ?? false

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    setError(undefined)
    signIn(key)
      .then(() => window.location.reload())
      .catch((e: unknown) => setError(e instanceof UnauthorizedError ? 'That isn’t the right key.' : String(e)))
  }

  return (
    <div className="panel">
      <p className="settings-lede">
        Over Tailscale, changing settings takes one more step, because another program on the server could pose as your Tailscale account.{' '}
        {mainHasPin
          ? 'Choose the main profile with its PIN, and this browser stays signed in until it’s signed out or left unused for two weeks.'
          : 'Ask whoever manages Flexarr to give the main profile a PIN, or sign in with the API key below.'}
      </p>
      {mainHasPin && (
        <p>
          <button className="button" onClick={() => switchToMainProfile(main)}>
            Choose the main profile
          </button>
        </p>
      )}
      <p className="settings-lede">{mainHasPin ? 'Or sign this browser in with the API key:' : 'Sign this browser in with the API key:'}</p>
      <form className="api-key-row" onSubmit={submit}>
        <input name="apiKey" type="password" autoComplete="off" aria-label="API key" required />
        <button type="submit" className="button">
          Sign in
        </button>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- Libraries

function LibrariesSection() {
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

  return (
    <div className="panel library-row">
      <div>
        <h3>{library.name}</h3>
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

// ---------------------------------------------------------------- Playback

const accelerationLabel: Record<HardwareAcceleration, string> = {
  Auto: 'Automatic (recommended)',
  None: 'Off: convert on the CPU',
  VideoToolbox: 'Apple VideoToolbox',
  Vaapi: 'VA-API (Intel and AMD on Linux)',
  Qsv: 'Intel Quick Sync',
  Nvenc: 'NVIDIA NVENC',
}

function useSettings() {
  const { data, error, reload } = useApi(getSettings, [])
  const [saved, setSaved] = useState<string>()
  const [saveError, setSaveError] = useState<string>()
  const save = async (update: Parameters<typeof saveSettings>[0], message = 'Saved') => {
    setSaveError(undefined)
    try {
      await saveSettings(update)
      setSaved(message)
      reload()
      setTimeout(() => setSaved(undefined), 3000)
    } catch (e) {
      setSaveError(e instanceof ApiError && e.messages.length ? e.messages.join(' ') : String(e))
    }
  }
  return { settings: data, error, save, saved, saveError }
}

function PlaybackSection() {
  const { settings, save, saved, saveError } = useSettings()
  const [draft, setDraft] = useState<Settings['transcoding']>()
  useEffect(() => settings && setDraft(settings.transcoding), [settings])
  if (!settings || !draft) return <p className="muted">Loading…</p>

  return (
    <section>
      <header className="settings-header">
        <h2>Playback</h2>
      </header>
      <p className="settings-lede">
        Files your device can play are sent untouched. Everything else is converted as you watch; with hardware acceleration, conversion runs on the graphics chip
        and uses very little power.
      </p>
      <form
        className="panel"
        onSubmit={(e) => {
          e.preventDefault()
          void save({ transcoding: draft })
        }}
      >
        <label className="field">
          <span className="field-label">Hardware acceleration</span>
          <select value={draft.hardwareAcceleration} onChange={(e) => setDraft({ ...draft, hardwareAcceleration: e.target.value as HardwareAcceleration })}>
            {settings.availableHardwareAcceleration.map((h) => (
              <option key={h} value={h}>
                {accelerationLabel[h]}
              </option>
            ))}
          </select>
          <span className="field-help">Only options your server's FFmpeg supports are listed.</span>
        </label>
        <label className="field">
          <span className="field-label">Limit each stream to</span>
          <select value={draft.maxStreamingBitrate} onChange={(e) => setDraft({ ...draft, maxStreamingBitrate: Number(e.target.value) })}>
            <option value={0}>No limit</option>
            <option value={20_000_000}>20 Mbps</option>
            <option value={10_000_000}>10 Mbps</option>
            <option value={4_000_000}>4 Mbps (good for remote viewing)</option>
            <option value={2_000_000}>2 Mbps</option>
          </select>
          <span className="field-help">Useful when watching away from home over a slow upload.</span>
        </label>
        {draft.hardwareAcceleration === 'Vaapi' && (
          <label className="field">
            <span className="field-label">VA-API device</span>
            <input type="text" value={draft.vaapiDevice} onChange={(e) => setDraft({ ...draft, vaapiDevice: e.target.value })} />
          </label>
        )}
        {draft.hardwareAcceleration === 'None' && (
          <label className="field">
            <span className="field-label">Software speed</span>
            <select value={draft.softwarePreset} onChange={(e) => setDraft({ ...draft, softwarePreset: e.target.value })}>
              <option value="ultrafast">Fastest, lowest quality</option>
              <option value="veryfast">Fast (recommended)</option>
              <option value="medium">Slower, better quality</option>
            </select>
          </label>
        )}
        <SaveBar saved={saved} error={saveError} />
      </form>
    </section>
  )
}

function SaveBar({ saved, error, label = 'Save changes' }: { saved?: string; error?: string; label?: string }) {
  return (
    <div className="form-actions">
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
      {saved && (
        <span className="muted" role="status">
          {saved}
        </span>
      )}
      <button type="submit" className="button">
        {label}
      </button>
    </div>
  )
}

// ---------------------------------------------------------------- Connections

interface DraftConnection {
  name: string
  kind: ArrKind
  baseUrl: string
  apiKey: string
  hasApiKey: boolean
}

function ConnectionsSection() {
  const { settings, save, saved, saveError } = useSettings()
  // Asked once the settings are in (and again after a save), not also while they load.
  const status = useApi((signal) => (settings ? getArrStatus(signal) : Promise.resolve(undefined)), [settings])
  const seerrStatus = useApi((signal) => (settings ? getRequestStatus(signal) : Promise.resolve(undefined)), [settings])
  const [connections, setConnections] = useState<DraftConnection[]>([])
  const [mappings, setMappings] = useState<Settings['pathMappings']>([])
  const [seerr, setSeerr] = useState({ baseUrl: '', apiKey: '', hasApiKey: false })
  const [tests, setTests] = useState<Record<number, ArrStatus | 'testing'>>({})

  useEffect(() => {
    if (!settings) return
    setConnections(settings.arr.map((a) => ({ ...a, apiKey: '' })))
    setMappings(settings.pathMappings)
    setSeerr({ baseUrl: settings.seerr?.baseUrl ?? '', apiKey: '', hasApiKey: settings.seerr?.hasApiKey ?? false })
  }, [settings])

  if (!settings) return <p className="muted">Loading…</p>

  const update = (i: number, change: Partial<DraftConnection>) => setConnections((c) => c.map((x, j) => (j === i ? { ...x, ...change } : x)))

  return (
    <section>
      <header className="settings-header">
        <h2>Connections</h2>
      </header>
      <p className="settings-lede">
        Flexarr reads artwork, descriptions, cast and trailers from your Sonarr and Radarr, so no other accounts or API keys are needed. Seerr adds
        Discover and requests.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          void save({
            arr: connections.map((c) => ({ name: c.name, kind: c.kind, baseUrl: c.baseUrl, apiKey: c.apiKey || undefined })),
            pathMappings: mappings,
            seerr: { baseUrl: seerr.baseUrl, apiKey: seerr.apiKey || undefined },
          })
        }}
      >
        {connections.map((c, i) => {
          const live = status.data?.find((s) => s.name === c.name)
          const test = tests[i]
          const shown = test && test !== 'testing' ? test : live
          return (
            <div key={i} className="panel connection">
              <div className="connection-status">
                <span className={shown?.ok ? 'status-dot ok' : shown ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
                <strong>{c.name || c.kind}</strong>
                <span className="muted">{test === 'testing' ? 'Testing…' : shown?.ok ? `Connected, version ${shown.version}` : shown?.error}</span>
              </div>
              <div className="field-row">
                <label>
                  Kind
                  <select value={c.kind} onChange={(e) => update(i, { kind: e.target.value as ArrKind })}>
                    <option>Sonarr</option>
                    <option>Radarr</option>
                    <option>Lidarr</option>
                  </select>
                </label>
                <label>
                  Name
                  <input type="text" value={c.name} onChange={(e) => update(i, { name: e.target.value })} />
                </label>
                <label className="grow">
                  Address
                  <input type="url" value={c.baseUrl} placeholder="http://localhost:8989" onChange={(e) => update(i, { baseUrl: e.target.value })} />
                </label>
                <label className="grow">
                  API key
                  <input
                    type="password"
                    value={c.apiKey}
                    placeholder={c.hasApiKey ? 'Saved; type to replace' : 'Settings › General in Sonarr or Radarr'}
                    onChange={(e) => update(i, { apiKey: e.target.value })}
                  />
                </label>
              </div>
              <div className="form-actions">
                <button
                  type="button"
                  className="button quiet small"
                  onClick={async () => {
                    setTests((t) => ({ ...t, [i]: 'testing' }))
                    const result = await testArr({ name: c.name, kind: c.kind, baseUrl: c.baseUrl, apiKey: c.apiKey || undefined }).catch(
                      (e: unknown): ArrStatus => ({ name: c.name, kind: c.kind, ok: false, version: null, error: e instanceof Error ? e.message : String(e) }),
                    )
                    setTests((t) => ({ ...t, [i]: result }))
                  }}
                >
                  Test connection
                </button>
                <button type="button" className="button danger small" onClick={() => setConnections((all) => all.filter((_, j) => j !== i))}>
                  Remove
                </button>
              </div>
            </div>
          )
        })}
        <div className="connections-add">
          <button
            type="button"
            className="button quiet small"
            onClick={() => setConnections((c) => [...c, { name: 'Sonarr', kind: 'Sonarr', baseUrl: 'http://localhost:8989', apiKey: '', hasApiKey: false }])}
          >
            Add a connection
          </button>
        </div>

        <h3 className="settings-subhead">Requests</h3>
        <p className="settings-lede">
          Connect Seerr (or Overseerr or Jellyseerr) to see what's trending that you don't have yet, and request it in one click. Requests follow your Seerr rules.
        </p>
        <div className="panel connection">
          <div className="connection-status">
            <span className={seerrStatus.data?.reachable ? 'status-dot ok' : seerrStatus.data?.configured ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
            <strong>Seerr</strong>
            <span className="muted">
              {seerrStatus.loading
                ? 'Checking…'
                : !seerrStatus.data?.configured
                  ? 'Not set up'
                  : seerrStatus.data.reachable
                    ? 'Connected'
                    : 'Flexarr can’t reach Seerr at this address, or the API key isn’t right'}
            </span>
          </div>
          <div className="field-row">
            <label className="grow">
              Seerr address
              <input type="url" value={seerr.baseUrl} placeholder="http://localhost:5055" onChange={(e) => setSeerr({ ...seerr, baseUrl: e.target.value })} />
            </label>
            <label className="grow">
              API key
              <input
                type="password"
                value={seerr.apiKey}
                placeholder={seerr.hasApiKey ? 'Saved; type to replace' : 'Settings › General in Seerr'}
                onChange={(e) => setSeerr({ ...seerr, apiKey: e.target.value })}
              />
            </label>
          </div>
        </div>

        <h3 className="settings-subhead">Path mappings</h3>
        <p className="settings-lede">
          If Sonarr and Radarr run in Docker, they see your media under different paths. Map their paths to the ones on this server.
        </p>
        <div className="panel">
          {mappings.map((m, i) => (
            <div key={i} className="field-row mapping">
              <label className="grow">
                Path in Sonarr or Radarr
                <input type="text" value={m.from} placeholder="/data" onChange={(e) => setMappings(mappings.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))} />
              </label>
              <label className="grow">
                Path on this server
                <input type="text" value={m.to} placeholder="/Volumes/Media/data" onChange={(e) => setMappings(mappings.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))} />
              </label>
              <button type="button" className="text-button" onClick={() => setMappings(mappings.filter((_, j) => j !== i))}>
                Remove
              </button>
            </div>
          ))}
          <button type="button" className="button quiet small" onClick={() => setMappings([...mappings, { from: '', to: '' }])}>
            Add a mapping
          </button>
        </div>
        <SaveBar saved={saved} error={saveError} />
      </form>
    </section>
  )
}

// ---------------------------------------------------------------- Activity

function ActivitySection() {
  const { data, reload } = useApi(getActivity, [])
  usePolling(reload, 5000)
  if (!data) return <p className="muted">Loading…</p>

  const analysed = data.episodesTotal ? Math.round((data.episodesAnalysed / data.episodesTotal) * 100) : 100
  return (
    <section>
      <header className="settings-header">
        <h2>Activity</h2>
      </header>
      <h3 className="settings-subhead">Watching now</h3>
      {data.streams.length === 0 && <p className="muted">Nobody is watching anything.</p>}
      {data.streams.map((s) => (
        <div key={s.sessionId} className="panel stream">
          <div>
            <strong>{s.series ?? s.item.title}</strong>
            {s.series && (
              <span className="muted">
                {' '}
                {episodeCode(s.item)} {s.item.title}
              </span>
            )}
            <p className="muted">
              {s.paused ? 'Paused at' : 'At'} {formatClock(s.positionSeconds)} of {formatClock(s.durationSeconds)}
            </p>
          </div>
          <span className="tag">{s.method === 'DirectPlay' ? 'Original file' : `Converting to ${s.transcode}`}</span>
        </div>
      ))}

      <h3 className="settings-subhead">Background work</h3>
      <div className="panel">
        {data.scan ? <ScanProgress scan={data.scan} /> : <p className="muted">No scans running.</p>}
        <div className="meter">
          <span>
            Intros and credits found for {data.episodesAnalysed.toLocaleString()} of {data.episodesTotal.toLocaleString()} episodes
          </span>
          <span className="scan-track">
            <span style={{ width: `${analysed}%` }} />
          </span>
          <span className="field-help">Episodes you start watching are analysed first. The rest happens while the server is idle.</span>
        </div>
        <div className="meter">
          <span>
            Artwork and details for {data.itemsWithMetadata.toLocaleString()} of {data.itemsTotal.toLocaleString()} titles
          </span>
        </div>
      </div>
    </section>
  )
}

// ---------------------------------------------------------------- Server

function ServerSection() {
  const { settings, save, saved, saveError } = useSettings()
  const [key, setKey] = useState<string>()
  const [keyError, setKeyError] = useState<string>()
  // After a new key: how many browsers and TVs were signed out with it.
  const [rotated, setRotated] = useState<{ signedOut: number }>()
  const [auth, setAuth] = useState<Settings['authenticationRequired']>()
  // Showing or replacing the key asks again first: for the main profile's PIN when it has one.
  const [ask, setAsk] = useState<'reveal' | 'rotate'>()
  const [signInsVersion, setSignInsVersion] = useState(0)
  const [webhookSecret, setWebhookSecret] = useState<string>()
  const [askSecret, setAskSecret] = useState(false)
  const [secretState, setSecretState] = useState<{ busy?: boolean; error?: string; done?: boolean }>({})
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const mainHasPin = main?.hasPin ?? false
  const origin = window.location.origin
  const askForm = useRef<HTMLFormElement>(null)
  const opener = useRef<HTMLButtonElement | null>(null)
  const keyArea = useRef<HTMLDivElement>(null)

  // Keyboard and screen reader users land on what to do next: the PIN, or the button that goes ahead.
  useEffect(() => {
    if (!ask) return
    askForm.current?.querySelector<HTMLElement>('input[name="pin"], button[type="submit"]')?.focus()
  }, [ask, mainHasPin])

  function open(which: 'reveal' | 'rotate', button: HTMLButtonElement) {
    opener.current = button
    setKeyError(undefined)
    setAsk(which)
  }

  function cancel() {
    setAsk(undefined)
    setKeyError(undefined)
    // The buttons were disabled while asking; once they're back, return to the one that asked.
    requestAnimationFrame(() => opener.current?.focus())
  }

  async function confirmKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const confirmation = { pin: form.get('pin')?.toString() || undefined, confirm: true }
    setKeyError(undefined)
    try {
      if (ask === 'reveal') {
        setKey((await revealApiKey(confirmation)).apiKey)
        setRotated(undefined)
      } else {
        const result = await rotateApiKey({ ...confirmation, signOutOthers: form.get('signOutOthers') === 'on' })
        setKey(result.apiKey)
        setRotated({ signedOut: result.signedOut })
        if (result.signedOut > 0) setSignInsVersion((n) => n + 1)
      }
      setAsk(undefined)
      requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
    } catch (e) {
      setKeyError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    }
  }

  function hideKey() {
    setKey(undefined)
    setRotated(undefined)
    requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
  }

  async function newWebhookSecret() {
    setSecretState({ busy: true })
    try {
      setWebhookSecret((await rotateWebhookSecret()).webhookSecret)
      setAskSecret(false)
      setSecretState({ done: true })
    } catch (e) {
      setSecretState({ error: e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e) })
    }
  }

  if (!settings) return <p className="muted">Loading…</p>
  const secret = webhookSecret ?? settings.webhookSecret
  return (
    <section>
      <header className="settings-header">
        <h2>Server</h2>
      </header>
      <form
        className="panel"
        onSubmit={(e) => {
          e.preventDefault()
          const value = new FormData(e.currentTarget).get('auth') as Settings['authenticationRequired']
          void save({ authenticationRequired: value })
        }}
      >
        <label className="field">
          <span className="field-label">Ask for the API key</span>
          <select name="auth" defaultValue={settings.authenticationRequired} onChange={(e) => setAuth(e.target.value as Settings['authenticationRequired'])}>
            <option value="Enabled">Always</option>
            <option value="DisabledForLocalAddresses">Only away from home</option>
          </select>
          <span className="field-help">“Only away from home” lets devices on your own network in without a key.</span>
        </label>
        {(auth ?? settings.authenticationRequired) === 'DisabledForLocalAddresses' && (
          <div className="panel advice">
            <p>
              <strong>Anyone on your Wi-Fi can change settings.</strong> Devices at home get in as the main profile, and a PIN doesn’t keep them out. Choose
              “Always” if that matters.
            </p>
            <details className="advice-more">
              <summary>Using a reverse proxy?</summary>
              <p>
                If Flexarr sits behind a reverse proxy in Docker or on another computer, add that proxy to <code>trustedProxies</code> in config.json, or visitors
                through it are treated as the internet and asked for the key.
              </p>
              <p>
                A reverse proxy on this computer must send <code>X-Forwarded-For</code> (in nginx, <code>proxy_set_header X-Forwarded-For $remote_addr;</code>), or
                everyone through it looks like this computer and gets in. To be safe, set <code>"loopbackNeedsForwarding": true</code> in config.json: requests
                from this computer that don’t say who they’re for then count as the internet (a browser on this computer will need the key too).
              </p>
            </details>
          </div>
        )}
        <SaveBar saved={saved} error={saveError} />
      </form>

      <h3 className="settings-subhead">API key</h3>
      <div className="panel">
        <p className="settings-lede">
          Sign in to a new browser, or set up an app, with this key. Browsers and TVs get a sign-in of their own once connected, so they don’t keep it.
        </p>
        {rotated && (
          <p className="key-note" role="status">
            New key made. The old one stopped working.
            {rotated.signedOut > 0
              ? ` ${rotated.signedOut.toLocaleString()} ${rotated.signedOut === 1 ? 'browser or TV was' : 'browsers and TVs were'} signed out.`
              : ' Browsers and TVs signed in below keep working.'}
          </p>
        )}
        <div className="key-row" ref={keyArea}>
          {key ? (
            <>
              <code className="api-key">{key}</code>
              <CopyButton text={key} what="the API key" />
              <button className="button quiet small" onClick={hideKey}>
                Hide
              </button>
            </>
          ) : (
            <button className="button quiet small" onClick={(e) => open('reveal', e.currentTarget)} disabled={ask !== undefined}>
              Show API key
            </button>
          )}
          <button className="button quiet small" onClick={(e) => open('rotate', e.currentTarget)} disabled={ask !== undefined}>
            Make a new key
          </button>
        </div>
        {ask && (
          <form
            ref={askForm}
            className="key-confirm"
            aria-label={ask === 'reveal' ? 'Show the API key' : 'Make a new API key'}
            onSubmit={(e) => void confirmKey(e)}
            onKeyDown={(e) => e.key === 'Escape' && cancel()}
          >
            {ask === 'reveal' ? (
              <p className="field-help" id="key-confirm-help">
                Anyone who sees the key can manage this server.{mainHasPin ? ' Enter the main profile’s PIN to show it.' : ''}
              </p>
            ) : (
              <>
                <p className="field-help" id="key-confirm-help">
                  Browsers and TVs signed in below keep working, unless you sign them out too. Anything you gave the old key itself (Sonarr or Radarr’s Emby
                  connection, scripts, an older TV app) will need the new one.
                </p>
                <label className="checkbox">
                  <input type="checkbox" name="signOutOthers" /> Also sign out every other browser and TV (do this if the key may have got out: anyone who had it
                  could have signed in)
                </label>
              </>
            )}
            {mainHasPin && (
              <label className="field">
                <span className="field-label">Main profile’s PIN</span>
                <input name="pin" type="password" inputMode="numeric" autoComplete="off" required aria-describedby="key-confirm-help" />
              </label>
            )}
            <div className="form-actions">
              <button type="button" className="button quiet small" onClick={cancel}>
                Cancel
              </button>
              <button type="submit" className="button small" aria-describedby={mainHasPin ? undefined : 'key-confirm-help'}>
                {ask === 'reveal' ? 'Show the key' : 'Replace the key'}
              </button>
            </div>
          </form>
        )}
        {keyError && (
          <p className="error" role="alert">
            {keyError}
          </p>
        )}
      </div>

      <h3 className="settings-subhead">Signed-in browsers and TVs</h3>
      <SignIns key={signInsVersion} />

      <h3 className="settings-subhead">Webhooks</h3>
      <div className="panel">
        <p className="settings-lede">
          In Sonarr and Radarr, add a Webhook connection under Settings › Connect so new downloads appear straight away. If they run in Docker on this machine, use{' '}
          <code>host.docker.internal</code> instead of this address.
        </p>
        {secretState.done && (
          <p className="key-note" role="status">
            New secret made. Give Sonarr, Radarr and Lidarr these new addresses: until then they can’t start scans.
          </p>
        )}
        <ul className="plain-list webhook-list">
          {(['sonarr', 'radarr', 'lidarr'] as const).map((app) => {
            const address = `${origin}/api/v1/webhooks/${app}?secret=${secret ?? '…'}`
            return (
              <li key={app}>
                <code className="folder">{address}</code>
                {secret && <CopyButton text={address} what={`the ${app[0].toUpperCase()}${app.slice(1)} address`} />}
              </li>
            )
          })}
        </ul>
        <p className="field-help">The secret in these addresses can only start scans. Webhooks set up earlier with the API key keep working.</p>
        <button className="button quiet small" onClick={() => setAskSecret(true)}>
          Make a new secret
        </button>
      </div>
      {askSecret && (
        <ConfirmDialog
          title="Make a new webhook secret?"
          confirmLabel={secretState.busy ? 'Making it…' : 'Make a new secret'}
          busy={secretState.busy}
          error={secretState.error}
          onConfirm={() => void newWebhookSecret()}
          onClose={() => {
            setAskSecret(false)
            setSecretState({})
          }}
        >
          <p>Sonarr, Radarr and Lidarr stop starting scans until you give them the new addresses.</p>
        </ConfirmDialog>
      )}

      <h3 className="settings-subhead">Storage</h3>
      <div className="panel">
        <dl className="facts-list">
          <dt>Data and cache</dt>
          <dd>
            <code>{settings.dataDirectory}</code>
          </dd>
          <dt>FFmpeg</dt>
          <dd>
            <code>{settings.ffmpegPath}</code>
          </dd>
        </dl>
      </div>
    </section>
  )
}

// ---------------------------------------------------------------- Import

function ImportSection() {
  const profiles = useApi(getProfiles, [])
  const [target, setTarget] = useState<string>()
  const [path, setPath] = useState('')
  const [lookup, setLookup] = useState(0)
  const source = useApi((signal) => getPlexSource(path || undefined, signal), [lookup])
  const [account, setAccount] = useState<number>()
  const [result, setResult] = useState<PlexImportResult>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const chosen = account ?? source.data?.accounts[0]?.id

  async function run() {
    if (chosen === undefined) return
    setBusy(true)
    setError(undefined)
    try {
      setResult(await importFromPlex(chosen, source.data?.path ?? undefined, target ?? profiles.data?.find((p) => p.isMain)?.id))
    } catch (e) {
      setError(e instanceof ApiError && e.messages.length ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section>
      <header className="settings-header">
        <h2>Bring over from Plex</h2>
      </header>
      <p className="settings-lede">
        Copy what you've watched, and where you stopped, from Plex Media Server. Flexarr matches by file, reads a copy of Plex's database and never changes
        Plex itself. You can run this again at any time; anything you've watched more recently in Flexarr is kept.
      </p>
      <div className="panel">
        {source.loading && <p className="muted">Looking for Plex…</p>}
        {source.data && !source.data.found && (
          <form
            className="field"
            onSubmit={(e) => {
              e.preventDefault()
              setLookup((n) => n + 1)
            }}
          >
            <span className="field-label">Plex wasn't found in the usual places. Where is com.plexapp.plugins.library.db?</span>
            <input type="text" value={path} onChange={(e) => setPath(e.target.value)} placeholder="/path/to/Plug-in Support/Databases/com.plexapp.plugins.library.db" />
            <div className="form-actions">
              <button type="submit" className="button quiet small">
                Look there
              </button>
            </div>
          </form>
        )}
        {source.data?.found && (
          <>
            <p className="muted">
              Found Plex at <code>{source.data.path}</code>
            </p>
            {source.data.accounts.length === 0 ? (
              <p>That Plex server has no watch history to bring over.</p>
            ) : (
              <label className="field">
                <span className="field-label">Whose history</span>
                <select value={chosen} onChange={(e) => setAccount(Number(e.target.value))}>
                  {source.data.accounts.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name}: {a.watched.toLocaleString()} watched, {a.inProgress.toLocaleString()} in progress
                    </option>
                  ))}
                </select>
              </label>
            )}
            {profiles.data && profiles.data.length > 1 && (
              <label className="field">
                <span className="field-label">Into which Flexarr profile</span>
                <select value={target ?? profiles.data.find((p) => p.isMain)?.id} onChange={(e) => setTarget(e.target.value)}>
                  {profiles.data.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {result && (
              <p role="status" className="import-result">
                Brought over {result.matched.toLocaleString()} titles: {result.watched.toLocaleString()} watched and {result.inProgress.toLocaleString()} to resume.
                {result.keptNewer > 0 && ` ${result.keptNewer} you'd watched more recently in Flexarr were left as they were.`}
                {result.unmatched > 0 && ` ${result.unmatched.toLocaleString()} files Plex knows about aren't in your Flexarr libraries.`}
              </p>
            )}
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <div className="form-actions">
              <button className="button" disabled={busy || chosen === undefined} onClick={run}>
                {busy ? 'Bringing history over…' : 'Bring history over'}
              </button>
            </div>
          </>
        )}
      </div>
      {source.data?.found && <PlexPictures path={source.data.path ?? undefined} />}
    </section>
  )
}

function PlexPictures({ path }: { path?: string }) {
  const [result, setResult] = useState<PlexAvatarResult>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  async function run() {
    setBusy(true)
    setError(undefined)
    try {
      setResult(await importPlexAvatars(path))
    } catch (e) {
      setError(e instanceof ApiError && e.messages.length ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel">
      <h3>Profile pictures</h3>
      <p className="muted">Use the pictures from your Plex Home users for the Flexarr profiles with the same names. It replaces any picture those profiles already have.</p>
      {result && (
        <p role="status" className="import-result">
          {result.matched.length > 0 ? `Updated ${result.matched.map((m) => m.profile).join(' and ')}.` : 'No profiles matched a Plex user’s name.'}
          {result.unmatchedPlexUsers.length > 0 && ` No profile is called ${result.unmatchedPlexUsers.join(' or ')}; add one and run this again.`}
        </p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="form-actions">
        <button className="button" disabled={busy} onClick={run}>
          {busy ? 'Fetching pictures…' : 'Use Plex profile pictures'}
        </button>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- Library health

function HealthCheckPanel({ onFinished }: { onFinished: () => void }) {
  const check = useApi(getHealthCheck, [])
  const [requested, setRequested] = useState<Record<string, string>>({})
  const running = check.data?.running ?? false

  // Poll while a check runs; when it finishes, the rest of the report reloads too.
  usePolling(check.reload, running ? 1500 : null)
  const wasRunning = useRef(false)
  useEffect(() => {
    if (running) wasRunning.current = true
    else if (wasRunning.current) {
      wasRunning.current = false
      onFinished()
    }
  }, [running, onFinished])

  const report = check.data
  const labels = { Missing: 'Missing from disk', Empty: 'Empty files', Unreadable: 'Still unreadable' } as const
  return (
    <div className="panel health-check">
      <div className="health-check-head">
        <div>
          <strong>Health check</strong>
          <p className="muted">
            {running && report
              ? `Checking files: ${report.checked.toLocaleString()} of ${report.total.toLocaleString()}`
              : report?.finishedAt
                ? `Last checked ${relativeTime(report.finishedAt)}: ${report.total.toLocaleString()} files, ${report.problems.length ? `${report.problems.length.toLocaleString()} with problems` : 'no problems'}.`
                : 'Looks at every file: whether it’s still on disk, not empty, and readable.'}
          </p>
        </div>
        <button
          className="button"
          disabled={running}
          onClick={async () => {
            await startHealthCheck()
            check.reload()
          }}
        >
          {running ? 'Checking…' : 'Run health check'}
        </button>
      </div>
      {running && report && report.total > 0 && (
        <span className="scan-track" aria-hidden="true">
          <span style={{ width: `${(report.checked / report.total) * 100}%` }} />
        </span>
      )}
      {!running &&
        (['Missing', 'Empty', 'Unreadable'] as const).map((kind) => {
          const found = report?.problems.filter((p) => p.file.problem === kind) ?? []
          if (found.length === 0) return null
          return (
            <div key={kind} className="health-check-group">
              <h4>
                {labels[kind]} ({found.length.toLocaleString()})
              </h4>
              <ul className="health-check-list">
                {found.slice(0, 100).map(({ file, item }) => (
                  <li key={file.fileId}>
                    <span>
                      <strong>{item ? (item.seriesTitle ? `${item.seriesTitle} ${episodeCode(item)}` : item.title) : 'Unknown title'}</strong>
                      <code>{file.path}</code>
                    </span>
                    {item && (
                      <button
                        className="button quiet small"
                        disabled={!!requested[item.id]}
                        onClick={async () => {
                          try {
                            await searchAgain(item.id)
                            setRequested((r) => ({ ...r, [item.id]: 'Search started' }))
                          } catch (e) {
                            setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Search failed' }))
                          }
                        }}
                      >
                        {requested[item.id] ?? 'Search again'}
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          )
        })}
    </div>
  )
}

function HealthSection() {
  const { data, reload } = useApi(getLibraryHealth, [])
  const [requested, setRequested] = useState<Record<string, string>>({})
  const [shown, setShown] = useState(40)
  const [removing, setRemoving] = useState<{ done: number; total: number; freed: number; kept: number; failed: number } | null>(null)
  const [copyErrors, setCopyErrors] = useState<Record<string, string>>({})
  const [confirmAll, setConfirmAll] = useState(false)
  const [matching, setMatching] = useState<ItemSummary>()
  if (!data) return <p className="muted">Checking your library…</p>

  async function upgrade(id: string) {
    try {
      await searchAgain(id)
      setRequested((r) => ({ ...r, [id]: 'Search started' }))
    } catch (e) {
      setRequested((r) => ({ ...r, [id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Search failed' }))
    }
  }

  /** One title at a time, so progress is visible and one failure doesn't stop the rest. */
  async function removeSpares(ids: string[]) {
    let freed = 0
    let kept = 0
    let failed = 0
    setRemoving({ done: 0, total: ids.length, freed, kept, failed })
    for (const [i, id] of ids.entries()) {
      try {
        const result = await removeDuplicates(id)
        freed += result.freedBytes
        // Nothing removed: Sonarr or Radarr use a different copy from the best one, so both stay.
        if (result.removed === 0) kept++
        failed += result.skipped.length
      } catch {
        failed++
      }
      setRemoving({ done: i + 1, total: ids.length, freed, kept, failed })
    }
    reload()
  }

  async function removeCopy(fileId: string) {
    try {
      await deleteCopy(fileId)
      reload()
    } catch (e) {
      setCopyErrors((c) => ({ ...c, [fileId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t delete it' }))
    }
  }

  const score = data.score
  const breakdown = [
    [score.duplicates, 'with spare copies'],
    [score.unreadable, 'unreadable'],
    [score.fileProblems, 'missing or empty files'],
    [score.standardDefinition, 'only in SD'],
    [score.missingArtwork, 'without artwork'],
  ] as const

  return (
    <section className="health">
      <header className="settings-header">
        <h2>Library health</h2>
      </header>

      <div className="panel health-score" data-grade={score.score >= 95 ? 'good' : score.score >= 80 ? 'fair' : 'poor'}>
        <div className="health-score-number">
          <span className="health-score-value">{score.score}</span>
          <span className="health-score-label">Health score</span>
        </div>
        <div>
          <p className="health-score-summary">
            {score.withProblems === 0
              ? `All ${score.titles.toLocaleString()} films and episodes are in good shape.`
              : `${(score.titles - score.withProblems).toLocaleString()} of ${score.titles.toLocaleString()} films and episodes have no problems.`}
          </p>
          <ul className="health-score-breakdown">
            {breakdown
              .filter(([n]) => n > 0)
              .map(([n, label]) => (
                <li key={label}>
                  <strong>{n.toLocaleString()}</strong> {label}
                </li>
              ))}
            {data.held.length > 0 && (
              <li>
                <strong>{data.held.length.toLocaleString()}</strong> {data.held.length === 1 ? 'folder' : 'folders'} on hold
              </li>
            )}
            {(score.artworkNeedsConnection ?? 0) > 0 && (
              <li>
                <strong>{score.artworkNeedsConnection!.toLocaleString()}</strong> without artwork:{' '}
                <Link to="/settings/connections">connect Radarr or Sonarr</Link> to fill it in
              </li>
            )}
          </ul>
        </div>
      </div>

      <HealthCheckPanel onFinished={reload} />

      {data.held.length > 0 && <HeldFolders folders={data.held} onConfirmed={reload} />}

      <HealthGroup
        title="Spare copies"
        count={data.duplicates.length}
        empty="Every title has a single copy."
        lede={`More than one file for the same film or episode. Removing spares frees ${formatSize(data.reclaimableBytes)}; the best copy, and the one Sonarr or Radarr use, always stay.`}
        action={
          data.duplicates.length > 0 && (
            <button className="button danger small" disabled={removing !== null && removing.done < removing.total} onClick={() => setConfirmAll(true)}>
              Remove all spare copies ({formatSize(data.reclaimableBytes)})
            </button>
          )
        }
      >
        {removing && (
          <div className="health-progress" role="status">
            <span>
              {removing.done < removing.total
                ? `Removing spare copies: ${removing.done.toLocaleString()} of ${removing.total.toLocaleString()} titles`
                : removing.total - removing.kept - removing.failed > 0
                  ? `Removed spare copies of ${(removing.total - removing.kept - removing.failed).toLocaleString()} titles, freeing ${formatSize(removing.freed)}.`
                  : 'Nothing was removed.'}
              {removing.done === removing.total &&
                removing.kept > 0 &&
                ` ${removing.kept.toLocaleString()} kept every copy: Sonarr or Radarr use a different copy from the best one. Open a title to delete the copy you don’t want.`}
              {removing.failed > 0 && ` ${removing.failed} couldn’t be removed (see the server log).`}
            </span>
            <span className="scan-track" aria-hidden="true">
              <span style={{ width: `${(removing.done / Math.max(1, removing.total)) * 100}%` }} />
            </span>
          </div>
        )}
        {data.duplicates.slice(0, shown).map((d) => (
          <details key={d.item.id} className="health-row-expandable">
            <summary>
              <HealthRowMain item={d.item} detail={`${d.files.length} copies, ${formatSize(d.reclaimableBytes)} spare`} />
              <button
                className="button quiet small"
                onClick={(e) => {
                  e.preventDefault()
                  void removeSpares([d.item.id])
                }}
              >
                Remove spares
              </button>
            </summary>
            <ul className="health-copies">
              {d.files.map((f) => {
                // Copies identical to the best: which one stays is whichever Sonarr or Radarr use.
                const best = d.files.find((x) => x.keep)!
                const same = (x: typeof f) => x.size === best.size && x.resolution === best.resolution
                const identical = same(f) && d.files.filter(same).length > 1
                return (
                  <li key={f.id}>
                    <span className={f.keep || identical ? 'tag keep' : 'tag'}>{identical ? 'Identical' : f.keep ? 'Best' : 'Spare'}</span>
                    <span title={f.path}>{fileName(f.path)}</span>
                    <span className="muted">{[f.resolution, f.videoCodec?.toUpperCase(), formatSize(f.size)].filter(Boolean).join(', ')}</span>
                    <button className="text-button" onClick={() => removeCopy(f.id)}>
                      Delete this copy
                    </button>
                    {copyErrors[f.id] && (
                      <span className="error" role="alert">
                        {copyErrors[f.id]}
                      </span>
                    )}
                  </li>
                )
              })}
            </ul>
          </details>
        ))}
        {data.duplicates.length > shown && (
          <button className="button quiet small health-more" onClick={() => setShown((n) => n + 100)}>
            Show more ({(data.duplicates.length - shown).toLocaleString()} left)
          </button>
        )}
      </HealthGroup>

      <HealthGroup title="Files that can’t be read" count={data.unreadable.length} empty="Every file opened cleanly." lede="Usually a damaged or unfinished download. A new copy fixes playback.">
        {data.unreadable.map((u) => (
          <div key={u.fileId} className="health-row">
            <HealthRowMain item={u.item} file={u.path} detail={`Can’t be read: ${u.error.split(': ').at(-1)?.toLowerCase()}`} />
            {u.item &&
              (data.downloading[u.item.id] ? (
                <DownloadingNote state={data.downloading[u.item.id]} />
              ) : (
                <button className="button quiet small" onClick={() => upgrade(u.item!.id)} disabled={!!requested[u.item.id]}>
                  {requested[u.item.id] ?? 'Get a new copy'}
                </button>
              ))}
          </div>
        ))}
      </HealthGroup>

      <HealthGroup title="Only in standard definition" count={data.standardDefinitionTotal} empty="Every film has an HD copy." lede="Your best copy is below 720p. Radarr can look for a better one.">
        {data.standardDefinition.map((item) => (
          <div key={item.id} className="health-row">
            <HealthRowMain item={item} detail="Best copy is SD" />
            {data.downloading[item.id] ? (
              <DownloadingNote state={data.downloading[item.id]} />
            ) : (
              <button className="button quiet small" onClick={() => upgrade(item.id)} disabled={!!requested[item.id]}>
                {requested[item.id] ?? 'Find a better copy'}
              </button>
            )}
          </div>
        ))}
      </HealthGroup>

      {data.matchedByTitle.length > 0 && (
        <HealthGroup
          title="Check these matches"
          count={data.matchedByTitle.length}
          lede="Sonarr and Radarr don’t manage these, so their details came from searching for the folder’s name, which can find the wrong one: an older show with the same name, or a remake. Fixing a match adds the title to Sonarr or Radarr. Ignore one that’s right and it stays as it is."
        >
          {data.matchedByTitle.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail={requested[item.id] ?? 'Matched by title'} />
              <div className="health-row-actions">
                <button
                  className="button quiet small"
                  disabled={requested[item.id] === 'Keeping this match…'}
                  onClick={async () => {
                    setRequested((r) => ({ ...r, [item.id]: 'Keeping this match…' }))
                    try {
                      await keepMatch(item.id)
                      reload()
                    } catch (e) {
                      setRequested((r) => ({ ...r, [item.id]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t keep this match' }))
                    }
                  }}
                >
                  Ignore
                </button>
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  Fix match
                </button>
              </div>
            </div>
          ))}
        </HealthGroup>
      )}

      {data.missingArtwork.length > 0 && (
        <HealthGroup
          title="Missing artwork"
          count={data.missingArtwork.length}
          lede={
            score.missingArtwork === 0 && (score.artworkNeedsConnection ?? 0) > 0
              ? 'Artwork comes from Radarr and Sonarr. Connect them in Settings › Connections to fill it in.'
              : 'Not found in Sonarr or Radarr. Fix the match to choose the right title, or rename the folder to “Title (Year)”.'
          }
        >
          {data.missingArtwork.map((item) => (
            <div key={item.id} className="health-row">
              <HealthRowMain item={item} detail="No poster or details" />
              {(item.kind === 'Movie' || item.kind === 'Series') && (
                <button className="button quiet small" onClick={() => setMatching(item)}>
                  Fix match
                </button>
              )}
            </div>
          ))}
        </HealthGroup>
      )}

      {matching && <FixMatchDialog item={matching} onFixed={reload} onClose={() => setMatching(undefined)} />}

      {confirmAll && (
        <ConfirmDialog
          title={`Remove ${data.duplicates.length.toLocaleString()} spare copies?`}
          cancelLabel="Keep them"
          confirmLabel="Remove spare copies"
          danger
          onClose={() => setConfirmAll(false)}
          onConfirm={() => {
            setConfirmAll(false)
            void removeSpares(data.duplicates.map((d) => d.item.id))
          }}
        >
          <p>
            This deletes the spare files from disk, freeing {formatSize(data.reclaimableBytes)}. For each title the best copy, and the one Sonarr or Radarr use, are kept.
            You can’t undo this.
          </p>
        </ConfirmDialog>
      )}
    </section>
  )
}

const fileName = (path: string) => path.split('/').at(-1) ?? path

/**
 * Folders whose files the scans are keeping: they looked unmounted or cleared out. If they really were cleared out,
 * confirming lets the next scan hide them (and remove them a day later).
 */
function HeldFolders({ folders, onConfirmed }: { folders: HeldFolder[]; onConfirmed: () => void }) {
  const [asking, setAsking] = useState<HeldFolder | null>(null)
  const [errors, setErrors] = useState<Record<number, string>>({})
  const why = (f: HeldFolder) =>
    f.reason === 'FolderMissing'
      ? 'The folder isn’t there. Is its drive or share connected?'
      : f.reason === 'FolderEmpty'
        ? `The folder is empty but held ${f.known.toLocaleString()} files. Is its drive or share mounted?`
        : `${f.missing.toLocaleString()} of its ${f.known.toLocaleString()} files went at once. If that’s a clear-out, it goes through by itself after a day of scans finding the same.`

  async function confirm(f: HeldFolder) {
    setAsking(null)
    try {
      await confirmHeldRemoval(f)
      onConfirmed()
    } catch (e) {
      setErrors((x) => ({ ...x, [f.folderId]: e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t confirm it' }))
      if (e instanceof ApiError && e.status === 409) onConfirmed() // Show what the scans found now.
    }
  }

  return (
    <HealthGroup
      title="Folders on hold"
      count={folders.length}
      lede="Scans are keeping these folders’ titles, in case a drive or share is having trouble. Titles you deleted on purpose stay listed until you confirm it."
    >
      {folders.map((f) => (
        <div key={f.folderId} className="health-row health-row-held">
          <span className="health-row-main">
            <span className="health-row-text health-row-wraps">
              <strong>{f.library}</strong>
              <code className="health-row-path">{f.path}</code>
              <span className="muted">{why(f)}</span>
              {errors[f.folderId] && (
                <span className="error" role="alert">
                  {errors[f.folderId]}
                </span>
              )}
            </span>
          </span>
          {f.canConfirm &&
            (f.confirmed ? (
              <span className="tag">Removal confirmed, scanning</span>
            ) : (
              <button className="button danger small" onClick={() => setAsking(f)}>
                Confirm removal
              </button>
            ))}
        </div>
      ))}
      {asking && (
        <ConfirmDialog
          title={`Remove ${asking.missing.toLocaleString()} ${heldNoun(asking)} from ${asking.library}?`}
          cancelLabel="Not yet"
          confirmLabel="Remove from Flexarr"
          danger
          onClose={() => setAsking(null)}
          onConfirm={() => void confirm(asking)}
        >
          <p>Flexarr can’t find them in this folder:</p>
          <p>
            <code className="confirm-dialog-path">{asking.path}</code>
          </p>
          <p>
            Flexarr hides them now and removes them a day later. Watch history is kept for 30 days in case they come back. No files are touched. If the
            drive is just disconnected, reconnect it instead.
          </p>
        </ConfirmDialog>
      )}
    </HealthGroup>
  )
}

/** What a held folder's missing files are, by the kind of library: "4 films". */
function heldNoun(f: HeldFolder) {
  const one = f.missing === 1
  return f.libraryType === 'Movies' ? (one ? 'film' : 'films') : f.libraryType === 'TvShows' ? (one ? 'episode' : 'episodes') : f.libraryType === 'Music' ? (one ? 'track' : 'tracks') : one ? 'file' : 'files'
}

/** In place of a search button: Sonarr or Radarr already have a new copy on the way. */
function DownloadingNote({ state }: { state: LibraryHealth['downloading'][string] }) {
  const status = state.status?.toLowerCase() ?? ''
  const text =
    state.progress >= 0.999 || status.includes('import')
      ? 'Downloaded, waiting to import'
      : status.includes('queued') || status.includes('delay')
        ? 'Queued to download'
        : `Downloading ${Math.round(state.progress * 100)}%`
  return <span className="tag health-downloading">{text}</span>
}

/** A titled group of problems with a count, a line on what it means, and an optional action for all of them. */
function HealthGroup({ title, count, lede, empty, action, children }: { title: string; count: number; lede: string; empty?: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="health-group">
      <header>
        <div>
          <h3 className="settings-subhead">
            {title} <span className="health-count">{count.toLocaleString()}</span>
          </h3>
          {count > 0 && <p className="muted">{lede}</p>}
        </div>
        {action}
      </header>
      {count === 0 ? <p className="muted">{empty}</p> : <div className="panel health-rows">{children}</div>}
    </section>
  )
}

/** The poster, the title (an episode's show, number and name), and what's wrong. */
function HealthRowMain({ item, file, detail }: { item: ItemSummary | null; file?: string; detail: string }) {
  return (
    <span className="health-row-main">
      <span className="health-row-art">{item && <Artwork src={item.artwork.poster} title={item.title} width={92} shape="poster" />}</span>
      <span className="health-row-text">
        {item ? (
          <Link to={item.kind === 'Episode' && item.seriesId ? `/item/${item.id}` : `/item/${item.id}`} className="health-row-title">
            {item.kind === 'Episode' ? `${item.seriesTitle ?? ''} ${episodeCode(item)} ${item.title}` : `${item.title}${item.year ? ` (${item.year})` : ''}`}
          </Link>
        ) : (
          <strong>Unknown title</strong>
        )}
        <span className="muted">
          {detail}
          {file && (
            <>
              {' · '}
              <span title={file}>{fileName(file)}</span>
            </>
          )}
        </span>
      </span>
    </span>
  )
}

// ---------------------------------------------------------------- Profiles

const profileColours = [
  { value: '#a9c2ec', name: 'Blue' },
  { value: '#e8a0bf', name: 'Pink' },
  { value: '#9fd8b9', name: 'Green' },
  { value: '#f2c879', name: 'Yellow' },
  { value: '#c7a6f2', name: 'Purple' },
  { value: '#f0a27e', name: 'Orange' },
  { value: '#86d0e0', name: 'Teal' },
]

/** A new profile's colour: the first no one has yet, so profiles look different in the picker. */
function freeColour(profiles: Profile[]) {
  const taken = new Set(profiles.map((p) => p.color.toLowerCase()))
  return (profileColours.find((c) => !taken.has(c.value)) ?? profileColours[profiles.length % profileColours.length]).value
}

function ProfilesSection() {
  const [version, setVersion] = useState(0)
  const { data } = useApi(getProfiles, [version])
  const libraries = useApi(getAllLibraries, []).data ?? []
  const [editing, setEditing] = useState<Profile | 'new'>()
  const reload = () => {
    setEditing(undefined)
    setVersion((v) => v + 1)
  }

  return (
    <section>
      <header className="settings-header">
        <h2>Profiles</h2>
        {editing === undefined && (
          <button className="button small" onClick={() => setEditing('new')}>
            Add a profile
          </button>
        )}
      </header>
      <p className="settings-lede">
        Everyone in the house can have their own Continue Watching, watched history and Up Next. With more than one profile, Flexarr asks who’s watching when it opens. A
        PIN keeps a profile private (handy for keeping kids out of yours).
      </p>
      {editing === 'new' && (
        <ProfileForm defaultColour={freeColour(data ?? [])} libraries={libraries} onDone={reload} onCancel={() => setEditing(undefined)} />
      )}
      {data?.map((p) =>
        // The form takes the profile's place in the list, rather than showing it twice.
        editing !== 'new' && editing?.id === p.id ? (
          <ProfileForm
            // A fresh form for each profile: one opened over another mustn't carry its settings across.
            key={p.id}
            profile={editing}
            defaultColour={freeColour(data)}
            libraries={libraries}
            onDone={reload}
            onCancel={() => setEditing(undefined)}
          />
        ) : (
          <div key={p.id} className="panel profile-row">
            <Avatar profile={p} size={48} />
            <div>
              <strong>{p.name}</strong>
              <p className="muted">
                {[p.isMain && 'Main profile', p.hasPin && 'PIN protected', hiddenSummary(p, libraries), p.isCurrent && 'You’re using this one'].filter(Boolean).join('. ')}
              </p>
            </div>
            <div className="library-row-actions">
              <button className="button quiet small" onClick={() => setEditing(p)}>
                Edit
              </button>
              {!p.isMain && (
                <button
                  className="button danger small"
                  onClick={async () => {
                    if (!confirm(`Remove ${p.name}’s profile? Their watch history goes with it.`)) return
                    await deleteProfile(p.id)
                    reload()
                  }}
                >
                  Remove
                </button>
              )}
            </div>
          </div>
        ),
      )}
    </section>
  )
}

/** An object URL for previewing a chosen file, released when it changes. */
function usePreviewUrl(file: File | null | undefined) {
  const url = useMemo(() => (file ? URL.createObjectURL(file) : undefined), [file])
  useEffect(
    () => () => {
      if (url) URL.revokeObjectURL(url)
    },
    [url],
  )
  return url
}

/** "Can't see Movies", or nothing when the profile sees every library. */
function hiddenSummary(profile: Profile, libraries: Library[]) {
  const names = libraries.filter((l) => profile.hiddenLibraryIds.includes(l.id)).map((l) => l.name)
  return names.length > 0 && `Can’t see ${names.join(', ')}`
}

function ProfileForm({
  profile,
  defaultColour,
  libraries,
  onDone,
  onCancel,
}: {
  profile?: Profile
  defaultColour: string
  libraries: Library[]
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(profile?.name ?? '')
  const [hidden, setHidden] = useState<string[]>(profile?.hiddenLibraryIds ?? [])
  const [picture, setPicture] = useState<File | null>()
  const preview = usePreviewUrl(picture)
  const shownImage = picture === null ? null : (preview ?? profile?.image ?? null)
  const [color, setColor] = useState(profile?.color ?? defaultColour)
  const [pin, setPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [removePin, setRemovePin] = useState(false)
  // Changing the main profile's PIN takes the one it has now (it guards the API key), or the key if it's forgotten.
  const [currentPin, setCurrentPin] = useState('')
  const [apiKey, setApiKey] = useState<string>()
  const asksForCurrentPin = profile?.isMain === true && profile.hasPin && (pin !== '' || removePin)
  const [errors, setErrors] = useState<string[]>([])

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (pin !== '' && !removePin && pin !== confirmPin) {
      setErrors(['The two PINs don’t match. Type the same one in both.'])
      return
    }
    try {
      const confirmation = asksForCurrentPin ? (apiKey !== undefined ? { apiKey: apiKey.trim() } : { currentPin }) : {}
      const saved = profile
        ? await updateProfile(profile.id, { name, color, pin: pin || undefined, removePin, hiddenLibraryIds: hidden, ...confirmation })
        : await createProfile({ name, color, pin: pin || undefined, hiddenLibraryIds: hidden })
      if (picture) await uploadProfileImage(saved.id, picture)
      else if (picture === null && profile?.image) await removeProfileImage(saved.id)
      onDone()
    } catch (err) {
      setErrors(err instanceof ApiError && err.messages.length ? err.messages : [String(err)])
    }
  }

  return (
    <form className="panel" onSubmit={submit} aria-label={profile ? `Edit ${profile.name}` : 'New profile'}>
      <div className="field-row">
        <label className="grow">
          Name
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} required maxLength={60} />
        </label>
      </div>
      <fieldset className="field profile-pin">
        <legend className="visually-hidden">PIN</legend>
        <div className="field-row">
          <label>
            {profile?.hasPin ? 'New PIN (optional)' : 'PIN (optional)'}
            <input
              type="password"
              inputMode="numeric"
              autoComplete="new-password"
              value={pin}
              disabled={removePin}
              onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
              placeholder="4 to 8 digits"
            />
          </label>
          <label>
            {profile?.hasPin ? 'Confirm new PIN' : 'Confirm PIN'}
            <input
              type="password"
              inputMode="numeric"
              autoComplete="new-password"
              value={confirmPin}
              disabled={removePin || pin === ''}
              required={pin !== '' && !removePin}
              onChange={(e) => setConfirmPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
            />
          </label>
          {asksForCurrentPin && apiKey === undefined && (
            <label>
              Current PIN
              <input
                type="password"
                inputMode="numeric"
                autoComplete="off"
                required
                value={currentPin}
                onChange={(e) => setCurrentPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
              />
            </label>
          )}
          {asksForCurrentPin && apiKey !== undefined && (
            <label>
              API key
              <input type="password" autoComplete="off" required value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
            </label>
          )}
        </div>
        {asksForCurrentPin && apiKey === undefined && (
          <span className="field-help">
            Changing it takes the current PIN. Forgotten it?{' '}
            <button type="button" className="text-button" onClick={() => setApiKey('')}>
              Use the API key instead
            </button>{' '}
            (it’s in config.json in the server’s data folder).
          </span>
        )}
        {profile?.isMain && <span className="field-help">The main profile’s PIN also unlocks Settings away from home.</span>}
        {profile?.hasPin && (
          <label className="checkbox">
            <input
              type="checkbox"
              checked={removePin}
              onChange={(e) => {
                setRemovePin(e.target.checked)
                setPin('')
                setConfirmPin('')
              }}
            />{' '}
            Remove the PIN
          </label>
        )}
      </fieldset>
      <div className="field profile-picture">
        <Avatar profile={{ name: name || '?', color, image: shownImage }} size={72} />
        <div>
          <span className="field-label">Picture</span>
          <div className="form-actions">
            <label className="button quiet small file-button">
              {shownImage ? 'Change picture' : 'Choose a picture'}
              <input type="file" accept="image/*" onChange={(e) => setPicture(e.target.files?.[0])} />
            </label>
            {shownImage && (
              <button type="button" className="button quiet small" onClick={() => setPicture(null)}>
                Remove picture
              </button>
            )}
          </div>
        </div>
      </div>
      <div className="field">
        <span className="field-label">{shownImage ? 'Colour (shown behind the picture while it loads)' : 'Colour'}</span>
        <div className="swatches" role="radiogroup" aria-label="Colour">
          {profileColours.map((c) => (
            <button
              type="button"
              key={c.value}
              role="radio"
              aria-checked={c.value === color.toLowerCase()}
              aria-label={c.name}
              title={c.name}
              className="swatch"
              style={{ background: c.value }}
              onClick={() => setColor(c.value)}
            />
          ))}
        </div>
      </div>
      {libraries.length > 1 && (
        <fieldset className="field profile-libraries">
          <legend className="field-label">Libraries {profile ? `${profile.name} can` : 'this profile can'} see</legend>
          <p className="muted">Unticked libraries are hidden everywhere on this profile: home, search, Discover, Coming soon and requests.</p>
          {libraries.map((l) => (
            <label key={l.id} className="checkbox">
              <input
                type="checkbox"
                checked={!hidden.includes(l.id)}
                onChange={(e) => setHidden(e.target.checked ? hidden.filter((id) => id !== l.id) : [...hidden, l.id])}
              />{' '}
              {l.name}
            </label>
          ))}
        </fieldset>
      )}
      {errors.length > 0 && (
        <ul className="error form-errors" role="alert">
          {errors.map((m) => (
            <li key={m}>{m}</li>
          ))}
        </ul>
      )}
      <div className="form-actions">
        <button type="button" className="button quiet" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="button">
          {profile ? 'Save profile' : 'Add profile'}
        </button>
      </div>
    </form>
  )
}

// ---------------------------------------------------------------- Home and Browse

function LayoutSection() {
  const { data, error } = useApi(getLayout, [])
  const [draft, setDraft] = useState<Layout>()
  const [saved, setSaved] = useState(false)
  const layout = draft ?? data

  if (error) return <p className="error">{error.message}</p>
  if (!layout) return <p className="muted">Loading…</p>

  const change = (key: keyof Layout, rows: RowChoice[]) => {
    setDraft({ ...layout, [key]: rows })
    setSaved(false)
  }

  return (
    <section>
      <header className="settings-header">
        <h2>Home and Browse</h2>
      </header>
      <p className="settings-lede">Choose which rows appear on Home and on each library’s Browse view, and in what order. This applies to every profile.</p>
      <form
        onSubmit={async (e) => {
          e.preventDefault()
          await saveLayout(layout)
          setSaved(true)
        }}
      >
        <RowList title="Home" rows={layout.home} onChange={(rows) => change('home', rows)} />
        <RowList title="Film libraries (Browse)" rows={layout.movies} onChange={(rows) => change('movies', rows)} />
        <RowList title="TV libraries (Browse)" rows={layout.shows} onChange={(rows) => change('shows', rows)} />
        <div className="form-actions">
          {saved && (
            <span className="muted" role="status">
              Saved. Home and Browse show the new layout next time they load.
            </span>
          )}
          <button type="submit" className="button" disabled={!draft}>
            Save layout
          </button>
        </div>
      </form>
    </section>
  )
}

function RowList({ title, rows, onChange }: { title: string; rows: RowChoice[]; onChange: (rows: RowChoice[]) => void }) {
  const move = (from: number, to: number) => {
    const next = [...rows]
    const [row] = next.splice(from, 1)
    next.splice(to, 0, row)
    onChange(next)
  }
  return (
    <fieldset className="panel row-list">
      <legend className="settings-subhead">{title}</legend>
      <ol>
        {rows.map((row, i) => (
          <li key={row.id}>
            <label className="checkbox">
              <input type="checkbox" checked={row.enabled} onChange={(e) => onChange(rows.map((r) => (r.id === row.id ? { ...r, enabled: e.target.checked } : r)))} />{' '}
              <span className={row.enabled ? undefined : 'muted'}>{row.label}</span>
            </label>
            <span className="row-list-move">
              <button type="button" className="icon-button" aria-label={`Move ${row.label} up`} disabled={i === 0} onClick={() => move(i, i - 1)}>
                <ChevronUp />
              </button>
              <button type="button" className="icon-button" aria-label={`Move ${row.label} down`} disabled={i === rows.length - 1} onClick={() => move(i, i + 1)}>
                <ChevronDown />
              </button>
            </span>
          </li>
        ))}
      </ol>
    </fieldset>
  )
}

// ---------------------------------------------------------------- Changelog

function ChangelogSection() {
  const { data, error } = useApi(getChangelog, [])
  const { data: status } = useApi(getSystemStatus, [])
  return (
    <section>
      <header className="settings-header">
        <h2>Changelog</h2>
      </header>
      {/* The server's version is "0.8.0+<commit>"; the commit is for bug reports, not people. */}
      {status && <p className="settings-lede">You’re running Flexarr {status.version.split('+')[0]}.</p>}
      {error && <p className="error">The changelog couldn’t be loaded: {error.message}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      {data && <Changelog markdown={data} />}
    </section>
  )
}

/** CHANGELOG.md's shape: "## date", "### change", "- point" (with **bold**), and plain paragraphs. */
function Changelog({ markdown }: { markdown: string }) {
  const blocks: { kind: 'date' | 'title' | 'text'; text: string; points?: string[] }[] = []
  for (const line of markdown.split('\n')) {
    if (line.startsWith('# ')) continue
    if (line.startsWith('## ')) blocks.push({ kind: 'date', text: line.slice(3) })
    else if (line.startsWith('### ')) blocks.push({ kind: 'title', text: line.slice(4), points: [] })
    else if (line.startsWith('- ') && blocks.at(-1)?.points) blocks.at(-1)!.points!.push(line.slice(2))
    else if (line.trim()) blocks.push({ kind: 'text', text: line })
  }
  const inline = (text: string) =>
    text.split(/(\*\*[^*]+\*\*)/).map((part, i) => (part.startsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part))
  return (
    <div className="changelog">
      {blocks.map((b, i) =>
        b.kind === 'date' ? (
          <h3 key={i} className="settings-subhead">
            {b.text}
          </h3>
        ) : b.kind === 'title' ? (
          <div key={i} className="changelog-entry">
            <h4>{b.text}</h4>
            <ul>
              {b.points!.map((p) => (
                <li key={p}>{inline(p)}</li>
              ))}
            </ul>
          </div>
        ) : (
          <p key={i} className="settings-lede">
            {inline(b.text)}
          </p>
        ),
      )}
    </div>
  )
}

// ---------------------------------------------------------------- Remote access

const zoneLabel = { Local: 'your home network', Tailnet: 'Tailscale', Internet: 'the internet' } as const

function RemoteAccessSection() {
  const [version, setVersion] = useState(0)
  const remote = useApi(getRemoteAccess, [version])
  const profiles = useApi(getProfiles, [version])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [copied, setCopied] = useState(false)

  async function run(action: () => Promise<RemoteAccess>) {
    setBusy(true)
    setError(undefined)
    try {
      await action()
      setVersion((v) => v + 1)
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (!remote.data) return <p className="muted">{remote.error ? remote.error.message : 'Checking Tailscale…'}</p>
  const { tailscale: ts } = remote.data

  return (
    <section>
      <header className="settings-header">
        <h2>Remote access</h2>
      </header>
      <p className="settings-lede">
        Watch away from home without opening ports on your router. Flexarr uses Tailscale: only devices signed in to your tailnet can reach it, traffic is
        encrypted end to end, and each person is signed in to their own profile by their Tailscale account, with no passwords to remember.
      </p>
      <p className="connection-note">
        You’re connected from {zoneLabel[remote.data.yourConnection]}
        {remote.data.yourTailscaleLogin && <> as {remote.data.yourTailscaleLogin}</>}.
      </p>

      <div className="panel remote-panel">
        <div className="connection-status">
          <span className={ts.privateAccess ? 'status-dot ok' : ts.running ? 'status-dot' : 'status-dot bad'} aria-hidden="true" />
          <strong>Tailscale</strong>
          <span className="muted">
            {!ts.installed
              ? 'Not installed on the server'
              : !ts.running
                ? 'Installed, not connected'
                : ts.privateAccess
                  ? 'Flexarr is available on your tailnet'
                  : `Connected as ${ts.dnsName}`}
          </span>
        </div>

        {ts.problem && <p className="error">{ts.problem}</p>}
        {!ts.installed && (
          <p>
            Install Tailscale on the server from <a href="https://tailscale.com/download">tailscale.com/download</a>, sign in, then come back here.
          </p>
        )}

        {ts.running && (
          <>
            {ts.privateAccess && ts.url && (
              <div className="remote-url">
                <span className="field-label">Your private address</span>
                <div className="remote-url-row">
                  <code>{ts.url}</code>
                  <button
                    className="button quiet small"
                    onClick={() => {
                      void navigator.clipboard?.writeText(ts.url!)
                      setCopied(true)
                      setTimeout(() => setCopied(false), 2000)
                    }}
                  >
                    {copied ? 'Copied' : 'Copy'}
                  </button>
                </div>
                <p className="field-help">
                  Open it on any phone, laptop or Apple TV with the Tailscale app signed in to your tailnet. It has a real HTTPS certificate.
                </p>
              </div>
            )}
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <div className="form-actions">
              <button className={ts.privateAccess ? 'button danger' : 'button'} disabled={busy} onClick={() => run(() => setTailscaleAccess(!ts.privateAccess))}>
                {busy ? 'Working…' : ts.privateAccess ? 'Turn off remote access' : 'Turn on remote access with Tailscale'}
              </button>
            </div>
          </>
        )}
      </div>

      {ts.running && remote.data.links.length > 0 && (
        <>
          <h3 className="settings-subhead">Who can sign in</h3>
          <p className="settings-lede">
            People on your tailnet, and the profile each one opens as. Anyone without a profile is turned away, even though Tailscale lets them reach the
            server.
          </p>
          <div className="panel">
            <ul className="plain-list tailnet-people">
              {remote.data.links.map((person) => (
                <li key={person.login}>
                  <span>
                    <strong>{person.name}</strong>
                    <span className="muted"> {person.login}</span>
                  </span>
                  <select
                    aria-label={`Profile for ${person.name}`}
                    value={person.profileId ?? ''}
                    disabled={busy}
                    onChange={(e) => run(() => linkTailscale(person.login, e.target.value || null))}
                  >
                    <option value="">Not allowed</option>
                    {profiles.data?.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </li>
              ))}
            </ul>
          </div>
        </>
      )}

      {ts.privateAccess && !remote.data.mainProfileHasPin && (
        <p className="panel advice">
          Anyone using a browser or TV you’ve signed in can switch to the main profile, which can change settings, because it has no PIN. Give it one in{' '}
          <a href="/settings/profiles">Profiles</a>.
        </p>
      )}

      <h3 className="settings-subhead">Sharing beyond your tailnet</h3>
      {ts.publicAccess ? (
        <p className="panel advice error">
          Tailscale Funnel is publishing Flexarr’s address to the whole internet. Turn remote access off and on again to make it private.
        </p>
      ) : (
        <p className="settings-lede">
          Flexarr isn’t on the public internet, and that’s the safe default. To share with someone, invite them to your tailnet (Tailscale’s free plan
          includes this) and give them a profile above.
          {remote.data.cloudflareTunnelRunning && ' A Cloudflare Tunnel runs on this server, but it doesn’t route to Flexarr.'} If you ever route a tunnel
          or Funnel to Flexarr, those requests are never treated as your home network and still need the API key.
        </p>
      )}
    </section>
  )
}
