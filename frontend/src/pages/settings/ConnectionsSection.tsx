import { createContext, useContext, useState, type FormEvent, type ReactNode } from 'react'
import { Link } from 'react-router'
import {
  addArrWebhook,
  getArrFolders,
  getArrStatus,
  getLiveTvStatus,
  getRequestStatus,
  getSubtitleSearchStatus,
  refreshLiveTv,
  rotateWebhookSecret,
  testArr,
  testLiveTv,
  testTmdb,
  type ArrFolder,
  type ArrKind,
  type ArrStatus,
  type LiveTvSettingsUpdate,
  type LiveTvTest,
  type Settings,
} from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Check, Close, Film, Pencil, Plus, Refresh } from '../../components/icons'
import { icon } from '../../components/icons.generated'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { CopyButton } from '../../components/CopyButton'
import { FolderPicker } from '../../components/FolderPicker'
import { useApi } from '../../hooks/useApi'
import { useChoosesOnMac } from '../../hooks/useChoosesOnMac'
import { usePolling } from '../../hooks/usePolling'
import { appLogos } from '../../lib/apps'
import { OpenApp, SaveStatus, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText, useSettings } from './useSettings'
import { locale, t, tk, Words } from '../../i18n'

/** Settings › Trakt may have come or gone: the Settings page looks again. */
export const TRAKT_CHANGED = 'flexarr:trakt'

/** The connections with a form of their own, by the name the open row goes by. */
type OtherService = 'seerr' | 'bazarr' | 'tmdb' | 'livetv' | 'trakt'

const arrKinds = ['Sonarr', 'Radarr', 'Lidarr'] as const

/** What each app adds, said beside it while it isn't connected. */
const purpose: Record<ArrKind | OtherService, string> = {
  Sonarr: Words.shows,
  Radarr: Words.movies,
  Lidarr: Words.music,
  seerr: tk('Discover and requests'),
  bazarr: Words.subtitles,
  tmdb: tk('Movie details without Radarr'),
  livetv: Words.liveTv,
  trakt: tk('Trakt'),
}

const notConnected = (service: ArrKind | OtherService) => t('Not connected, {purpose}', { purpose: t(purpose[service]) })

interface DraftConnection {
  name: string
  kind: ArrKind
  baseUrl: string
  apiKey: string
  hasApiKey: boolean
}

type Save = ReturnType<typeof useSettings>['save']

/**
 * Every app Flexarr works with, always listed: Sonarr, Radarr and Lidarr (as many of each as are connected, with Add another beside the last), then
 * Seerr, Bazarr, TMDB and Dispatcharr (Live TV). A line each saying how it is, opened to connect or change it. Below them, where the
 * apps' media folders are on this server, and the webhook addresses the apps call when something new arrives.
 */
