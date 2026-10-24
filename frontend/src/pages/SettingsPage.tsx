import { useEffect, useState, type FormEvent } from 'react'
import { NavLink, useParams } from 'react-router'
import {
  ApiError,
  createLibrary,
  deleteLibrary,
  getActivity,
  getApiKey,
  getArrStatus,
  getSettings,
  saveSettings,
  scanLibrary,
  testArr,
  type ArrKind,
  type ArrStatus,
  type HardwareAcceleration,
  type Library,
  type LibraryType,
  type Settings,
} from '../api/client'
import { FolderPicker } from '../components/FolderPicker'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { episodeCode, formatClock, libraryTypeLabel, relativeTime } from '../lib/format'

const sections = [
  { id: 'libraries', label: 'Libraries' },
  { id: 'playback', label: 'Playback' },
  { id: 'connections', label: 'Sonarr and Radarr' },
  { id: 'activity', label: 'Activity' },
  { id: 'server', label: 'Server' },
] as const

export function SettingsPage() {
  const { section = 'libraries' } = useParams()

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
        {section === 'playback' && <PlaybackSection />}
        {section === 'connections' && <ConnectionsSection />}
        {section === 'activity' && <ActivitySection />}
        {section === 'server' && <ServerSection />}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- Libraries

function LibrariesSection() {
  const { libraries, reload } = useLibraries()
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
  const status = useApi(getArrStatus, [settings])
  const [connections, setConnections] = useState<DraftConnection[]>([])
  const [mappings, setMappings] = useState<Settings['pathMappings']>([])
  const [tests, setTests] = useState<Record<number, ArrStatus | 'testing'>>({})

  useEffect(() => {
    if (!settings) return
    setConnections(settings.arr.map((a) => ({ ...a, apiKey: '' })))
    setMappings(settings.pathMappings)
  }, [settings])

  if (!settings) return <p className="muted">Loading…</p>

  const update = (i: number, change: Partial<DraftConnection>) => setConnections((c) => c.map((x, j) => (j === i ? { ...x, ...change } : x)))

  return (
    <section>
      <header className="settings-header">
        <h2>Sonarr and Radarr</h2>
      </header>
      <p className="settings-lede">
        Flexarr reads artwork, descriptions, cast and trailers from your Sonarr and Radarr, so no other accounts or API keys are needed.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          void save({ arr: connections.map((c) => ({ name: c.name, kind: c.kind, baseUrl: c.baseUrl, apiKey: c.apiKey || undefined })), pathMappings: mappings })
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
        <button
          type="button"
          className="button quiet small"
          onClick={() => setConnections((c) => [...c, { name: 'Sonarr', kind: 'Sonarr', baseUrl: 'http://localhost:8989', apiKey: '', hasApiKey: false }])}
        >
          Add a connection
        </button>

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
  const [version, setVersion] = useState(0)
  const { data } = useApi(getActivity, [version])
  useEffect(() => {
    const timer = setInterval(() => setVersion((v) => v + 1), 5000)
    return () => clearInterval(timer)
  }, [])
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
  const origin = window.location.origin

  if (!settings) return <p className="muted">Loading…</p>
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
          <select name="auth" defaultValue={settings.authenticationRequired}>
            <option value="Enabled">Always</option>
            <option value="DisabledForLocalAddresses">Only away from home</option>
          </select>
          <span className="field-help">“Only away from home” lets devices on your own network in without a key.</span>
        </label>
        <SaveBar saved={saved} error={saveError} />
      </form>

      <h3 className="settings-subhead">API key</h3>
      <div className="panel">
        <p className="settings-lede">Apps and the Apple TV app use this key to connect.</p>
        {key ? (
          <code className="api-key">{key}</code>
        ) : (
          <button className="button quiet small" onClick={() => getApiKey().then((k) => setKey(k.apiKey))}>
            Show API key
          </button>
        )}
      </div>

      <h3 className="settings-subhead">Webhooks</h3>
      <div className="panel">
        <p className="settings-lede">
          In Sonarr and Radarr, add a Webhook connection under Settings › Connect so new downloads appear straight away. If they run in Docker on this machine, use{' '}
          <code>host.docker.internal</code> instead of this address.
        </p>
        {(['sonarr', 'radarr', 'lidarr'] as const).map((app) => (
          <code key={app} className="folder">
            {origin}/api/v1/webhooks/{app}?apikey={key ?? '…'}
          </code>
        ))}
      </div>

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
