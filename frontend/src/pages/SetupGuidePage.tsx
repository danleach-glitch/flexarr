import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import {
  addArrWebhook,
  createLibrary,
  createProfile,
  deleteProfile,
  discoverArrs,
  getArrFolders,
  getLibraries,
  getProfiles,
  getRemoteAccess,
  getSettings,
  getSetupGuide,
  saveSettings,
  saveSetupGuide,
  setTailscaleAccess,
  testArr,
  updateProfile,
  type ArrFolder,
  type ArrKind,
  type LibraryType,
  type Profile,
  type Settings,
  type SetupStep,
} from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { FolderPicker } from '../components/FolderPicker'
import { Antenna, AppleLogo, Check, Close, External, Film, Home, Monitor, Music, Pencil, Phone, Stack, Tv } from '../components/icons'
import { PairingPrompt } from '../components/PairingPrompt'
import { Avatar } from '../components/ProfilePicker'
import { useApi } from '../hooks/useApi'
import { appLogos } from '../lib/apps'
import { libraryTypeLabel } from '../lib/format'
import { freeColour, profileColours } from '../lib/profileColours'
import { errorText } from './settings/useSettings'
import '../styles/onboarding.css'

/** The steps; the first, the main profile's name, isn't one the server keeps track of. */
const steps: { id: SetupStep | 'name'; title: string; lede: string; icon: ReactNode; summary: string }[] = [
  { id: 'name', title: 'What’s your name?', lede: 'Your profile is the one that runs Flexarr.', icon: <Pencil />, summary: 'Tell Flexarr your name' },
  {
    id: 'apps',
    title: 'Connect your apps',
    lede: 'Flexarr gets artwork, details and new downloads from Sonarr, Radarr and Lidarr.',
    icon: <Stack />,
    summary: 'Connect Sonarr, Radarr or Lidarr',
  },
  { id: 'libraries', title: 'Add your libraries', lede: 'Choose the folders that hold your films, shows and music.', icon: <Film />, summary: 'Add your films, shows and music' },
  { id: 'profiles', title: 'Who’s watching?', lede: 'Everyone gets their own Continue watching and history.', icon: <Home />, summary: 'Add everyone who watches' },
  { id: 'devices', title: 'Watch on all your devices', lede: 'Get the app and open it at home. When it asks to connect, choose Allow here.', icon: <Tv />, summary: 'Watch on all your devices' },
  { id: 'away', title: 'Watch away from home', lede: 'Tailscale lets your devices reach Flexarr from anywhere, privately.', icon: <Antenna />, summary: 'Watch away from home' },
]

const tracked = steps.filter((s): s is (typeof steps)[number] & { id: SetupStep } => s.id !== 'name')

type Place = SetupStep | 'name' | 'welcome' | 'done'

/**
 * The first-run guide: a welcome, a card for each step, and a finish. Every step can be skipped, and leaving part way
 * keeps a list of what's left on Home; everything here is in Settings too.
 */