export function ConnectionsSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  // Asked once the settings are in (and again after a save), not also while they load.
  const status = useApi((signal) => (settings ? getArrStatus(signal) : Promise.resolve(undefined)), [settings])
  const seerrStatus = useApi((signal) => (settings ? getRequestStatus(signal) : Promise.resolve(undefined)), [settings])
  const bazarrStatus = useApi((signal) => (settings ? getSubtitleSearchStatus(signal) : Promise.resolve(undefined)), [settings])
  const liveStatus = useApi((signal) => (settings ? getLiveTvStatus(signal) : Promise.resolve(undefined)), [settings])
  const folders = useApi((signal) => (settings?.arr.length ? getArrFolders(signal) : Promise.resolve(undefined)), [settings])
  // While the channels and guide come in (after a save, or Refresh), keep asking how it's going.
  usePolling(liveStatus.reload, liveStatus.data?.refreshing ? 2000 : null)
  // Which one is open: an index into the list, `new-Sonarr` (and so on) for one being added, 'seerr', 'bazarr', 'tmdb' or 'livetv'.
  const [open, setOpen] = useState<number | `new-${ArrKind}` | OtherService>()
  // How adding Flexarr's webhook to each app went, by connection name.
  const [hooks, setHooks] = useState<Record<string, HookState>>({})
  if (!settings) return waiting

  /** Adds Flexarr's webhook to the app (or brings it up to date), and says how that went beside its address. */
  async function hookUp(name: string) {
    setHooks((h) => ({ ...h, [name]: { busy: true } }))
    let next: HookState
    try {
      const result = await addArrWebhook(name)
      next = result.ok ? { ok: true } : { error: result.error ?? t('Couldn’t add it to {name}.', { name }) }
    } catch (e) {
      next = { error: errorText(e) }
    }
    setHooks((h) => ({ ...h, [name]: next }))
  }

  const current = settings.arr.map((a) => ({ name: a.name, kind: a.kind, baseUrl: a.baseUrl, apiKey: undefined as string | undefined }))
  /** The whole list goes to the server, with this one changed (or taken out); keys left blank stay as they were. */
  async function saveArr(index: number | 'new', next: DraftConnection | null) {
    const list = [...current]
    const entry = next && { name: next.name, kind: next.kind, baseUrl: next.baseUrl, apiKey: next.apiKey || undefined }
    if (index === 'new') list.push(entry!)
    else if (entry) list[index] = entry
    else list.splice(index, 1)
    if (!(await save({ arr: list }))) return
    setOpen(undefined)
    // A new app gets Flexarr's webhook by itself, so its imports show straight away; Webhooks below says how that went,
    // with the address to add by hand if the app can't reach Flexarr.
    if (index === 'new') void hookUp(entry!.name)
  }

  /** A name no connection has yet: the app's own for the first, then Radarr 2 and so on (to be changed to, say, Radarr 4K). */
  function freshName(kind: ArrKind) {
    let n = 1
    while (settings!.arr.some((a) => a.name === (n === 1 ? kind : `${kind} ${n}`))) n++
    return n === 1 ? kind : `${kind} ${n}`
  }

  return (
    <ServerName value={settings.hostName}>
      <section>
        <SettingsHeader title={t('Connections')}>
          {t('The apps Flexarr gets your titles from, and the ones that add requests, subtitles and Live TV.')}
        </SettingsHeader>

        <SettingsGroup title={t('Apps & Integrations')} note={saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : undefined}>
          {arrKinds.map((kind) => {
            const connected = settings.arr.flatMap((a, i) => (a.kind === kind ? [i] : []))
            const adding = open === `new-${kind}`
            return [
              ...connected.map((i, n) => {
                const a = settings.arr[i]
                return (
                  <ArrRow
                    key={`${a.name}-${i}`}
                    connection={{ ...a, apiKey: '' }}
                    live={status.data?.find((s) => s.name === a.name)}
                    unfound={folders.data?.filter((f) => f.app === a.name && (f.state === 'Suggested' || f.state === 'Missing')).map((f) => f.path)}
                    open={open === i}
                    onOpen={() => setOpen(i)}
                    onClose={() => setOpen(undefined)}
                    onSave={(c) => saveArr(i, c)}
                    onRemove={() => saveArr(i, null)}
                    others={connected.length > 1}
                    onAddAnother={n === connected.length - 1 && !adding ? () => setOpen(`new-${kind}`) : undefined}
                  />
                )
              }),
              // Not connected yet (a line with Connect), or another of this kind being added under the ones there are.
              (connected.length === 0 || adding) && (
                <ArrRow
                  key={`new-${kind}`}
                  connection={{ name: freshName(kind), kind, baseUrl: '', apiKey: '', hasApiKey: false }}
                  isNew
                  open={adding}
                  onOpen={() => setOpen(`new-${kind}`)}
                  onClose={() => setOpen(undefined)}
                  onSave={(c) => saveArr('new', c)}
                />
              ),
              // Lidarr is also where music requests go: on unless turned off here (servers before 0.59 don't say).
              kind === 'Lidarr' && connected.length > 0 && settings.allowMusicRequests != null && (
                <ToggleRow
                  key="music-requests"
                  label={t('Music requests')}
                  help={t('Discover and request artists and albums.')}
                  checked={settings.allowMusicRequests}
                  onChange={(on) => void save({ allowMusicRequests: on })}
                />
              ),
            ]
          })}
          <ServiceRow
            kind="Seerr"
            service={settings.seerr}
            notConnected={notConnected('seerr')}
            description={t('Discover and one-click requests. Overseerr and Jellyseerr work too.')}
            placeholder="http://localhost:5055"
            keyHint={t('In {app}, under Settings › General', { app: 'Seerr' })}
            removeWarning={t('Disconnect Seerr? Discover and requests go with it.')}
            status={serviceStatus(seerrStatus)}
            ok={seerrStatus.data?.configured ? seerrStatus.data.reachable : undefined}
            open={open === 'seerr'}
            onOpen={() => setOpen('seerr')}
            onClose={() => setOpen(undefined)}
            onSave={async (seerr) => {
              if (await save({ seerr })) setOpen(undefined)
            }}
          />
          <ServiceRow
            kind="Bazarr"
            service={settings.bazarr}
            notConnected={notConnected('bazarr')}
            description={t('Find and re-sync subtitles from the player.')}
            more={
              <p>
                {t('It uses the languages and subtitle sites set up in Bazarr, for titles Sonarr and Radarr manage. A title with no languages profile gets Bazarr’s first one, so Bazarr may then fetch subtitles for it on its own schedule too.')}
              </p>
            }
            placeholder="http://localhost:6767"
            keyHint={t('In {app}, under Settings › General', { app: 'Bazarr' })}
            removeWarning={t('Disconnect Bazarr? Searching for subtitles from the player goes with it.')}
            status={serviceStatus(bazarrStatus)}
            ok={bazarrStatus.data?.configured ? bazarrStatus.data.reachable : undefined}
            open={open === 'bazarr'}
            onOpen={() => setOpen('bazarr')}
            onClose={() => setOpen(undefined)}
            onSave={async (bazarr) => {
              if (await save({ bazarr })) setOpen(undefined)
            }}
          />
          <TmdbRow
            hasKey={settings.hasTmdbKey === true}
            hasRadarr={settings.arr.some((a) => a.kind === 'Radarr')}
            open={open === 'tmdb'}
            onOpen={() => setOpen('tmdb')}
            onClose={() => setOpen(undefined)}
            onSave={async (apiKey) => {
              if (await save({ tmdb: { apiKey } })) setOpen(undefined)
            }}
          />
          <TraktAppRow
            app={settings.traktApp ?? null}
            open={open === 'trakt'}
            onOpen={() => setOpen('trakt')}
            onClose={() => setOpen(undefined)}
            onSave={async (traktApp) => {
              // Trakt's sign-ins belong to the app that gave them: everyone connects again after a switch (a new secret for
              // the same app changes nothing for them).
              if ((traktApp.clientId || null) !== (settings.traktApp?.clientId || null) && !confirm(t('Change the Trakt app? Everyone who connected to Trakt will need to connect again.'))) return
              if (!(await save({ traktApp }))) return
              setOpen(undefined)
              window.dispatchEvent(new Event(TRAKT_CHANGED))
            }}
          />
          <DispatcharrRow
            liveTv={settings.liveTv ?? null}
            status={
              liveStatus.loading
                ? t('Checking…')
                : !liveStatus.data?.configured
                  ? notConnected('livetv')
                  : liveStatus.data.refreshing
                    ? t('Bringing in the channels and guide…')
                    : (liveStatus.data.error ?? liveStatus.data.downloadError ?? t('Live TV: {count, plural, one {# channel} other {# channels}}', { count: liveStatus.data.channels }))
            }
            ok={liveStatus.data?.configured ? !liveStatus.data.error && !liveStatus.data.downloadError : undefined}
            refreshing={liveStatus.data?.refreshing ?? false}
            open={open === 'livetv'}
            onOpen={() => setOpen('livetv')}
            onClose={() => setOpen(undefined)}
            onSave={async (liveTv, message) => {
              if (await save({ liveTv }, message)) setOpen(undefined)
            }}
            onRefresh={async () => {
              await refreshLiveTv()
              // The button keeps spinning until the status (polled while refreshing) has had time to say it's under way.
              await new Promise((done) => setTimeout(done, 500))
              liveStatus.reload()
            }}
          />
        </SettingsGroup>

        <MediaFolders folders={folders.data} hasApps={settings.arr.length > 0} mappings={settings.pathMappings} save={save} />
        <Webhooks secret={settings.webhookSecret} connections={settings.arr} hooks={hooks} onHookUp={hookUp} />
      </section>
    </ServerName>
  )
}

