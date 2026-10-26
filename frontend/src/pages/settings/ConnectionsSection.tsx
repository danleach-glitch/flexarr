import { useState, type FormEvent, type ReactNode } from 'react'
import {
  getArrStatus,
  getLiveTvStatus,
  getRequestStatus,
  refreshLiveTv,
  rotateWebhookSecret,
  testArr,
  testLiveTv,
  type ArrKind,
  type ArrStatus,
  type LiveTvSettingsUpdate,
  type LiveTvTest,
  type Settings,
} from '../../api/client'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { CopyButton } from '../../components/CopyButton'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { appLogos } from '../../lib/apps'
import { OpenApp, SaveStatus, SettingsGroup, SettingsHeader } from './shared'
import { errorText, useSettings } from './useSettings'

interface DraftConnection {
  name: string
  kind: ArrKind
  baseUrl: string
  apiKey: string
  hasApiKey: boolean
}

type Save = ReturnType<typeof useSettings>['save']

/**
 * Sonarr, Radarr and Lidarr, then Seerr and Dispatcharr (Live TV): a line each saying how it is, opened to change it. Below them, path mappings
 * and the webhook addresses the apps call when something new arrives.
 */
export function ConnectionsSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  // Asked once the settings are in (and again after a save), not also while they load.
  const status = useApi((signal) => (settings ? getArrStatus(signal) : Promise.resolve(undefined)), [settings])
  const seerrStatus = useApi((signal) => (settings ? getRequestStatus(signal) : Promise.resolve(undefined)), [settings])
  const liveStatus = useApi((signal) => (settings ? getLiveTvStatus(signal) : Promise.resolve(undefined)), [settings])
  // While the channels and guide come in (after a save, or Refresh), keep asking how it's going.
  usePolling(liveStatus.reload, liveStatus.data?.refreshing ? 2000 : null)
  // Which one is open: an index into the list, 'new' for one being added, 'seerr' or 'livetv'.
  const [open, setOpen] = useState<number | 'new' | 'seerr' | 'livetv'>()
  if (!settings) return waiting

  const current = settings.arr.map((a) => ({ name: a.name, kind: a.kind, baseUrl: a.baseUrl, apiKey: undefined as string | undefined }))
  /** The whole list goes to the server, with this one changed (or taken out); keys left blank stay as they were. */
  async function saveArr(index: number | 'new', next: DraftConnection | null) {
    const list = [...current]
    const entry = next && { name: next.name, kind: next.kind, baseUrl: next.baseUrl, apiKey: next.apiKey || undefined }
    if (index === 'new') list.push(entry!)
    else if (entry) list[index] = entry
    else list.splice(index, 1)
    if (await save({ arr: list })) setOpen(undefined)
  }

  const nextKind: ArrKind = (['Sonarr', 'Radarr', 'Lidarr'] as const).find((k) => !settings.arr.some((a) => a.kind === k)) ?? 'Sonarr'

  return (
    <section>
      <SettingsHeader title="Connections">
        Flexarr gets artwork, details and trailers from Sonarr, Radarr and Lidarr, so no other accounts are needed. Seerr adds Discover and requests,
        and Dispatcharr Live TV.
      </SettingsHeader>

      <SettingsGroup
        title="Apps"
        note={saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : undefined}
        action={
          open !== 'new' && (
            <button className="button quiet small" onClick={() => setOpen('new')}>
              Add a connection
            </button>
          )
        }
      >
        {settings.arr.map((a, i) => (
          <ArrRow
            key={`${a.name}-${i}`}
            connection={{ ...a, apiKey: '' }}
            live={status.data?.find((s) => s.name === a.name)}
            open={open === i}
            onOpen={() => setOpen(i)}
            onClose={() => setOpen(undefined)}
            onSave={(c) => saveArr(i, c)}
            onRemove={() => saveArr(i, null)}
          />
        ))}
        {open === 'new' && (
          <ArrRow
            connection={{ name: nextKind, kind: nextKind, baseUrl: '', apiKey: '', hasApiKey: false }}
            isNew
            open
            onOpen={() => undefined}
            onClose={() => setOpen(undefined)}
            onSave={(c) => saveArr('new', c)}
          />
        )}
        <SeerrRow
          seerr={settings.seerr}
          status={seerrStatus.loading ? 'Checking…' : !seerrStatus.data?.configured ? 'Not set up' : seerrStatus.data.reachable ? 'Connected' : 'Can’t be reached, or the API key isn’t right'}
          ok={seerrStatus.data?.configured ? seerrStatus.data.reachable : undefined}
          open={open === 'seerr'}
          onOpen={() => setOpen('seerr')}
          onClose={() => setOpen(undefined)}
          onSave={async (seerr) => {
            if (await save({ seerr })) setOpen(undefined)
          }}
        />
        <DispatcharrRow
          liveTv={settings.liveTv ?? null}
          status={
            liveStatus.loading
              ? 'Checking…'
              : !liveStatus.data?.configured
                ? 'Not set up'
                : liveStatus.data.refreshing
                  ? 'Bringing in the channels and guide…'
                  : (liveStatus.data.error ?? `Live TV: ${liveStatus.data.channels.toLocaleString()} channels`)
          }
          ok={liveStatus.data?.configured ? !liveStatus.data.error : undefined}
          refreshing={liveStatus.data?.refreshing ?? false}
          open={open === 'livetv'}
          onOpen={() => setOpen('livetv')}
          onClose={() => setOpen(undefined)}
          onSave={async (liveTv, message) => {
            if (await save({ liveTv }, message)) setOpen(undefined)
          }}
          onRefresh={async () => {
            await refreshLiveTv()
            setTimeout(liveStatus.reload, 500)
          }}
        />
      </SettingsGroup>

      <PathMappings mappings={settings.pathMappings} save={save} />
      <Webhooks secret={settings.webhookSecret} />
    </section>
  )
}