export function SetupGuidePage() {
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const asked = params.get('step') as Place | null
  const place: Place = asked === 'done' || steps.some((s) => s.id === asked) ? asked! : 'welcome'
  const index = steps.findIndex((s) => s.id === place)
  const [skipped, setSkipped] = useState<SetupStep[]>([])

  useEffect(() => {
    void saveSetupGuide({ opened: true }).catch(() => undefined)
    // Kept across visits, so coming back from Home's list adds to what was skipped before.
    void getSetupGuide()
      .then((g) => setSkipped(g.skipped))
      .catch(() => undefined)
  }, [])

  function go(next: Place) {
    setParams(next === 'welcome' ? {} : { step: next })
    window.scrollTo(0, 0)
  }

  const nav: StepNav = {
    back: () => go(index > 0 ? steps[index - 1].id : 'welcome'),
    next: () => go(steps[index + 1]?.id ?? 'done'),
    skip: () => {
      const id = steps[index].id
      if (id !== 'name') {
        const now = [...new Set([...skipped, id])]
        setSkipped(now)
        void saveSetupGuide({ skipped: now }).catch(() => undefined)
      }
      go(steps[index + 1]?.id ?? 'done')
    },
  }

  return (
    <div className="onboard">
      <header className="onboard-top">
        <span className="onboard-brand">
          <img src="/logo-mark.svg" alt="" />
          Flexarr
        </span>
        {place !== 'done' && (
          <Link className="onboard-later" to="/">
            Set up later
          </Link>
        )}
      </header>
      {place === 'welcome' ? (
        <Welcome onStart={() => go('name')} />
      ) : place === 'done' ? (
        <Done onFinish={() => navigate('/')} />
      ) : (
        <section className="onboard-card" key={place} aria-labelledby="onboard-title">
          <div className="onboard-progress" role="progressbar" aria-label="Setup" aria-valuemin={1} aria-valuemax={steps.length} aria-valuenow={index + 1}>
            <span style={{ width: `${((index + 1) / steps.length) * 100}%` }} />
          </div>
          <div className="onboard-card-head">
            <span className="onboard-icon">{steps[index].icon}</span>
            <p className="onboard-count">
              Step {index + 1} of {steps.length}
            </p>
            <h1 id="onboard-title">{steps[index].title}</h1>
            <p className="onboard-lede">{steps[index].lede}</p>
          </div>
          {place === 'name' && <NameStep nav={nav} />}
          {place === 'apps' && <AppsStep nav={nav} />}
          {place === 'libraries' && <LibrariesStep nav={nav} />}
          {place === 'profiles' && <ProfilesStep nav={nav} />}
          {place === 'devices' && <DevicesStep nav={nav} />}
          {place === 'away' && <AwayStep nav={nav} />}
        </section>
      )}
      {/* A TV or phone opened while the guide is up asks here to connect. */}
      <PairingPrompt />
    </div>
  )
}

interface StepNav {
  back: () => void
  next: () => void
  skip: () => void
}

function Welcome({ onStart }: { onStart: () => void }) {
  return (
    <section className="onboard-card onboard-welcome" aria-labelledby="onboard-title">
      <img className="onboard-hero-mark" src="/logo-mark.svg" alt="" />
      <h1 id="onboard-title">Welcome to Flexarr</h1>
      <p className="onboard-lede">
        Let’s get your films, shows and music ready to watch.
        <br />
        It takes just 2 minutes.
      </p>
      <ol className="onboard-plan">
        {steps.map((s) => (
          <li key={s.id}>
            <span className="onboard-plan-icon">{s.icon}</span>
            {s.summary}
          </li>
        ))}
      </ol>
      <div className="onboard-welcome-actions">
        <button type="button" className="button onboard-primary onboard-wide" onClick={onStart}>
          Get started
        </button>
        <Link className="onboard-later" to="/">
          I’ll set it up myself
        </Link>
      </div>
    </section>
  )
}

/** Back on the left; Skip and the step's main button on the right. */
function StepFooter({ nav, next, skipLabel = 'Skip' }: { nav: StepNav; next: ReactNode; skipLabel?: string | null }) {
  return (
    <footer className="onboard-foot">
      <button type="button" className="onboard-back" onClick={nav.back}>
        Back
      </button>
      <span className="onboard-foot-end">
        {skipLabel && (
          <button type="button" className="button quiet" onClick={nav.skip}>
            {skipLabel}
          </button>
        )}
        {next}
      </span>
    </footer>
  )
}