/** The server's name on the network, for Open links to apps it reaches at localhost. */
const ServerName = createContext<string | null | undefined>(undefined)

/** A connection's line: whether it's reachable, its version, a link to it beside its name, and Edit, which opens the form under it. */
function ConnectionLine({
  kind,
  icon,
  name,
  ok,
  status,
  warning,
  baseUrl,
  connected = true,
  open,
  onOpen,
  onAddAnother,
}: {
  kind?: keyof typeof appLogos
  /** In place of an app's logo, for a service with none here. */
  icon?: ReactNode
  name: string
  ok?: boolean
  status: ReactNode
  /** Something to see to though it's connected: shown under the status, with an amber dot. */
  warning?: ReactNode
  baseUrl: string
  /** False for an app that isn't set up: Connect in place of Edit. */
  connected?: boolean
  open: boolean
  onOpen: () => void
  /** For Sonarr, Radarr and Lidarr, which can have more than one each. */
  onAddAnother?: () => void
}) {
  const host = useContext(ServerName)
  return (
    <div className={connected ? 'connection-line' : 'connection-line unconnected'}>
      <span className={ok && warning ? 'status-dot warn' : ok ? 'status-dot ok' : ok === false ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
      {kind ? (
        <img className="app-logo" src={appLogos[kind]} alt="" />
      ) : (
        <span className="app-logo app-logo-icon" aria-hidden="true">
          {icon}
        </span>
      )}
      <span className="setting-text">
        <strong className="connection-name">
          {name}
          {baseUrl && <OpenApp baseUrl={baseUrl} name={name} host={host} />}
        </strong>
        <span className="muted">{status}</span>
        {warning && <span className="connection-warning">{warning}</span>}
      </span>
      <span className="connection-actions">
        {onAddAnother && <IconButton label={t('Add another {app}', { app: kind })} icon={<Plus />} onClick={onAddAnother} />}
        {!open &&
          (connected ? (
            <IconButton label={t('Edit')} aria-label={t('Edit {name}', { name })} icon={<Pencil />} onClick={onOpen} />
          ) : (
            <button type="button" className="button quiet small go" aria-label={t('Connect {name}', { name })} onClick={onOpen}>
              {t('Connect')}
            </button>
          ))}
      </span>
    </div>
  )
}

function ArrRow({
  connection,
  live,
  unfound,
  isNew,
  open,
  onOpen,
  onClose,
  onSave,
  onRemove,
  others,
  onAddAnother,
}: {
  connection: DraftConnection
  live?: ArrStatus
  /** Its root folders that aren't at the same path here and have no mapping. */
  unfound?: string[]
  isNew?: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (c: DraftConnection) => Promise<void>
  onRemove?: () => Promise<void>
  /** Others of its kind are connected, so removing this one takes its row away rather than leaving it to connect again. */
  others?: boolean
  onAddAnother?: () => void
}) {
  const [draft, setDraft] = useState(connection)
  const [test, setTest] = useState<ArrStatus | 'testing'>()
  const [saving, setSaving] = useState(false)
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
    setSaving(true)
    void onSave(draft).finally(() => setSaving(false))
  }

  const statusText = test === 'testing' ? t('Testing…') : shown?.ok ? t('Connected, version {version}', { version: shown.version }) : shown ? shown.error : isNew ? notConnected(connection.kind) : t('Checking…')
  return (
    <div className="connection">
      <ConnectionLine
        kind={draft.kind}
        name={draft.name || draft.kind}
        ok={shown?.ok}
        status={statusText}
        warning={
          unfound?.length ? (
            <>
              {unfound.length === 1
                ? t.rich('Its {folder} folder isn’t at that path on this server.', { folder: <code>{unfound[0]}</code> })
                : t('{count, plural, one {# of its folders isn’t at that path on this server.} other {# of its folders aren’t at those paths on this server.}}', { count: unfound.length })}{' '}
              <a href="#media-folders">{t('{count, plural, one {Find it} other {Find them}}', { count: unfound.length })}</a>
            </>
          ) : undefined
        }
        baseUrl={isNew ? '' : connection.baseUrl}
        connected={!isNew}
        open={open}
        onOpen={onOpen}
        onAddAnother={onAddAnother}
      />
      {open && (
        <form className="connection-form" onSubmit={submit} aria-label={isNew ? t('Connect {name}', { name: connection.kind }) : t('Edit {name}', { name: connection.name })}>
          <div className="field-row">
            <label>
              {t('Name')}
              <input type="text" value={draft.name} required onChange={(e) => update({ name: e.target.value })} />
            </label>
            <label className="grow">
              {t('Address')}
              <input type="url" value={draft.baseUrl} required placeholder={placeholderUrl[draft.kind]} onChange={(e) => update({ baseUrl: e.target.value })} />
            </label>
          </div>
          <label className="field">
            <span className="field-label">{t('API key')}</span>
            <input
              type="password"
              value={draft.apiKey}
              placeholder={connection.hasApiKey ? t('Saved; type to replace') : t('In {app}, under Settings › General', { app: draft.kind })}
              onChange={(e) => update({ apiKey: e.target.value })}
            />
          </label>
          <div className="form-actions">
            {onRemove &&
              (others ? (
                <IconButton label={t('Remove')} aria-label={t('Remove {name}', { name: connection.name })} icon={<icon.delete />} className="button quiet small icon danger-icon form-actions-start" onClick={() => confirm(t('Remove {name}?', { name: connection.name })) && onRemove()} />
              ) : (
                <AsyncButton className="button danger small form-actions-start" aria-label={t('Disconnect {name}', { name: connection.name })} onClick={() => (confirm(t('Disconnect {name}? Its titles stay in your libraries.', { name: connection.name })) ? onRemove() : undefined)}>
                  {t('Disconnect')}
                </AsyncButton>
              ))}
            <AsyncButton className="button quiet small" onClick={() => runTest()}>
              {test && test !== 'testing' && <TestMark ok={test.ok} />}
              {t('Test')}
            </AsyncButton>
            <button
              type="button"
              className="button quiet small"
              onClick={() => {
                setDraft(connection)
                setTest(undefined)
                onClose()
              }}
            >
              {t(Words.cancel)}
            </button>
            <AsyncButton type="submit" className={isNew ? 'button small go' : 'button small'} busy={saving}>
              {isNew ? t('Connect') : t('Save')}
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

const placeholderUrl: Record<ArrKind, string> = { Sonarr: 'http://localhost:8989', Radarr: 'http://localhost:7878', Lidarr: 'http://localhost:8686' }

/** A service with an address and an API key (Seerr, Bazarr); the key is never sent back, so the form only replaces it. */
function ServiceRow({
  kind,
  service,
  notConnected,
  description,
  more,
  placeholder,
  keyHint,
  removeWarning,
  status,
  ok,
  open,
  onOpen,
  onClose,
  onSave,
}: {
  kind: 'Seerr' | 'Bazarr'
  service: { baseUrl: string; hasApiKey: boolean } | null | undefined
  /** Its status while it isn't set up. */
  notConnected: string
  description: string
  /** More about it, behind a disclosure under the description. */
  more?: ReactNode
  placeholder: string
  keyHint: string
  removeWarning: string
  status: string
  ok?: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (service: { baseUrl: string; apiKey?: string }) => Promise<void>
}) {
  const [baseUrl, setBaseUrl] = useState(service?.baseUrl ?? '')
  const [apiKey, setApiKey] = useState('')
  const [saving, setSaving] = useState(false)
  return (
    <div className="connection">
      <ConnectionLine
        kind={kind}
        name={kind}
        ok={ok}
        status={service ? status : notConnected}
        baseUrl={service?.baseUrl ?? ''}
        connected={!!service}
        open={open}
        onOpen={onOpen}
      />
      {open && (
        <form
          className="connection-form"
          aria-label={service ? t('Edit {name}', { name: kind }) : t('Connect {name}', { name: kind })}
          onSubmit={(e) => {
            e.preventDefault()
            setSaving(true)
            void onSave({ baseUrl, apiKey: apiKey || undefined }).finally(() => setSaving(false))
          }}
        >
          <p className="muted">{description}</p>
          {more && (
            <details className="advice-more muted">
              <summary>{t('How it works')}</summary>
              {more}
            </details>
          )}
          <div className="field-row">
            <label className="grow">
              {t('Address')}
              <input type="url" value={baseUrl} placeholder={placeholder} onChange={(e) => setBaseUrl(e.target.value)} />
            </label>
            <label className="grow">
              {t('API key')}
              <input type="password" value={apiKey} placeholder={service?.hasApiKey ? t('Saved; type to replace') : keyHint} onChange={(e) => setApiKey(e.target.value)} />
            </label>
          </div>
          <div className="form-actions">
            {service && (
              <AsyncButton className="button danger small form-actions-start" aria-label={t('Disconnect {name}', { name: kind })} onClick={() => (confirm(removeWarning) ? onSave({ baseUrl: '' }) : undefined)}>
                {t('Disconnect')}
              </AsyncButton>
            )}
            <button type="button" className="button quiet small" onClick={onClose}>
              {t(Words.cancel)}
            </button>
            <AsyncButton type="submit" className={service ? 'button small' : 'button small go'} busy={saving}>
              {service ? t('Save') : t('Connect')}
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

/** How a service with an address and a key is: checking, not set up, connected, or not answering. */
function serviceStatus(check: { loading: boolean; data?: { configured: boolean; reachable: boolean } }) {
  return check.loading ? t('Checking…') : !check.data?.configured ? t('Not set up') : check.data.reachable ? t('Connected') : t('Can’t be reached, or the API key isn’t right')
}

/**
 * The owner's own TMDB key, for films Radarr doesn't have (or all of them, without Radarr). It's never sent back, so
 * the form only replaces or removes it.
 */
function TmdbRow({
  hasKey,
  hasRadarr,
  open,
  onOpen,
  onClose,
  onSave,
}: {
  hasKey: boolean
  hasRadarr: boolean
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (apiKey: string) => Promise<void>
}) {
  const [apiKey, setApiKey] = useState('')
  const [saving, setSaving] = useState(false)
  const [test, setTest] = useState<{ ok: boolean; error: string | null }>()
  const status = test && !test.ok ? (test.error ?? t('TMDB didn’t accept the key')) : hasKey ? t('Key saved') : notConnected('tmdb')
  return (
    <div className="connection">
      <ConnectionLine icon={<Film />} name="TMDB" ok={test ? test.ok : hasKey ? true : undefined} status={status} baseUrl="" connected={hasKey} open={open} onOpen={onOpen} />
      {open && (
        <form
          className="connection-form"
          aria-label={t('Edit TMDB')}
          onSubmit={(e) => {
            e.preventDefault()
            setSaving(true)
            void onSave(apiKey.trim()).finally(() => setSaving(false))
          }}
        >
          <p className="muted">
            {hasRadarr ? t('Details, artwork and cast for movies Radarr doesn’t have.') : t('Details, artwork and cast for your movies.')} {t('Shows come from TVmaze and need no key.')}
          </p>
          <div className="field-row">
            <label className="grow">
              {t('API key')}
              <input
                type="password"
                value={apiKey}
                autoComplete="off"
                placeholder={hasKey ? t('Saved; paste to replace') : t('API key or read access token')}
                onChange={(e) => {
                  setApiKey(e.target.value)
                  setTest(undefined)
                }}
              />
            </label>
          </div>
          <p className="muted field-hint">
            {t.rich('Free from {site} by registering an app for personal use.', {
              site: (
                <a href="https://www.themoviedb.org/settings/api" target="_blank" rel="noreferrer">
                  themoviedb.org
                </a>
              ),
            })}{' '}
            <a href="https://flexarr.io/guide/without-arrs#movies-from-tmdb" target="_blank" rel="noreferrer">
              {t('How to get a key')}
            </a>
          </p>
          {test?.ok && (
            <p className="connection-ok" role="status">
              <Check /> {t('TMDB accepted the key.')}
            </p>
          )}
          <div className="form-actions">
            {hasKey && (
              <AsyncButton
                type="button"
                className="button danger small form-actions-start"
                aria-label={t('Disconnect TMDB')}
                onClick={() => (confirm(t('Disconnect TMDB? Its key is forgotten, and movies already filled in keep their details.')) ? onSave('') : undefined)}
              >
                {t('Disconnect')}
              </AsyncButton>
            )}
            <AsyncButton type="button" className="button quiet small" disabled={!apiKey.trim() && !hasKey} onClick={async () => setTest(await testTmdb(apiKey.trim()))}>
              {test && <TestMark ok={test.ok} />}
              {t('Test')}
            </AsyncButton>
            <button type="button" className="button quiet small" onClick={onClose}>
              {t(Words.cancel)}
            </button>
            <AsyncButton type="submit" className={hasKey ? 'button small' : 'button small go'} busy={saving} disabled={!apiKey.trim()}>
              {hasKey ? t('Save') : t('Connect')}
            </AsyncButton>
          </div>
          <p className="muted field-hint">{t('This product uses the TMDB API but is not endorsed or certified by TMDB.')}</p>
        </form>
      )}
    </div>
  )
}

/**
 * Trakt works through Flexarr's own Trakt app. Someone building Flexarr themselves, or who wants their own, can give
 * their app's id and secret instead; the secret is never sent back.
 */
function TraktAppRow({
  app,
  open,
  onOpen,
  onClose,
  onSave,
}: {
  app: { clientId: string; hasSecret: boolean } | null
  open: boolean
  onOpen: () => void
  onClose: () => void
  onSave: (app: { clientId: string; clientSecret?: string }) => Promise<void>
}) {
  const [clientId, setClientId] = useState(app?.clientId ?? '')
  const [secret, setSecret] = useState('')
  const [saving, setSaving] = useState(false)
  return (
    <div className="connection">
      <ConnectionLine icon={<Refresh />} name="Trakt" ok={app ? app.hasSecret : undefined} status={app ? t('Your own Trakt app') : t('Flexarr’s Trakt app')} baseUrl="" connected open={open} onOpen={onOpen} />
      {open && (
        <form
          className="connection-form"
          aria-label={t('Edit Trakt')}
          onSubmit={(e) => {
            e.preventDefault()
            setSaving(true)
            void onSave({ clientId: clientId.trim(), clientSecret: secret.trim() || undefined }).finally(() => setSaving(false))
          }}
        >
          <p className="muted">{t('Each profile connects its own Trakt account in its Settings. To use your own Trakt app instead of Flexarr’s, give its details.')}</p>
          <div className="field-row">
            <label className="grow">
              {t('Client ID')}
              <input value={clientId} autoComplete="off" onChange={(e) => setClientId(e.target.value)} />
            </label>
            <label className="grow">
              {t('Client secret')}
              <input type="password" value={secret} autoComplete="off" placeholder={app?.hasSecret ? t('Saved; paste to replace') : ''} onChange={(e) => setSecret(e.target.value)} />
            </label>
          </div>
          <div className="form-actions">
            {app && (
              <AsyncButton type="button" className="button danger small form-actions-start" onClick={() => onSave({ clientId: '' })}>
                {t('Use Flexarr’s app')}
              </AsyncButton>
            )}
            <button type="button" className="button quiet small" onClick={onClose}>
              {t(Words.cancel)}
            </button>
            <AsyncButton type="submit" className="button small" busy={saving} disabled={!clientId.trim() || (!secret.trim() && !(app?.hasSecret && app.clientId === clientId.trim()))}>
              {t('Save')}
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

/**
 * Dispatcharr, for Live TV: Flexarr signs in to its Xtream Codes API as a Dispatcharr user of its own, and the
 * channels and guide are the ones Dispatcharr gives that user.
 */
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
  const [adminKey, setAdminKey] = useState('')
  const [test, setTest] = useState<LiveTvTest | 'testing'>()
  const [saving, setSaving] = useState(false)
  // Typed keys don't outlive the form: reopened, a Save would send them again, undoing a removal or reaching a new address.
  const [wasOpen, setWasOpen] = useState(open)
  if (open !== wasOpen) {
    setWasOpen(open)
    if (!open) {
      setPassword('')
      setAdminKey('')
    }
  }
  const keyTested = !!adminKey.trim() || (!!liveTv?.hasAdminApiKey && liveTv.baseUrl === baseUrl.trim())
  const shown = test === 'testing' ? t('Testing…') : test ? (test.ok ? testSummary(test, keyTested) : test.error) : status

  return (
    <div className="connection">
      <ConnectionLine kind="Dispatcharr" name="Dispatcharr" ok={test && test !== 'testing' ? test.ok : ok} status={shown} baseUrl={liveTv?.baseUrl ?? ''} connected={!!liveTv} open={open} onOpen={onOpen} />
      {open && (
        <form
          className="connection-form"
          aria-label={t('Edit Dispatcharr')}
          onSubmit={(e) => {
            e.preventDefault()
            setTest(undefined)
            setSaving(true)
            void onSave({
              baseUrl: baseUrl.trim(),
              username: username.trim(),
              password: password || undefined,
              adminApiKey: adminKey.trim() || undefined,
            }).finally(() => setSaving(false))
          }}
        >
          <p className="muted">{t('TV channels and their guide.')}</p>
          <details className="advice-more muted">
            <summary>{t('How to set it up')}</summary>
            <p>
              {t('In Dispatcharr, add a user for Flexarr at Streamer level with a long random XC password, and use that password as the key here. It can only list and play the channels Dispatcharr gives that user.')}
            </p>
            <p>
              {t.rich('Rewinding and recording are in {page}.', { page: <Link to="/settings/livetv">{t(Words.liveTv)}</Link> })}
            </p>
          </details>
          <div className="field-row">
            <label className="grow">
              {t('Address')}
              <input type="url" value={baseUrl} required placeholder="http://localhost:9191" onChange={(e) => setBaseUrl(e.target.value)} />
            </label>
            <label>
              {t('User name')}
              <input type="text" autoComplete="off" value={username} required placeholder="flexarr" onChange={(e) => setUsername(e.target.value)} />
            </label>
            <label>
              {t('Key (XC password)')}
              <input
                type="password"
                autoComplete="new-password"
                value={password}
                placeholder={liveTv?.hasPassword ? t('Saved; type to replace') : t('The user’s XC password in Dispatcharr')}
                onChange={(e) => setPassword(e.target.value)}
              />
            </label>
          </div>
          <div className="field-row">
            <label className="grow">
              {t('Admin API key (optional)')}
              <input
                type="password"
                autoComplete="new-password"
                value={adminKey}
                placeholder={liveTv?.hasAdminApiKey ? t('Saved; type to replace') : t('A Dispatcharr admin’s API key')}
                onChange={(e) => setAdminKey(e.target.value)}
              />
            </label>
          </div>
          <p className="muted field-hint">{t('Lets Refresh have Dispatcharr download its guides first. Gives Flexarr admin rights there.')}</p>
          {liveTv?.hasAdminApiKey && (
            <p>
              <AsyncButton type="button" className="button quiet small" onClick={() => onSave({ baseUrl: liveTv.baseUrl, username: liveTv.username, adminApiKey: '' }, t('Admin API key removed'))}>
                {t('Remove admin API key')}
              </AsyncButton>
            </p>
          )}
          <div className="form-actions">
            {liveTv && (
              <AsyncButton
                type="button"
                className="button danger small form-actions-start"
                aria-label={t('Disconnect Dispatcharr')}
                onClick={() => (confirm(t('Disconnect Dispatcharr? Live TV, its channels and everyone’s favourites go with it.')) ? onSave({ baseUrl: '' }, t('Live TV disconnected')) : undefined)}
              >
                {t('Disconnect')}
              </AsyncButton>
            )}
            {liveTv && (
              <IconButton label={t('Refresh guide')} icon={<Refresh />} busy={refreshing} onClick={() => onRefresh()} />
            )}
            <AsyncButton
              className="button quiet small"
              disabled={!baseUrl || !username}
              onClick={async () => {
                setTest('testing')
                setTest(
                  await testLiveTv({ baseUrl: baseUrl.trim(), username: username.trim(), password: password || undefined, adminApiKey: adminKey.trim() || undefined }).catch(
                    (e: unknown): LiveTvTest => ({ ok: false, channels: null, maxConnections: null, expiresAt: null, error: errorText(e) }),
                  ),
                )
              }}
            >
              {test && test !== 'testing' && <TestMark ok={test.ok} />}
              {t('Test')}
            </AsyncButton>
            <button type="button" className="button quiet small" onClick={onClose}>
              {t(Words.cancel)}
            </button>
            <AsyncButton type="submit" className={liveTv ? 'button small' : 'button small go'} busy={saving}>
              {liveTv ? t('Save') : t('Connect')}
            </AsyncButton>
          </div>
        </form>
      )}
    </div>
  )
}

function testSummary(test: LiveTvTest, keyTested: boolean) {
  const parts = [t('Connected: {count, plural, one {# channel} other {# channels}}', { count: test.channels ?? 0 })]
  if (keyTested) parts.push(t('admin key accepted'))
  if (test.maxConnections) parts.push(t('{count, plural, one {# connection} other {# connections}} at a time', { count: test.maxConnections }))
  if (test.expiresAt) parts.push(t('subscription ends {date}', { date: new Date(test.expiresAt).toLocaleDateString(locale()) }))
  return parts.join(', ')
}

/**
 * Where Sonarr, Radarr and Lidarr's root folders are on this server, one line each. In Docker the apps see the media
 * under other paths; the server works out where from titles both sides have, and saves it itself when enough agree.
 * What it can't be sure of is a suggestion to take, or a folder to choose. Mappings can still be written by hand below
 * (Plex's paths, for an import, go through them too).
 */
function MediaFolders({ folders, hasApps, mappings, save }: { folders?: ArrFolder[]; hasApps: boolean; mappings: Settings['pathMappings']; save: Save }) {
  const [choosing, setChoosing] = useState<string>()

  /** Saves a mapping for one folder, in place of any from the same path. */
  const map = (from: string, to: string) => save({ pathMappings: [...mappings.filter((m) => m.from !== from), { from, to }] }, t('Mapping saved'))

  return (
    <div id="media-folders">
      <SettingsGroup
        title={t('Media folders')}
        note={
          hasApps
            ? t('Where each app’s folders are on this server. Flexarr works it out when it can.')
            : t('Only needed when another app, such as Plex for an import, sees your media under other paths (as in Docker).')
        }
      >
        {folders?.map((f) => (
          <div key={`${f.app}:${f.path}`} className="connection">
            <div className="connection-line media-folder">
              <span className={f.state === 'Found' || f.state === 'Mapped' ? 'status-dot ok' : 'status-dot warn'} aria-hidden="true" />
              <img className="app-logo" src={appLogos[f.kind]} alt="" />
              <span className="setting-text">
                <strong>
                  <code>{f.path}</code> <span className="muted">{t('in {app}', { app: f.app })}</span>
                </strong>
                <span className="muted">
                  <FolderState folder={f} />
                </span>
              </span>
              <span className="connection-actions">
                {f.state === 'Suggested' && f.suggested && (
                  <AsyncButton className="button small" onClick={() => map(f.suggested!.from, f.suggested!.to)}>
                    {t('Use this')}
                  </AsyncButton>
                )}
                {f.state !== 'Found' && (
                  <button type="button" className="button quiet small" onClick={() => setChoosing(choosing === f.path ? undefined : f.path)}>
                    {f.state === 'Mapped' ? t('Change') : f.state === 'Suggested' ? t('Choose another') : t('Choose folder')}
                  </button>
                )}
              </span>
            </div>
            {choosing === f.path && (
              <div className="connection-form">
                <p className="muted">
                  {t.rich('Where is {app}’s {folder} on this server?', { app: f.app, folder: <code>{f.path}</code> })}
                </p>
                <FolderPicker onPick={(to) => void map(f.path, to).then((ok) => ok && setChoosing(undefined))} />
                <button type="button" className="text-button" onClick={() => setChoosing(undefined)}>
                  {t(Words.cancel)}
                </button>
              </div>
            )}
          </div>
        ))}
        <SavedMappings key={JSON.stringify(mappings)} mappings={mappings} save={save} />
      </SettingsGroup>
    </div>
  )
}

/** Where one of an app's folders is here, or what says where it might be. */
function FolderState({ folder: f }: { folder: ArrFolder }) {
  switch (f.state) {
    case 'Found':
      return <>{t('At the same path on this server')}</>
    case 'Mapped':
      return (
        <>{t.rich('On this server at {folder}', { folder: <code>{f.local}</code> })}</>
      )
    case 'Suggested':
      return (
        <>
          {f.agreeing > 0
            ? t.rich('Not at that path here, but {titles} in both {count, plural, one {is} other {are}} in {folder}', {
                titles: titlesAgreeing(f.kind, f.agreeing, f.compared),
                count: f.compared,
                folder: <code>{f.suggested?.to}</code>,
              })
            : t.rich('Not at that path here. Perhaps {folder}, which has the same name', { folder: <code>{f.suggested?.to}</code> })}
        </>
      )
    default:
      return <>{t('Not at that path here, and no titles say where it is. Choose the folder it is on this server.')}</>
  }
}

/** "the 2 artists", "all 120 shows", "110 of the 120 films": the titles behind a suggestion. */
function titlesAgreeing(kind: ArrKind, agreeing: number, compared: number) {
  if (compared === 1) return t('{kind, select, Sonarr {the one show} Radarr {the one movie} other {the one artist}}', { kind })
  if (agreeing < compared)
    return t('{kind, select, Sonarr {{agreeing, number} of the {compared, plural, one {# show} other {# shows}}} Radarr {{agreeing, number} of the {compared, plural, one {# movie} other {# movies}}} other {{agreeing, number} of the {compared, plural, one {# artist} other {# artists}}}}', { kind, agreeing, compared })
  return compared < 5
    ? t('{kind, select, Sonarr {the {compared, plural, one {# show} other {# shows}}} Radarr {the {compared, plural, one {# movie} other {# movies}}} other {the {compared, plural, one {# artist} other {# artists}}}}', { kind, compared })
    : t('{kind, select, Sonarr {all {compared, plural, one {# show} other {# shows}}} Radarr {all {compared, plural, one {# movie} other {# movies}}} other {all {compared, plural, one {# artist} other {# artists}}}}', { kind, compared })
}

/**
 * The mappings saved (worked out, taken from a suggestion, or written by hand): a line saying how many, opened to edit
 * them by hand. Saved together, once changed.
 */
function SavedMappings({ mappings: saved, save }: { mappings: Settings['pathMappings']; save: Save }) {
  const [editing, setEditing] = useState(false)
  const [mappings, setMappings] = useState(saved)
  // The mapping whose server folder is being chosen. The Mac app reaches only folders chosen in macOS's folder window,
  // so there the server's side is chosen, never typed.
  const [choosing, setChoosing] = useState<number>()
  const onMac = useChoosesOnMac()
  const changed = JSON.stringify(mappings) !== JSON.stringify(saved)
  const set = (i: number, change: Partial<Settings['pathMappings'][number]>) => setMappings(mappings.map((x, j) => (j === i ? { ...x, ...change } : x)))

  if (!editing)
    return (
      <div className="saved-mappings connection-line">
        <span className="setting-text">
          <strong>{saved.length ? t('Path mappings: {count, number}', { count: saved.length }) : t('Path mappings')}</strong>
          <span className="muted">{t('Choosing a folder above saves one. Edit them to type a path yourself or remove an old one.')}</span>
        </span>
        <button
          type="button"
          className="button quiet small"
          onClick={() => {
            setMappings(saved.length ? saved : [{ from: '', to: '' }])
            setEditing(true)
          }}
        >
          {saved.length ? t('Edit mappings') : t('Add a mapping')}
        </button>
      </div>
    )

  return (
    <div className="saved-mappings">
      {mappings.map((m, i) => (
        <div key={i} className="field-row mapping">
          <label className="grow">
            {t('Path in the app')}
            <input type="text" value={m.from} placeholder="/data" onChange={(e) => set(i, { from: e.target.value })} />
          </label>
          {onMac ? (
            <div className="grow mapping-chosen">
              {t('Path on this server')}
              <span className="mapping-chosen-row">
                <code>{m.to || t('None chosen')}</code>
                {choosing !== i && (
                  <button type="button" className="button quiet small" onClick={() => setChoosing(i)}>
                    {m.to ? t('Change') : t('Choose…')}
                  </button>
                )}
              </span>
            </div>
          ) : (
            <label className="grow">
              {t('Path on this server')}
              <input type="text" value={m.to} placeholder="/Volumes/Media/data" onChange={(e) => set(i, { to: e.target.value })} />
            </label>
          )}
          <IconButton
            label={t('Remove')}
            aria-label={m.from ? t('Remove the mapping for {path}', { path: m.from }) : t('Remove the mapping for this path')}
            icon={<icon.delete />}
            className="button quiet small icon danger-icon"
            onClick={() => {
              setMappings(mappings.filter((_, j) => j !== i))
              setChoosing(undefined)
            }}
          />
          {choosing === i && (
            <div className="mapping-picker">
              <FolderPicker
                onPick={(to) => {
                  set(i, { to })
                  setChoosing(undefined)
                }}
              />
              <button type="button" className="text-button" onClick={() => setChoosing(undefined)}>
                {t(Words.cancel)}
              </button>
            </div>
          )}
        </div>
      ))}
      <div className="form-actions">
        <button type="button" className="button quiet small form-actions-start" onClick={() => setMappings([...mappings, { from: '', to: '' }])}>
          {t('Add a mapping')}
        </button>
        <button type="button" className="button quiet small" onClick={() => setEditing(false)}>
          {t(Words.cancel)}
        </button>
        <AsyncButton className="button small" disabled={!changed} onClick={() => save({ pathMappings: mappings })}>
          {t('Save')}
        </AsyncButton>
      </div>
    </div>
  )
}

/** Adding Flexarr's webhook to one app: under way, done, or why it couldn't. */
type HookState = { busy?: boolean; ok?: boolean; error?: string }

/**
 * The addresses Sonarr, Radarr and Lidarr call when something's downloaded, so it shows up straight away, with a button
 * per connected app that adds it there (done by itself when an app is connected).
 */
function Webhooks({
  secret: savedSecret,
  connections,
  hooks,
  onHookUp,
}: {
  secret?: string | null
  connections: { name: string; kind: ArrKind }[]
  hooks: Record<string, HookState>
  onHookUp: (name: string) => void
}) {
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
      title={t('Webhooks')}
      note={
        <>
          {t.rich('So new downloads show up straight away. {addTo} puts it in the app, or add a Webhook there under Settings › Connect. In Docker on this computer, use {dockerHost} for {host}.', {
            addTo: <strong>{t('Add to')}</strong>,
            dockerHost: <code>host.docker.internal</code>,
            host: <code>{window.location.hostname}</code>,
          })}
        </>
      }
      action={
        <button className="button quiet small" onClick={() => setAsking(true)}>
          {t('Make a new secret')}
        </button>
      }
    >
      {state.done && (
        <p className="key-note" role="status">
          {t('New secret made. Add it to each app below (or give them these new addresses): until then they can’t start scans.')}
        </p>
      )}
      <ul className="plain-list webhook-list">
        {(['Sonarr', 'Radarr', 'Lidarr'] as const).map((app) => {
          const address = `${origin}/api/v1/webhooks/${app.toLowerCase()}?secret=${secret ?? '…'}`
          const mine = connections.filter((c) => c.kind === app)
          return (
            <li key={app}>
              <img className="app-logo" src={appLogos[app]} alt="" />
              <span className="webhook-app">{app}</span>
              <code className="secret-field webhook-address" title={address}>
                {address}
              </code>
              {secret ? <CopyButton text={address} what={t('the {app} address', { app })} /> : <span />}
              <span className="webhook-hooks">
                {mine.map((c) => (
                  <AsyncButton key={c.name} className="button quiet small" busy={hooks[c.name]?.busy} onClick={() => onHookUp(c.name)}>
                    {hooks[c.name]?.ok ? <Check className="test-ok" /> : hooks[c.name]?.error ? <Close className="test-bad" /> : null}
                    {hooks[c.name]?.ok ? t('Added to {name}', { name: c.name }) : t('Add to {name}', { name: c.name })}
                  </AsyncButton>
                ))}
              </span>
              {mine.flatMap((c) =>
                hooks[c.name]?.error
                  ? [
                      <p key={c.name} className="error webhook-error" role="alert">
                        {hooks[c.name].error}
                      </p>,
                    ]
                  : [],
              )}
            </li>
          )
        })}
      </ul>
      {asking && (
        <ConfirmDialog
          title={t('Make a new webhook secret?')}
          confirmLabel={t('Make a new secret')}
          busy={state.busy}
          error={state.error}
          onConfirm={() => rotate()}
          onClose={() => {
            setAsking(false)
            setState({})
          }}
        >
          <p>{t('Sonarr, Radarr and Lidarr stop starting scans until you give them the new addresses. The secret only lets them start scans.')}</p>
        </ConfirmDialog>
      )}
    </SettingsGroup>
  )
}

/** How a Test went, on its button: a green tick, or a red cross. */
function TestMark({ ok }: { ok: boolean }) {
  return ok ? <Check className="test-ok" /> : <Close className="test-bad" />
}