/** A connection's line: whether it's reachable, its version, a link to it, and Edit, which opens the form under it. */
function ConnectionLine({ kind, name, ok, status, baseUrl, open, onOpen }: { kind: keyof typeof appLogos; name: string; ok?: boolean; status: ReactNode; baseUrl: string; open: boolean; onOpen: () => void }) {
  return (
    <div className="connection-line">
      <span className={ok ? 'status-dot ok' : ok === false ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
      <img className="app-logo" src={appLogos[kind]} alt="" />
      <span className="setting-text">
        <strong>{name}</strong>
        <span className="muted">{status}</span>
      </span>
      <span className="connection-actions">
        {baseUrl && <OpenApp baseUrl={baseUrl} name={name} />}
        {!open && (
          <button className="button quiet small" onClick={onOpen} aria-label={`Edit ${name}`}>
            Edit
          </button>
        )}
      </span>
    </div>
  )
}

function ArrRow({
  connection,
  live,
  isNew,
  open,
  onOpen,
  onClose,
  onSave,
  onRemove,
}: {
  connection: DraftConnection
  live?: ArrStatus
  isNew?: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (c: DraftConnection) => Promise<void>
  onRemove?: () => Promise<void>
}) {
  const [draft, setDraft] = useState(connection)
  const [test, setTest] = useState<ArrStatus | 'testing'>()
  const shown = test && test !== 'testing' ? test : live
  const update = (change: Partial<DraftConnection>) => setDraft((d) => ({ ...d, ...change }))

  async function runTest() {
    setTest('testing')
    const result = await testArr({ name: draft.name, kind: draft.kind, baseUrl: draft.baseUrl, apiKey: draft.apiKey || undefined }).catch(
      (e: unknown): ArrStatus => ({ name: draft.name, kind: draft.kind, ok: false, version: null, error: errorText(e) }),
    )
    setTest(result)
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    void onSave(draft)
  }

  const statusText = test === 'testing' ? 'Testing…' : shown?.ok ? `Connected · version ${shown.version}` : shown ? shown.error : isNew ? 'New connection' : 'Checking…'
  return (
    <div className="connection">
      <ConnectionLine kind={draft.kind} name={draft.name || draft.kind} ok={shown?.ok} status={statusText} baseUrl={isNew ? '' : connection.baseUrl} open={open} onOpen={onOpen} />
      {open && (
        <form className="connection-form" onSubmit={submit} aria-label={isNew ? 'New connection' : `Edit ${connection.name}`}>
          <div className="field-row">
            {isNew && (
              <label>
                Kind
                <select value={draft.kind} onChange={(e) => update({ kind: e.target.value as ArrKind, name: draft.name === draft.kind ? e.target.value : draft.name })}>
                  <option>Sonarr</option>
                  <option>Radarr</option>
                  <option>Lidarr</option>
                </select>
              </label>
            )}
            <label>
              Name
              <input type="text" value={draft.name} required onChange={(e) => update({ name: e.target.value })} />
            </label>
            <label className="grow">
              Address
              <input type="url" value={draft.baseUrl} required placeholder={placeholderUrl[draft.kind]} onChange={(e) => update({ baseUrl: e.target.value })} />
            </label>
          </div>
          <label className="field">
            <span className="field-label">API key</span>
            <input
              type="password"
              value={draft.apiKey}
              placeholder={connection.hasApiKey ? 'Saved; type to replace' : `In ${draft.kind}, under Settings › General`}
              onChange={(e) => update({ apiKey: e.target.value })}
            />
          </label>
          <div className="form-actions">
            {onRemove && (
              <button type="button" className="button danger small form-actions-start" onClick={() => confirm(`Remove ${connection.name}?`) && void onRemove()}>
                Remove
              </button>
            )}
            <button type="button" className="button quiet small" onClick={() => void runTest()}>
              Test
            </button>
            <button
              type="button"
              className="button quiet small"
              onClick={() => {
                setDraft(connection)
                setTest(undefined)
                onClose()
              }}
            >
              Cancel
            </button>
            <button type="submit" className="button small">
              {isNew ? 'Add' : 'Save'}
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

const placeholderUrl: Record<ArrKind, string> = { Sonarr: 'http://localhost:8989', Radarr: 'http://localhost:7878', Lidarr: 'http://localhost:8686' }

function SeerrRow({
  seerr,
  status,
  ok,
  open,
  onOpen,
  onClose,
  onSave,
}: {
  seerr: Settings['seerr']
  status: string
  ok?: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (seerr: { baseUrl: string; apiKey?: string }) => Promise<void>
}) {
  const [baseUrl, setBaseUrl] = useState(seerr?.baseUrl ?? '')
  const [apiKey, setApiKey] = useState('')
  return (
    <div className="connection">
      <ConnectionLine kind="Seerr" name="Seerr" ok={ok} status={status} baseUrl={seerr?.baseUrl ?? ''} open={open} onOpen={onOpen} />
      {open && (
        <form
          className="connection-form"
          aria-label="Edit Seerr"
          onSubmit={(e) => {
            e.preventDefault()
            void onSave({ baseUrl, apiKey: apiKey || undefined })
          }}
        >
          <p className="muted">Seerr, Overseerr or Jellyseerr: what’s trending that you don’t have yet, requested in one click under your Seerr rules.</p>
          <div className="field-row">
            <label className="grow">
              Address
              <input type="url" value={baseUrl} placeholder="http://localhost:5055" onChange={(e) => setBaseUrl(e.target.value)} />
            </label>
            <label className="grow">
              API key
              <input type="password" value={apiKey} placeholder={seerr?.hasApiKey ? 'Saved; type to replace' : 'In Seerr, under Settings › General'} onChange={(e) => setApiKey(e.target.value)} />
            </label>
          </div>
          <div className="form-actions">
            {seerr && (
              <button type="button" className="button danger small form-actions-start" onClick={() => confirm('Remove Seerr? Discover and requests go with it.') && void onSave({ baseUrl: '' })}>
                Remove
              </button>
            )}
            <button type="button" className="button quiet small" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="button small">
              Save
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

/**
 * Dispatcharr, for Live TV: Flexarr signs in to its Xtream Codes API as a Dispatcharr user of its own, and the
 * channels and guide are the ones Dispatcharr gives that user. It's often reachable only on this server, so a
 * localhost address gets no Open link (a browser elsewhere couldn't follow it).
 */
/** A select's choices with the saved value among them (set elsewhere, it would otherwise be changed by saving). */
function withSaved(choices: number[], saved: number | undefined) {
  return saved === undefined || choices.includes(saved) ? choices : [...choices, saved].sort((a, b) => a - b)
}

function DispatcharrRow({
  liveTv,
  status,
  ok,
  refreshing,
  open,
  onOpen,
  onClose,
  onSave,
  onRefresh,
}: {
  liveTv: Settings['liveTv'] | null
  status: string
  ok?: boolean
  refreshing: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (liveTv: LiveTvSettingsUpdate, message?: string) => Promise<void>
  onRefresh: () => Promise<void>
}) {
  const [baseUrl, setBaseUrl] = useState(liveTv?.baseUrl ?? '')
  const [username, setUsername] = useState(liveTv?.username ?? '')
  const [password, setPassword] = useState('')
  const [rewind, setRewind] = useState(liveTv?.rewindMinutes ?? 60)
  const [dvrFolder, setDvrFolder] = useState(liveTv?.dvrFolder ?? '')
  const [before, setBefore] = useState(liveTv?.paddingBeforeMinutes ?? 1)
  const [after, setAfter] = useState(liveTv?.paddingAfterMinutes ?? 5)
  const [minFree, setMinFree] = useState(liveTv?.minFreeGigabytes ?? 100)
  const [streams, setStreams] = useState(liveTv?.maxStreams ?? 0)
  const [test, setTest] = useState<LiveTvTest | 'testing'>()
  const local = /^(localhost|127\.|\[::1\])/.test(hostOf(liveTv?.baseUrl))
  const shown = test === 'testing' ? 'Testing…' : test ? (test.ok ? testSummary(test) : test.error) : status

  return (
    <div className="connection">
      <ConnectionLine kind="Dispatcharr" name="Dispatcharr" ok={test && test !== 'testing' ? test.ok : ok} status={shown} baseUrl={local ? '' : (liveTv?.baseUrl ?? '')} open={open} onOpen={onOpen} />
      {open && (
        <form
          className="connection-form"
          aria-label="Edit Dispatcharr"
          onSubmit={(e) => {
            e.preventDefault()
            setTest(undefined)
            void onSave({
              baseUrl: baseUrl.trim(),
              username: username.trim(),
              password: password || undefined,
              rewindMinutes: rewind,
              dvrFolder: dvrFolder.trim(),
              paddingBeforeMinutes: before,
              paddingAfterMinutes: after,
              minFreeGigabytes: minFree,
              maxStreams: streams,
            })
          }}
        >
          <p className="muted">
            TV channels and their guide. In Dispatcharr, add a user for Flexarr at Streamer level and give it a long random XC password: that works as
            Flexarr’s key, which can only list and play channels (never sign in to Dispatcharr or change it). The channels are the ones Dispatcharr gives
            that user.
          </p>
          <div className="field-row">
            <label className="grow">
              Address
              <input type="url" value={baseUrl} required placeholder="http://localhost:9191" onChange={(e) => setBaseUrl(e.target.value)} />
            </label>
            <label>
              User name
              <input type="text" autoComplete="off" value={username} required placeholder="flexarr" onChange={(e) => setUsername(e.target.value)} />
            </label>
            <label>
              Key (XC password)
              <input
                type="password"
                autoComplete="new-password"
                value={password}
                placeholder={liveTv?.hasPassword ? 'Saved; type to replace' : 'The user’s XC password in Dispatcharr'}
                onChange={(e) => setPassword(e.target.value)}
              />
            </label>
          </div>
          <label className="field">
            <span className="field-label">Rewind live TV up to</span>
            <select value={rewind} onChange={(e) => setRewind(Number(e.target.value))}>
              {[15, 30, 60, 120, 240].map((m) => (
                <option key={m} value={m}>
                  {m < 60 ? `${m} minutes` : m === 60 ? '1 hour' : `${m / 60} hours`}
                </option>
              ))}
            </select>
            <span className="muted">Kept on this server while someone watches a channel, about 2 GB an hour for HD.</span>
          </label>
          <fieldset className="connection-fieldset">
            <legend>Recordings</legend>
            <label className="field">
              <span className="field-label">Folder</span>
              <input type="text" value={dvrFolder} placeholder={liveTv?.dvrFolderInUse ?? 'A DVR folder on the drive your TV library is on'} onChange={(e) => setDvrFolder(e.target.value)} />
              <span className="muted">
                {dvrFolder.trim() ? 'Recordings made from now on go here; earlier ones stay where they are.' : `Empty for the default${liveTv?.dvrFolderInUse ? `: ${liveTv.dvrFolderInUse}` : ''}.`}
              </span>
            </label>
            <div className="field-row">
              <label>
                Start early
                <select value={before} onChange={(e) => setBefore(Number(e.target.value))}>
                  {withSaved([0, 1, 2, 5, 10], liveTv?.paddingBeforeMinutes).map((m) => (
                    <option key={m} value={m}>
                      {m === 0 ? 'On time' : `${m} min`}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Run on
                <select value={after} onChange={(e) => setAfter(Number(e.target.value))}>
                  {withSaved([0, 2, 5, 10, 15, 30], liveTv?.paddingAfterMinutes).map((m) => (
                    <option key={m} value={m}>
                      {m === 0 ? 'No' : `${m} min`}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Keep free
                <select value={minFree} onChange={(e) => setMinFree(Number(e.target.value))}>
                  {withSaved([10, 25, 50, 100, 250, 500], liveTv?.minFreeGigabytes).map((g) => (
                    <option key={g} value={g}>
                      {g} GB
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Channels at once
                <select value={streams} onChange={(e) => setStreams(Number(e.target.value))}>
                  <option value={0}>As Dispatcharr says</option>
                  {withSaved([1, 2, 3, 4, 5], liveTv?.maxStreams ?? undefined).map((n) => (
                    <option key={n} value={n}>
                      {n}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <span className="muted">
              Broadcasts drift, so recordings start a little early and run on. No new recording starts when the drive has less than that free. Recordings and
              live viewing share the channels your IPTV login allows (watching and recording the same channel counts once).
            </span>
          </fieldset>
          <div className="form-actions">
            {liveTv && (
              <button
                type="button"
                className="button danger small form-actions-start"
                onClick={() => confirm('Remove Dispatcharr? Live TV, its channels and everyone’s favourites go with it.') && void onSave({ baseUrl: '' }, 'Live TV removed')}
              >
                Remove
              </button>
            )}
            {liveTv && (
              <button type="button" className="button quiet small" disabled={refreshing} onClick={() => void onRefresh()}>
                Refresh channels
              </button>
            )}
            <button
              type="button"
              className="button quiet small"
              disabled={!baseUrl || !username}
              onClick={async () => {
                setTest('testing')
                setTest(
                  await testLiveTv({ baseUrl: baseUrl.trim(), username: username.trim(), password: password || undefined }).catch(
                    (e: unknown): LiveTvTest => ({ ok: false, channels: null, maxConnections: null, expiresAt: null, error: errorText(e) }),
                  ),
                )
              }}
            >
              Test
            </button>
            <button type="button" className="button quiet small" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="button small">
              Save
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

function hostOf(url: string | undefined) {
  try {
    return url ? new URL(url).host : ''
  } catch {
    return ''
  }
}

function testSummary(t: LiveTvTest) {
  const parts = [`Connected: ${(t.channels ?? 0).toLocaleString()} channels`]
  if (t.maxConnections) parts.push(`${t.maxConnections} ${t.maxConnections === 1 ? 'connection' : 'connections'} at a time`)
  if (t.expiresAt) parts.push(`subscription ends ${new Date(t.expiresAt).toLocaleDateString()}`)
  return parts.join(' · ')
}

/** Where Docker'd apps see the media, against where this server does. Saved together, once changed. */
function PathMappings({ mappings: saved, save }: { mappings: Settings['pathMappings']; save: Save }) {
  const [mappings, setMappings] = useState(saved)
  const changed = JSON.stringify(mappings) !== JSON.stringify(saved)
  const set = (i: number, change: Partial<Settings['pathMappings'][number]>) => setMappings(mappings.map((x, j) => (j === i ? { ...x, ...change } : x)))

  return (
    <SettingsGroup title="Path mappings" note="Only needed when Sonarr, Radarr or Lidarr see your media under other paths, as they do in Docker.">
      {mappings.map((m, i) => (
        <div key={i} className="field-row mapping">
          <label className="grow">
            Path in the app
            <input type="text" value={m.from} placeholder="/data" onChange={(e) => set(i, { from: e.target.value })} />
          </label>
          <label className="grow">
            Path on this server
            <input type="text" value={m.to} placeholder="/Volumes/Media/data" onChange={(e) => set(i, { to: e.target.value })} />
          </label>
          <button type="button" className="text-button" onClick={() => setMappings(mappings.filter((_, j) => j !== i))}>
            Remove
          </button>
        </div>
      ))}
      <div className="form-actions">
        <button type="button" className="button quiet small form-actions-start" onClick={() => setMappings([...mappings, { from: '', to: '' }])}>
          Add a mapping
        </button>
        {changed && (
          <>
            <button type="button" className="button quiet small" onClick={() => setMappings(saved)}>
              Cancel
            </button>
            <button type="button" className="button small" onClick={() => void save({ pathMappings: mappings })}>
              Save
            </button>
          </>
        )}
      </div>
    </SettingsGroup>
  )
}

/** The addresses Sonarr, Radarr and Lidarr call when something's downloaded, so it shows up straight away. */
function Webhooks({ secret: savedSecret }: { secret?: string | null }) {
  const [secret, setSecret] = useState(savedSecret)
  const [asking, setAsking] = useState(false)
  const [state, setState] = useState<{ busy?: boolean; error?: string; done?: boolean }>({})
  const origin = window.location.origin

  async function rotate() {
    setState({ busy: true })
    try {
      setSecret((await rotateWebhookSecret()).webhookSecret)
      setAsking(false)
      setState({ done: true })
    } catch (e) {
      setState({ error: errorText(e) })
    }
  }

  return (
    <SettingsGroup
      title="Webhooks"
      note={
        <>
          Add a Webhook connection in each app, under Settings › Connect, so new downloads show up straight away. In Docker on this computer, use{' '}
          <code>host.docker.internal</code> in place of <code>{window.location.hostname}</code>.
        </>
      }
      action={
        <button className="button quiet small" onClick={() => setAsking(true)}>
          Make a new secret
        </button>
      }
    >
      {state.done && (
        <p className="key-note" role="status">
          New secret made. Give Sonarr, Radarr and Lidarr these new addresses: until then they can’t start scans.
        </p>
      )}
      <ul className="plain-list webhook-list">
        {(['Sonarr', 'Radarr', 'Lidarr'] as const).map((app) => {
          const address = `${origin}/api/v1/webhooks/${app.toLowerCase()}?secret=${secret ?? '…'}`
          return (
            <li key={app}>
              <img className="app-logo" src={appLogos[app]} alt="" />
              <span className="webhook-app">{app}</span>
              <code className="secret-field webhook-address" title={address}>
                {address}
              </code>
              {secret && <CopyButton text={address} what={`the ${app} address`} />}
            </li>
          )
        })}
      </ul>
      {asking && (
        <ConfirmDialog
          title="Make a new webhook secret?"
          confirmLabel={state.busy ? 'Making it…' : 'Make a new secret'}
          busy={state.busy}
          error={state.error}
          onConfirm={() => void rotate()}
          onClose={() => {
            setAsking(false)
            setState({})
          }}
        >
          <p>Sonarr, Radarr and Lidarr stop starting scans until you give them the new addresses. The secret only lets them start scans.</p>
        </ConfirmDialog>
      )}
    </SettingsGroup>
  )
}
