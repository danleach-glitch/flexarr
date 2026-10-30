import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import {
  addArrWebhook,
  createLibrary,
  createProfile,
  discoverArrs,
  getArrFolders,
  getLibraries,
  getProfiles,
  getSettings,
  saveSettings,
  saveSetupGuide,
  testArr,
  type ArrFolder,
  type ArrKind,
  type LibraryType,
  type Settings,
  type SetupStep,
} from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { ConnectTv } from '../components/ConnectTv'
import { FolderPicker } from '../components/FolderPicker'
import { useApi } from '../hooks/useApi'
import { appLogos } from '../lib/apps'
import { libraryTypeLabel } from '../lib/format'
import { freeColour } from '../lib/profileColours'
import { RemoteAccessGroups } from './settings/AccessSection'
import { errorText } from './settings/useSettings'

const steps: { id: SetupStep; title: string }[] = [
  { id: 'apps', title: 'Your apps' },
  { id: 'libraries', title: 'Libraries' },
  { id: 'profiles', title: 'Who’s watching' },
  { id: 'devices', title: 'Your TV and phone' },
  { id: 'away', title: 'Away from home' },
]

/**
 * The first-run guide: a few short steps from a new server to something to watch, each one skippable. Leaving part
 * way keeps a list of what's left on Home; everything here can be done in Settings too.
 */
export function SetupGuidePage() {
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const at = steps.findIndex((s) => s.id === params.get('step'))
  const index = at < 0 ? 0 : at
  const finished = params.get('step') === 'done'
  const [skipped, setSkipped] = useState<SetupStep[]>([])

  useEffect(() => {
    void saveSetupGuide({ opened: true }).catch(() => undefined)
  }, [])

  function go(step: SetupStep | 'done') {
    setParams({ step }, { replace: true })
    window.scrollTo(0, 0)
  }

  const next = () => go(steps[index + 1]?.id ?? 'done')

  function skip() {
    const now = [...skipped, steps[index].id]
    setSkipped(now)
    void saveSetupGuide({ skipped: now }).catch(() => undefined)
    next()
  }

  const step = steps[index]
  return (
    <main className="setup-guide">
      <header className="setup-guide-head">
        <ol className="setup-steps" aria-label="Steps">
          {steps.map((s, i) => (
            <li key={s.id} aria-current={!finished && i === index ? 'step' : undefined} className={finished || i < index ? 'done' : undefined}>
              <button type="button" onClick={() => go(s.id)}>
                {s.title}
              </button>
            </li>
          ))}
        </ol>
        <Link className="text-button" to="/">
          I’ll do it myself
        </Link>
      </header>
      {finished ? (
        <Done onFinish={() => navigate('/')} />
      ) : (
        <section className="setup-step" aria-labelledby="setup-step-title">
          <h1 id="setup-step-title">{step.title}</h1>
          {step.id === 'apps' && <AppsStep onNext={next} onNoApps={skip} />}
          {step.id === 'libraries' && <LibrariesStep onNext={next} />}
          {step.id === 'profiles' && <ProfilesStep />}
          {step.id === 'devices' && <DevicesStep />}
          {step.id === 'away' && <RemoteAccessGroups />}
          {step.id !== 'apps' && step.id !== 'libraries' && (
            <div className="setup-actions">
              <button type="button" className="button quiet" onClick={skip}>
                Skip
              </button>
              <button type="button" className="button" onClick={next}>
                Continue
              </button>
            </div>
          )}
        </section>
      )}
    </main>
  )
}

const arrKinds: ArrKind[] = ['Sonarr', 'Radarr', 'Lidarr']
const arrPurpose: Record<ArrKind, string> = { Sonarr: 'TV shows', Radarr: 'Films', Lidarr: 'Music' }
const usualUrl: Record<ArrKind, string> = { Sonarr: 'http://localhost:8989', Radarr: 'http://localhost:7878', Lidarr: 'http://localhost:8686' }