/** The main profile's name and colour, which start as "Main profile" in blue. */
function NameStep({ nav }: { nav: StepNav }) {
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const [name, setName] = useState<string>()
  const [color, setColor] = useState<string>()
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  if (!main) return <p className="onboard-wait">{profiles.error?.message ?? 'Loading…'}</p>
  // The name it was given when the server was made isn't anyone's: start with an empty box.
  const shown = name ?? (main.name === 'Main profile' ? '' : main.name)
  const chosen = color ?? main.color

  async function save(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    setBusy(true)
    try {
      await updateProfile(main!.id, { name: shown.trim(), color: chosen })
      nav.next()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={save} aria-label="Your name">
      <div className="onboard-me">
        <Avatar profile={{ name: shown.trim() || '?', color: chosen, image: main.image }} size={88} />
        <div className="onboard-me-fields">
          {/* oxlint-disable-next-line jsx-a11y/no-autofocus -- the one thing this step asks. */}
          <input type="text" value={shown} required maxLength={40} placeholder="Your name" aria-label="Your name" autoFocus onChange={(e) => setName(e.target.value)} />
          <div className="onboard-swatches" role="radiogroup" aria-label="Colour">
            {profileColours.map((c) => (
              <button
                key={c.value}
                type="button"
                role="radio"
                aria-checked={chosen.toLowerCase() === c.value}
                aria-label={c.name}
                className="onboard-swatch"
                style={{ background: c.value }}
                onClick={() => setColor(c.value)}
              />
            ))}
          </div>
        </div>
      </div>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      {/* Everyone needs a name, so this step has no Skip. */}
      <StepFooter
        nav={nav}
        skipLabel={null}
        next={
          <AsyncButton type="submit" className="button onboard-primary" busy={busy} disabled={!shown.trim()}>
            Continue
          </AsyncButton>
        }
      />
    </form>
  )
}

const arrKinds: ArrKind[] = ['Sonarr', 'Radarr', 'Lidarr']
const arrPurpose: Record<ArrKind, string> = { Sonarr: 'TV shows', Radarr: 'Films', Lidarr: 'Music' }
const usualUrl: Record<ArrKind, string> = { Sonarr: 'http://localhost:8989', Radarr: 'http://localhost:7878', Lidarr: 'http://localhost:8686' }

function AppsStep({ nav }: { nav: StepNav }) {
  const settings = useApi(getSettings, [])
  const found = useApi(discoverArrs, [])
  const [open, setOpen] = useState<ArrKind>()

  if (!settings.data) return <p className="onboard-wait">{settings.error ? settings.error.message : 'Loading…'}</p>
  const connected = settings.data.arr.length > 0
  return (
    <>
      <div className="onboard-tiles">
        {arrKinds.map((kind) => (
          <ArrTile
            key={kind}
            kind={kind}
            settings={settings.data!}
            found={found.data?.find((f) => f.kind === kind)?.baseUrl}
            open={open === kind}
            onOpen={(on) => setOpen(on ? kind : undefined)}
            onConnected={() => {
              setOpen(undefined)
              settings.reload()
            }}
          />
        ))}
      </div>
      <StepFooter
        nav={nav}
        skipLabel={connected ? null : 'Skip'}
        next={
          <button type="button" className="button onboard-primary" disabled={!connected} onClick={nav.next}>
            Continue
          </button>
        }
      />
    </>
  )
}

/** One app: connected already, or found nearby, or not; opened, its address and key. */
function ArrTile(props: { kind: ArrKind; settings: Settings; found?: string; open: boolean; onOpen: (open: boolean) => void; onConnected: () => void }) {
  const { kind, settings, found, open } = props
  const connected = settings.arr.some((a) => a.kind === kind)
  const [baseUrl, setBaseUrl] = useState<string>()
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState<string>()
  const [webhookError, setWebhookError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const address = baseUrl ?? found ?? ''

  async function connect(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    setBusy(true)
    const connection = { name: kind, kind, baseUrl: address.trim(), apiKey: apiKey.trim() }
    try {
      const status = await testArr(connection)
      if (!status.ok) return setError(status.error ?? `Couldn’t reach ${kind}.`)
      const current = settings.arr.map((a) => ({ name: a.name, kind: a.kind, baseUrl: a.baseUrl, apiKey: undefined }))
      await saveSettings({ arr: [...current, connection] })
      // New downloads show straight away once the app can tell Flexarr about them.
      const added = await addArrWebhook(kind).catch((err: unknown) => ({ ok: false, error: errorText(err) }))
      setWebhookError(added.ok ? undefined : (added.error ?? 'Couldn’t add the webhook.'))
      props.onConnected()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  // Connected by mistake, or to the wrong one: take it off again.
  async function disconnect() {
    setError(undefined)
    try {
      const rest = settings.arr.filter((a) => a.kind !== kind).map((a) => ({ name: a.name, kind: a.kind, baseUrl: a.baseUrl, apiKey: undefined }))
      await saveSettings({ arr: rest })
      setWebhookError(undefined)
      props.onConnected()
    } catch (err) {
      setError(errorText(err))
    }
  }

  const status = connected
    ? webhookError
      ? 'Connected. Add its webhook in Settings › Connections.'
      : 'Connected'
    : found
      ? 'Found on your network'
      : arrPurpose[kind]

  return (
    <div className={`onboard-tile${connected ? ' is-done' : ''}${open ? ' is-open' : ''}`}>
      <div className="onboard-tile-line">
        <img className="onboard-app-logo" src={appLogos[kind]} alt="" />
        <span className="onboard-tile-text">
          <strong>{kind}</strong>
          <span className={found && !connected ? 'onboard-found' : undefined}>{status}</span>
        </span>
        {connected ? (
          <span className="onboard-tile-end">
            <AsyncButton className="button quiet small" onClick={disconnect}>
              Disconnect
            </AsyncButton>
            <span className="onboard-tick" role="img" aria-label="Connected">
              <Check />
            </span>
          </span>
        ) : (
          !open && (
            <button type="button" className="button small" onClick={() => props.onOpen(true)}>
              Connect
            </button>
          )
        )}
      </div>
      {error && !open && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      {open && !connected && (
        <form className="onboard-form" onSubmit={connect} aria-label={`Connect ${kind}`}>
          <label>
            <span>Address</span>
            {/* oxlint-disable-next-line jsx-a11y/no-autofocus -- the form opens because this is what comes next. */}
            <input type="url" value={address} required autoFocus placeholder={usualUrl[kind]} onChange={(e) => setBaseUrl(e.target.value)} />
          </label>
          <label>
            <span>API key</span>
            <input type="text" autoComplete="off" spellCheck={false} value={apiKey} required onChange={(e) => setApiKey(e.target.value)} />
            <small>In {kind}: Settings › General</small>
          </label>
          {error && (
            <p className="onboard-error" role="alert">
              {error}
            </p>
          )}
          <div className="onboard-form-actions">
            <button type="button" className="button quiet small" onClick={() => props.onOpen(false)}>
              Cancel
            </button>
            <AsyncButton type="submit" className="button small" busy={busy}>
              Connect
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

const folderKind: Record<ArrKind, LibraryType> = { Sonarr: 'TvShows', Radarr: 'Movies', Lidarr: 'Music' }
const kindIcon: Record<LibraryType, ReactNode> = { Movies: <Film />, TvShows: <Tv />, Music: <Music />, HomeVideos: <Home />, Podcasts: <Antenna /> }
/** The guide makes one library of each of these; more, and home videos, are in Settings › Libraries. */
const kinds: LibraryType[] = ['Movies', 'TvShows', 'Music']

interface Choice {
  folder: string
  /** Where an app sees it, when that's elsewhere and the mapping isn't saved yet. */
  mapFrom?: string
  /** Found in an app, rather than chosen here. */
  fromApp?: string
}

/** Where an app's root folder is here, if that's known or suggested. */
function localFolder(f: ArrFolder): Choice | undefined {
  if (f.state === 'Found') return { folder: f.path, fromApp: f.app }
  if (f.state === 'Mapped' && f.local) return { folder: f.local, fromApp: f.app }
  if (f.state === 'Suggested' && f.suggested) return { folder: f.suggested.to, mapFrom: f.suggested.from, fromApp: f.app }
  return undefined
}

/** A line for films, TV shows and music: added already, a folder ready (the app's, or one chosen), or none yet. */
function LibrariesStep({ nav }: { nav: StepNav }) {
  const libraries = useApi(getLibraries, [])
  const folders = useApi(getArrFolders, [])
  // What's been chosen or cleared here, over what the apps suggest; null is "not this kind".
  const [chosen, setChosen] = useState<Partial<Record<LibraryType, Choice | null>>>({})
  const [picking, setPicking] = useState<LibraryType>()
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  // A list that didn't come says so, with a way to ask again, rather than waiting for ever.
  if (libraries.error)
    return (
      <p className="onboard-wait">
        {libraries.error.message}{' '}
        <button type="button" className="onboard-back" onClick={libraries.reload}>
          Try again
        </button>
      </p>
    )
  if (!libraries.data || (!folders.data && !folders.error)) return <p className="onboard-wait">Looking for your folders…</p>
  const existing = (type: LibraryType) => libraries.data!.find((l) => l.type === type)
  const suggested = (type: LibraryType) =>
    (folders.data ?? []).filter((f) => folderKind[f.kind] === type).map(localFolder).find((c): c is Choice => !!c)
  const choice = (type: LibraryType) => (type in chosen ? chosen[type] : suggested(type))
  const toAdd = kinds.filter((type) => !existing(type) && choice(type))
  const added = kinds.some((type) => existing(type))
  const missing = (folders.data ?? []).filter((f) => !localFolder(f))

  async function add() {
    setError(undefined)
    setBusy(true)
    try {
      const mappings = toAdd.flatMap((type) => (choice(type)?.mapFrom ? [{ from: choice(type)!.mapFrom!, to: choice(type)!.folder }] : []))
      if (mappings.length > 0) {
        const settings = await getSettings()
        await saveSettings({ pathMappings: [...settings.pathMappings, ...mappings] })
      }
      for (const type of toAdd) await createLibrary({ name: libraryTypeLabel[type], type, folders: [choice(type)!.folder] })
      nav.next()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="onboard-tiles">
        {kinds.map((type) => {
          const library = existing(type)
          const picked = choice(type)
          return (
            <div key={type} className={`onboard-tile${library ? ' is-done' : picked ? ' is-on' : ''}${picking === type ? ' is-open' : ''}`}>
              <div className="onboard-tile-line">
                <span className="onboard-kind">{kindIcon[type]}</span>
                <span className="onboard-tile-text">
                  <strong>{libraryTypeLabel[type]}</strong>
                  {library ? (
                    <code>{library.folders.join(', ')}</code>
                  ) : picked ? (
                    <code>{picked.folder}</code>
                  ) : (
                    <span>No folder chosen</span>
                  )}
                </span>
                {library ? (
                  <span className="onboard-tick" role="img" aria-label="Added">
                    <Check />
                  </span>
                ) : picking === type ? null : picked ? (
                  <span className="onboard-tile-end">
                    <button type="button" className="onboard-back" onClick={() => setChosen({ ...chosen, [type]: null })} aria-label={`Don’t add ${libraryTypeLabel[type]}`}>
                      Remove
                    </button>
                    <button type="button" className="button quiet small" onClick={() => setPicking(type)}>
                      Change
                    </button>
                  </span>
                ) : (
                  <button type="button" className="button small" onClick={() => setPicking(type)}>
                    Choose folder
                  </button>
                )}
              </div>
              {picking === type && (
                <div className="onboard-tile-picker">
                  <FolderPicker
                    pickLabel="Add library"
                    onPick={(folder) => {
                      setChosen({ ...chosen, [type]: { folder } })
                      setPicking(undefined)
                    }}
                  />
                  <button type="button" className="onboard-back" onClick={() => setPicking(undefined)}>
                    Cancel
                  </button>
                </div>
              )}
            </div>
          )
        })}
        {missing.map((f) => (
          <p key={`${f.app}:${f.path}`} className="onboard-warn">
            Flexarr can’t see {f.app}’s <code>{f.path}</code> on this server. Choose where it is above.
          </p>
        ))}
      </div>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      <StepFooter
        nav={nav}
        skipLabel={added || toAdd.length > 0 ? null : 'Skip'}
        next={
          toAdd.length === 0 ? (
            <button type="button" className="button onboard-primary" disabled={!added} onClick={nav.next}>
              Continue
            </button>
          ) : (
            <AsyncButton className="button onboard-primary" busy={busy} onClick={add}>
              Next
            </AsyncButton>
          )
        }
      />
    </>
  )
}

function ProfilesStep({ nav }: { nav: StepNav }) {
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

  async function remove(p: Profile) {
    setError(undefined)
    try {
      await deleteProfile(p.id)
      profiles.reload()
    } catch (err) {
      setError(errorText(err))
    }
  }

  const more = (profiles.data?.length ?? 0) > 1
  return (
    <>
      <ul className="onboard-people">
        {profiles.data?.map((p) => (
          <li key={p.id}>
            <span className="onboard-person">
              <Avatar profile={p} size={64} />
              {!p.isMain && (
                <AsyncButton className="onboard-remove" aria-label={`Remove ${p.name}`} title="Remove" onClick={() => remove(p)}>
                  <Close />
                </AsyncButton>
              )}
            </span>
            <span>{p.isMain ? `${p.name} (you)` : p.name}</span>
          </li>
        ))}
      </ul>
      <form className="onboard-person-new" onSubmit={add} aria-label="Add a profile">
        <input type="text" value={name} required maxLength={40} placeholder="Add someone: their name" aria-label="Name" onChange={(e) => setName(e.target.value)} />
        <AsyncButton type="submit" className="button" busy={busy} disabled={!name.trim()}>
          Add
        </AsyncButton>
      </form>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      <StepFooter
        nav={nav}
        skipLabel={more ? null : 'Skip'}
        next={
          <button type="button" className="button onboard-primary" disabled={!more} onClick={nav.next}>
            Continue
          </button>
        }
      />
    </>
  )
}

const deviceApps: { name: string; icon: ReactNode; href?: string }[] = [
  { name: 'iPhone and iPad', icon: <Phone />, href: 'https://apps.apple.com/app/flexarr/id6826618084' },
  { name: 'Apple TV', icon: <Tv /> },
  { name: 'Android phone and tablet', icon: <Phone /> },
  { name: 'Android TV, Google TV and Fire TV', icon: <Tv /> },
]

/** Where to get the apps. A device asking to connect pops up here (the page has PairingPrompt) to allow. */
function DevicesStep({ nav }: { nav: StepNav }) {
  return (
    <>
      <div className="onboard-tiles">
        {deviceApps.map((app) => (
          <div key={app.name} className="onboard-tile">
            <div className="onboard-tile-line">
              <span className="onboard-kind">{app.icon}</span>
              <span className="onboard-tile-text">
                <strong>{app.name}</strong>
              </span>
              {app.href ? (
                <a className="button small onboard-store" href={app.href} target="_blank" rel="noreferrer">
                  <AppleLogo className="onboard-store-logo" />
                  App Store
                  <External />
                  <span className="visually-hidden"> (opens in a new tab)</span>
                </a>
              ) : (
                <span className="onboard-soon">Coming soon</span>
              )}
            </div>
          </div>
        ))}
        <div className="onboard-tile">
          <div className="onboard-tile-line">
            <span className="onboard-kind">
              <Monitor />
            </span>
            <span className="onboard-tile-text">
              <strong>Any web browser</strong>
              <code>{window.location.origin}</code>
            </span>
          </div>
        </div>
      </div>
      <StepFooter
        nav={nav}
        next={
          <button type="button" className="button onboard-primary" onClick={nav.next}>
            Continue
          </button>
        }
      />
    </>
  )
}

function AwayStep({ nav }: { nav: StepNav }) {
  const remote = useApi(getRemoteAccess, [])
  const [error, setError] = useState<string>()
  const ts = remote.data?.tailscale
  const on = !!ts?.privateAccess || !!remote.data?.tailnetAddress
  const docker = !!remote.data?.inContainer && !ts?.installed

  async function turn(on: boolean) {
    setError(undefined)
    try {
      await setTailscaleAccess(on)
      remote.reload()
    } catch (e) {
      setError(errorText(e))
    }
  }

  const status = !remote.data
    ? 'Checking…'
    : on
      ? 'On. Your devices switch to it by themselves.'
      : remote.data.inMacApp
        ? 'Install Tailscale on this Mac and your devices, signed in to the same account.'
        : docker
          ? 'Tailscale can’t be turned on from Flexarr in Docker. The guide has other ways.'
          : !ts?.installed
            ? 'Not installed on this server yet. It’s free for personal use.'
            : !ts.running
              ? 'Installed. Open Tailscale on this server and sign in.'
              : 'Ready to turn on.'

  return (
    <>
      <div className="onboard-tiles">
        <div className={`onboard-tile${on ? ' is-done' : ''}`}>
          <div className="onboard-tile-line">
            <span className="onboard-kind">
              <Antenna />
            </span>
            <span className="onboard-tile-text">
              <strong>Tailscale</strong>
              <span>{status}</span>
            </span>
            {on ? (
              <span className="onboard-tile-end">
                {ts?.privateAccess && !remote.data?.inMacApp && (
                  <AsyncButton className="button quiet small" onClick={() => turn(false)}>
                    Disconnect
                  </AsyncButton>
                )}
                <span className="onboard-tick" role="img" aria-label="On">
                  <Check />
                </span>
              </span>
            ) : ts?.running && !remote.data?.inMacApp ? (
              <AsyncButton className="button small" onClick={() => turn(true)}>
                Turn on
              </AsyncButton>
            ) : remote.data && docker ? (
              <a className="button quiet small" href="https://flexarr.io/guide/away-from-home" target="_blank" rel="noreferrer">
                Guide
              </a>
            ) : (
              remote.data && (
                <span className="onboard-tile-end">
                  {/* Installed, or signed in, while the guide waits: look again. */}
                  <AsyncButton className="button quiet small" onClick={remote.reload}>
                    Check again
                  </AsyncButton>
                  {(!ts?.installed || remote.data.inMacApp) && (
                    <a className="button small" href="https://tailscale.com/download" target="_blank" rel="noreferrer">
                      Get Tailscale
                    </a>
                  )}
                </span>
              )
            )}
          </div>
        </div>
      </div>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      <StepFooter
        nav={nav}
        skipLabel={null}
        next={
          // Finishing without it counts as passing it over, so Home's list doesn't keep asking.
          <button type="button" className="button onboard-primary" onClick={on ? nav.next : nav.skip}>
            Finish
          </button>
        }
      />
    </>
  )
}

/** The end: what's set up, what's still being read, and anything that looks wrong. */
function Done({ onFinish }: { onFinish: () => void }) {
  const libraries = useApi(getLibraries, [])
  const folders = useApi(getArrFolders, [])
  const guide = useApi(getSetupGuide, [])
  const media = libraries.data?.filter((l) => l.type !== 'Podcasts') ?? []
  // A library just added hasn't started its first scan yet: that counts as being checked too.
  const scanning = media.filter((l) => l.scan || !l.lastScannedAt)
  const { reload } = libraries

  // Still reading: look again until it's done.
  useEffect(() => {
    if (scanning.length === 0) return
    const timer = setInterval(reload, 3000)
    return () => clearInterval(timer)
  }, [scanning.length, reload])

  const total = scanning.reduce((n, l) => n + (l.scan?.total ?? 0), 0)
  const read = scanning.reduce((n, l) => n + (l.scan?.processed ?? 0), 0)
  const titles = media.reduce((n, l) => n + l.itemCount, 0)
  const problems = [
    ...media.filter((l) => !l.scan && l.lastScannedAt && l.itemCount === 0).map((l) => `Nothing found in ${l.name}. Is its drive connected?`),
    ...(folders.data ?? []).filter((f) => f.state === 'Missing').map((f) => `Flexarr can’t see ${f.app}’s ${f.path}. Check Settings › Connections.`),
  ]

  return (
    <section className="onboard-card onboard-done" aria-labelledby="onboard-title">
      {scanning.length > 0 ? (
        <span className="onboard-checking" aria-hidden="true" />
      ) : media.length > 0 ? (
        <span className="onboard-done-mark" aria-hidden="true">
          <Check />
        </span>
      ) : (
        <img className="onboard-hero-mark" src="/logo-mark.svg" alt="" />
      )}
      <h1 id="onboard-title">{media.length === 0 ? 'Nearly there' : scanning.length > 0 ? 'Checking your libraries' : 'You’re all set'}</h1>
      <p className="onboard-lede">
        {media.length === 0
          ? 'Add a library whenever you’re ready, in Settings › Libraries.'
          : scanning.length > 0
            ? `Flexarr is reading ${total > 0 ? `${read.toLocaleString()} of ${total.toLocaleString()} files` : 'your files'}.`
            : `${titles.toLocaleString()} ${titles === 1 ? 'title' : 'titles'} ready to watch.`}
      </p>
      {scanning.length > 0 && (
        <div className="onboard-scan" role="progressbar" aria-label="Reading your files" aria-valuemin={0} aria-valuemax={total || undefined} aria-valuenow={read}>
          <span style={{ width: total > 0 ? `${(read / total) * 100}%` : '8%' }} />
        </div>
      )}
      {guide.data && (
        <ul className="onboard-summary">
          {tracked.map((s) => (
            <li key={s.id} className={guide.data![s.id] ? 'is-done' : undefined}>
              <span className="onboard-summary-icon">{guide.data![s.id] ? <Check /> : s.icon}</span>
              {s.summary}
            </li>
          ))}
        </ul>
      )}
      {problems.map((p) => (
        <p key={p} className="onboard-warn" role="status">
          {p}
        </p>
      ))}
      <button type="button" className="button onboard-primary onboard-wide" onClick={onFinish}>
        {media.length > 0 ? 'Start watching now' : 'Go to Home'}
      </button>
    </section>
  )
}