function AppsStep({ onNext, onNoApps }: { onNext: () => void; onNoApps: () => void }) {
  const settings = useApi(getSettings, [])
  const found = useApi(discoverArrs, [])

  if (!settings.data) return <p className="muted">{settings.error ? settings.error.message : 'Loading…'}</p>
  const connected = settings.data.arr.length > 0
  return (
    <>
      <p className="setup-lede">Connect Sonarr, Radarr or Lidarr and Flexarr takes artwork, details and new downloads from them.</p>
      <div className="settings-group">
        {arrKinds.map((kind) => (
          <ArrConnect
            key={kind}
            kind={kind}
            settings={settings.data!}
            found={found.data?.find((f) => f.kind === kind)?.baseUrl}
            onConnected={settings.reload}
          />
        ))}
      </div>
      <div className="setup-actions">
        {!connected && (
          <button type="button" className="button quiet" onClick={onNoApps}>
            I don’t use these
          </button>
        )}
        <button type="button" className="button" disabled={!connected} onClick={onNext}>
          Continue
        </button>
      </div>
    </>
  )
}

/** One app: connected already, or its address (filled in when it was found) and key to connect it with. */
function ArrConnect({ kind, settings, found, onConnected }: { kind: ArrKind; settings: Settings; found?: string; onConnected: () => void }) {
  const existing = settings.arr.filter((a) => a.kind === kind)
  const [baseUrl, setBaseUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState<string>()
  const [webhook, setWebhook] = useState<string>()
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)

  async function connect(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    setBusy(true)
    const connection = { name: kind, kind, baseUrl: baseUrl || found || '', apiKey }
    try {
      const status = await testArr(connection)
      if (!status.ok) return setError(status.error ?? `Couldn’t reach ${kind}.`)
      const current = settings.arr.map((a) => ({ name: a.name, kind: a.kind, baseUrl: a.baseUrl, apiKey: undefined }))
      await saveSettings({ arr: [...current, connection] })
      // New downloads show straight away once the app can tell Flexarr about them.
      const added = await addArrWebhook(kind).catch((e: unknown) => ({ ok: false, error: errorText(e) }))
      setWebhook(added.ok ? undefined : (added.error ?? undefined))
      setOpen(false)
      onConnected()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="connection">
      <div className="connection-line">
        <span className={existing.length > 0 ? 'status-dot ok' : 'status-dot'} aria-hidden="true" />
        <img className="app-logo" src={appLogos[kind]} alt="" />
        <span className="setting-text">
          <strong>{kind}</strong>
          <span className="muted">{existing.length > 0 ? 'Connected' : found ? `Found at ${found}` : arrPurpose[kind]}</span>
        </span>
        {existing.length === 0 && !open && (
          <span className="connection-actions">
            <button type="button" className="button small" onClick={() => setOpen(true)}>
              Connect
            </button>
          </span>
        )}
      </div>
      {webhook && <p className="muted setup-note">{webhook}</p>}
      {open && (
        <form className="connection-form" onSubmit={connect} aria-label={`Connect ${kind}`}>
          <label className="field">
            Address
            <input type="url" value={baseUrl || found || ''} required placeholder={usualUrl[kind]} onChange={(e) => setBaseUrl(e.target.value)} />
          </label>
          <label className="field">
            API key
            <input
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={apiKey}
              required
              placeholder={`In ${kind}, under Settings › General`}
              onChange={(e) => setApiKey(e.target.value)}
            />
          </label>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <div className="form-actions">
            <button type="button" className="button quiet" onClick={() => setOpen(false)}>
              Cancel
            </button>
            <AsyncButton type="submit" className="button" busy={busy}>
              Connect
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

const folderKind: Record<ArrKind, LibraryType> = { Sonarr: 'TvShows', Radarr: 'Movies', Lidarr: 'Music' }

interface Planned {
  type: LibraryType
  folder: string
  /** Where an app sees it, when that's elsewhere and the mapping isn't saved yet. */
  mapFrom?: string
  on: boolean
}

/** Where an app's root folder is here, if that's known or suggested. */
function localFolder(f: ArrFolder) {
  if (f.state === 'Found') return { folder: f.path }
  if (f.state === 'Mapped' && f.local) return { folder: f.local }
  if (f.state === 'Suggested' && f.suggested) return { folder: f.suggested.to, mapFrom: f.suggested.from }
  return undefined
}

/** The apps' own folders, ticked ready; and a folder of any kind, chosen by hand. */
function LibrariesStep({ onNext }: { onNext: () => void }) {
  const libraries = useApi(getLibraries, [])
  const folders = useApi(getArrFolders, [])
  const [planned, setPlanned] = useState<Planned[]>()
  const [adding, setAdding] = useState<LibraryType>()
  const [error, setError] = useState<string>()

  const taken = new Set(libraries.data?.flatMap((l) => l.folders))
  useEffect(() => {
    if (!folders.data || !libraries.data || planned) return
    setPlanned(
      folders.data.flatMap((f) => {
        const here = localFolder(f)
        return here && !taken.has(here.folder) ? [{ type: folderKind[f.kind], ...here, on: true }] : []
      }),
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [folders.data, libraries.data, planned])

  const missing = folders.data?.filter((f) => !localFolder(f)) ?? []
  const media = libraries.data?.filter((l) => l.type !== 'Podcasts') ?? []
  const chosen = planned?.filter((p) => p.on) ?? []

  async function add() {
    setError(undefined)
    try {
      const mappings = chosen.filter((p) => p.mapFrom).map((p) => ({ from: p.mapFrom!, to: p.folder }))
      if (mappings.length > 0) {
        const settings = await getSettings()
        await saveSettings({ pathMappings: [...settings.pathMappings, ...mappings] })
      }
      // One library for each kind, with every folder of that kind in it.
      for (const type of [...new Set(chosen.map((p) => p.type))]) {
        const existing = media.find((l) => l.type === type)
        if (existing) continue
        await createLibrary({ name: libraryTypeLabel[type], type, folders: chosen.filter((p) => p.type === type).map((p) => p.folder) })
      }
      onNext()
    } catch (e) {
      setError(errorText(e))
    }
  }

  if (!planned) return <p className="muted">{libraries.error?.message ?? 'Looking for your folders…'}</p>
  return (
    <>
      <p className="setup-lede">A library is a folder of one kind of thing: films, TV shows, music or home videos.</p>
      <div className="settings-group">
        {media.map((l) => (
          <div key={l.id} className="connection-line">
            <span className="status-dot ok" aria-hidden="true" />
            <span className="setting-text">
              <strong>{l.name}</strong>
              <span className="muted">{l.folders.join(', ')}</span>
            </span>
          </div>
        ))}
        {planned.map((p, i) => (
          <label key={p.folder} className="connection-line setup-folder">
            <input type="checkbox" checked={p.on} onChange={(e) => setPlanned(planned.map((x, j) => (j === i ? { ...x, on: e.target.checked } : x)))} />
            <span className="setting-text">
              <strong>{libraryTypeLabel[p.type]}</strong>
              <code className="muted">{p.folder}</code>
            </span>
          </label>
        ))}
        {missing.map((f) => (
          <p key={`${f.app}:${f.path}`} className="muted setup-note">
            Flexarr can’t see {f.app}’s <code>{f.path}</code> on this server. Add it below, or see Settings › Connections › Media folders.
          </p>
        ))}
      </div>
      {adding ? (
        <div className="panel">
          <p>Choose the folder with your {libraryTypeLabel[adding].toLowerCase()}.</p>
          <FolderPicker
            onPick={(folder) => {
              if (!planned.some((p) => p.folder === folder)) setPlanned([...planned, { type: adding, folder, on: true }])
              setAdding(undefined)
            }}
          />
          <button type="button" className="text-button" onClick={() => setAdding(undefined)}>
            Cancel
          </button>
        </div>
      ) : (
        <div className="setup-add">
          <span className="muted">Add a folder:</span>
          {(['Movies', 'TvShows', 'Music', 'HomeVideos'] as const).map((type) => (
            <button key={type} type="button" className="button quiet small" onClick={() => setAdding(type)}>
              {libraryTypeLabel[type]}
            </button>
          ))}
        </div>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="setup-actions">
        {chosen.length === 0 && media.length > 0 ? (
          <button type="button" className="button" onClick={onNext}>
            Continue
          </button>
        ) : (
          <AsyncButton className="button" disabled={chosen.length === 0} onClick={add}>
            {chosen.length > 1 ? 'Add these' : 'Add library'}
          </AsyncButton>
        )}
      </div>
    </>
  )
}

function ProfilesStep() {
  const profiles = useApi(getProfiles, [])
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  async function add(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    setBusy(true)
    try {
      await createProfile({ name: name.trim(), color: freeColour(profiles.data ?? []) })
      setName('')
      profiles.reload()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <p className="setup-lede">Everyone gets their own Continue watching and history. You can add PINs in Settings › Profiles.</p>
      <ul className="setup-profiles">
        {profiles.data?.map((p) => (
          <li key={p.id}>
            <span className="setup-avatar" style={{ background: p.color }} aria-hidden="true">
              {p.name.slice(0, 1).toUpperCase()}
            </span>
            {p.name}
          </li>
        ))}
      </ul>
      <form className="setup-inline" onSubmit={add} aria-label="Add a profile">
        <input type="text" value={name} required maxLength={40} placeholder="Name" aria-label="Name" onChange={(e) => setName(e.target.value)} />
        <AsyncButton type="submit" className="button quiet" busy={busy}>
          Add
        </AsyncButton>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}

function DevicesStep() {
  return (
    <>
      <p className="setup-lede">
        Get Flexarr on your <a href="https://apps.apple.com/app/flexarr/id6826618084">iPhone, iPad or Apple TV</a>. Open it at home and it shows a code: type it
        here.
      </p>
      <ConnectTv titled={false} />
    </>
  )
}

/** The end: what's still being read, and anything that looks wrong. */
function Done({ onFinish }: { onFinish: () => void }) {
  const libraries = useApi(getLibraries, [])
  const folders = useApi(getArrFolders, [])
  const media = libraries.data?.filter((l) => l.type !== 'Podcasts') ?? []
  const scanning = media.filter((l) => l.scan)

  // Still reading: look again until it's done.
  useEffect(() => {
    if (scanning.length === 0) return
    const timer = setInterval(libraries.reload, 3000)
    return () => clearInterval(timer)
  }, [scanning.length, libraries.reload])

  const problems = [
    ...media.filter((l) => !l.scan && l.lastScannedAt && l.itemCount === 0).map((l) => `Nothing found in ${l.name}. Is its drive connected?`),
    ...(folders.data ?? []).filter((f) => f.state === 'Missing').map((f) => `Flexarr can’t see ${f.app}’s ${f.path}. See Settings › Connections › Media folders.`),
  ]

  return (
    <section className="setup-step">
      <h1>{media.length === 0 ? 'Nearly there' : 'You’re set up'}</h1>
      {media.length === 0 ? (
        <p className="setup-lede">Add a library whenever you’re ready, in Settings › Libraries.</p>
      ) : scanning.length > 0 ? (
        <p className="setup-lede">
          Flexarr is reading your files ({scanning.reduce((n, l) => n + (l.scan?.processed ?? 0), 0)} so far). Titles appear on Home as they’re found.
        </p>
      ) : (
        <p className="setup-lede">{media.reduce((n, l) => n + l.itemCount, 0).toLocaleString()} titles ready to watch.</p>
      )}
      {problems.length > 0 && (
        <ul className="setup-problems" role="status">
          {problems.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      )}
      <div className="setup-actions">
        <button type="button" className="button" onClick={onFinish}>
          Go to Home
        </button>
      </div>
    </section>
  )
}
