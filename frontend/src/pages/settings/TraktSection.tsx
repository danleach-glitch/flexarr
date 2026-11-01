import { useState } from 'react'
import {
  ApiError,
  cancelTraktConnect,
  confirmTraktImport,
  connectTrakt,
  disconnectTrakt,
  getTraktLists,
  getTraktProfiles,
  getTraktStatus,
  previewTraktImport,
  saveTraktLists,
  syncTraktNow,
  type TraktImportPreview,
  type TraktPendingConnect,
  type TraktStatus,
} from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { CopyButton } from '../../components/CopyButton'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { relativeTime } from '../../lib/format'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'

/** Why a connect ended without connecting. */
const ended: Record<Exclude<TraktPendingConnect['state'], 'Waiting' | 'Connected'>, string> = {
  Expired: 'The code ran out. Try again.',
  Denied: 'Trakt said no.',
  InUse: 'That Trakt account is connected to another profile.',
  Failed: 'That didn’t work. Try again.',
}

/** The server's own words for what went wrong (its problem's detail), or that it couldn't be reached. */
const problemText = (e: unknown, fallback = 'That didn’t work. Try again.') => {
  if (e instanceof ApiError && typeof e.problem?.detail === 'string' && e.problem.detail) return e.problem.detail
  if (e instanceof TypeError) return 'Couldn’t reach the server.'
  return fallback
}

/** This profile's Trakt account: connect by code, how the sync is going, the first import, lists, and the admin's overview. */
export function TraktSection() {
  const { me } = useSession()
  const [version, setVersion] = useState(0)
  const status = useApi((signal) => getTraktStatus(signal), [version])
  const reload = () => setVersion((v) => v + 1)
  const s = status.data

  // While a code is waiting, or the import's running, keep looking.
  const waiting = s?.pending?.state === 'Waiting' || s?.importing === true
  usePolling(reload, waiting ? 2000 : null)

  return (
    <section>
      <SettingsHeader title="Trakt">Keep what you watch, your place and your watchlist in step with Trakt.</SettingsHeader>
      {s === undefined && status.loading && <p className="muted">Loading…</p>}
      {s === undefined && !status.loading && status.error && (
        <p className="error settings-status" role="alert">
          Couldn’t reach the server.{' '}
          <button type="button" className="text-button" onClick={reload}>
            Try again
          </button>
        </p>
      )}
      {(s === null || s?.available === false) && <p className="muted">Trakt isn’t available on this server.</p>}
      {s?.available && (s.state === 'connected' ? <Connected status={s} reload={reload} /> : <Connect status={s} reload={reload} />)}
      {s?.available && s.state === 'connected' && <History status={s} reload={reload} />}
      {s?.available && s.state === 'connected' && <Lists />}
      {s?.available && me?.isAdmin && <Profiles me={me.profileId} />}
    </section>
  )
}

/** Runs an action, keeping its error beside the part of the page it belongs to. */
function useAction(reload: () => void) {
  const [error, setError] = useState<string | null>(null)
  async function act(work: () => Promise<unknown>) {
    setError(null)
    try {
      await work()
    } catch (e) {
      setError(problemText(e))
    } finally {
      reload()
    }
  }
  return { error, act }
}

function Connect({ status, reload }: { status: TraktStatus; reload: () => void }) {
  const { error, act } = useAction(reload)
  const pending = status.pending
  if (pending?.state === 'Waiting') {
    const where = pending.verificationUrl.replace(/^https?:\/\//, '')
    return (
      <SettingsGroup title="Connect" note={error ? <SaveStatus error={error} /> : 'Flexarr waits, so you can leave this.'}>
        <SettingRow
          as="div"
          label={
            <span className="trakt-code" aria-label={pending.userCode.split('').join(' ')}>
              {pending.userCode}
            </span>
          }
          help={
            <>
              Enter this code at{' '}
              <a href={pending.verificationUrl} target="_blank" rel="noreferrer">
                {where}
              </a>
              .
            </>
          }
        >
          <span className="library-row-actions">
            <CopyButton text={pending.userCode} label="Copy code" />
            <AsyncButton className="button quiet small" onClick={() => act(cancelTraktConnect)}>
              Cancel
            </AsyncButton>
          </span>
        </SettingRow>
      </SettingsGroup>
    )
  }

  const problem = status.state === 'needsSignIn' ? 'Trakt signed Flexarr out.' : pending && pending.state !== 'Connected' ? (pending.error ?? ended[pending.state]) : null
  return (
    <SettingsGroup title="Account" note={error ? <SaveStatus error={error} /> : undefined}>
      <SettingRow as="div" label={status.state === 'needsSignIn' ? `Connect ${status.username ?? 'Trakt'} again` : 'Connect Trakt'} help={problem ?? 'Each profile connects its own account.'}>
        <AsyncButton className="button" onClick={() => act(connectTrakt)}>
          Connect
        </AsyncButton>
      </SettingRow>
    </SettingsGroup>
  )
}

function Connected({ status, reload }: { status: TraktStatus; reload: () => void }) {
  const { error, act } = useAction(reload)
  const synced = status.lastSyncAt ? `Synced ${relativeTime(status.lastSyncAt)}` : 'Not synced yet'
  const queued = status.queued > 0 ? `\u2003${status.queued} waiting to send` : ''
  return (
    <SettingsGroup title="Account" note={error ? <SaveStatus error={error} /> : undefined}>
      <SettingRow as="div" label={status.username ?? 'Connected'} help={status.lastError ?? synced + queued}>
        <span className="library-row-actions">
          <AsyncButton className="button quiet" onClick={() => act(syncTraktNow)}>
            Sync now
          </AsyncButton>
          <AsyncButton
            className="button danger"
            onClick={() => (confirm('Disconnect Trakt? What you’ve watched stays as it is, here and on Trakt.') ? act(disconnectTrakt) : undefined)}
          >
            Disconnect
          </AsyncButton>
        </span>
      </SettingRow>
      {status.watchlistFull && <SettingRow as="div" label="Trakt’s watchlist is full" help="New titles stay on Flexarr’s list." />}
    </SettingsGroup>
  )
}

/** The first import: what it would do, then doing it, then that it's done. */
function History({ status, reload }: { status: TraktStatus; reload: () => void }) {
  const { error, act } = useAction(reload)
  const [preview, setPreview] = useState<TraktImportPreview | null>(null)
  const failed = error ?? (!status.importing && !status.importedAt ? status.importError : null)
  const note = failed ? <SaveStatus error={failed} /> : preview && !status.importedAt && !status.importing ? 'Nothing is marked unwatched, on either side.' : undefined

  let row
  if (status.importing) row = <SettingRow as="div" label="Bringing your history across…" />
  else if (status.importedAt) row = <SettingRow as="div" label="History brought across" help={relativeTime(status.importedAt)} />
  else if (preview)
    row = (
      <SettingRow as="div" label={summary(preview)} help={details(preview)}>
        <AsyncButton className="button" onClick={() => act(confirmTraktImport)}>
          Bring across
        </AsyncButton>
      </SettingRow>
    )
  else
    row = (
      <SettingRow as="div" label="Bring your Trakt history across" help="See what changes first.">
        <AsyncButton className="button" onClick={() => act(async () => setPreview(await previewTraktImport()))}>
          See what changes
        </AsyncButton>
      </SettingRow>
    )

  return (
    <SettingsGroup title="Your history" note={note}>
      {row}
    </SettingsGroup>
  )
}

const n = (count: number, one: string, many = `${one}s`) => `${count.toLocaleString()} ${count === 1 ? one : many}`

function summary(p: TraktImportPreview) {
  return `Trakt has ${n(p.traktFilms, 'movie')} and ${n(p.traktEpisodes, 'episode')}`
}

function details(p: TraktImportPreview) {
  const parts = [
    p.inLibrary === 0 ? 'None are here yet' : `${p.inLibrary.toLocaleString()} are here${p.newToFlexarr > 0 ? `, ${p.newToFlexarr.toLocaleString()} not yet watched in Flexarr` : ''}`,
  ]
  if (p.notOnServer > 0) parts.push(`${p.notOnServer.toLocaleString()} will show as watched if they arrive`)
  if (p.toTrakt > 0) parts.push(`${n(p.toTrakt, 'play')} from Flexarr ${p.toTrakt === 1 ? 'goes' : 'go'} to Trakt`)
  return parts.join('. ') + '.'
}

/** Trakt lists (this profile's own, and ones it liked) to show as its collections, kept in step. */
function Lists() {
  const lists = useApi((signal) => getTraktLists(signal), [])
  const [chosen, setChosen] = useState<string[] | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  if (lists.error)
    return (
      <SettingsGroup title="Lists">
        <SettingRow as="div" label="Couldn’t load your Trakt lists" help={problemText(lists.error, 'Try again in a moment.')} />
      </SettingsGroup>
    )
  if (!lists.data || lists.data.length === 0) return null
  const selected = chosen ?? lists.data.filter((l) => l.selected).map((l) => l.id)

  async function toggle(id: string, on: boolean) {
    const next = on ? [...selected, id] : selected.filter((x) => x !== id)
    setChosen(next)
    setError(null)
    setSaving(true)
    try {
      await saveTraktLists(next)
    } catch (e) {
      setChosen(null)
      setError(problemText(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <SettingsGroup title="Lists" note={error ? <SaveStatus error={error} /> : 'Chosen lists show in Collections, just for you.'}>
      {lists.data.map((l) => (
        <ToggleRow
          key={l.id}
          label={l.name}
          help={l.owner ? `${l.owner}\u2003${n(l.itemCount, 'title')}` : n(l.itemCount, 'title')}
          checked={selected.includes(l.id)}
          disabled={saving}
          onChange={(on) => void toggle(l.id, on)}
        />
      ))}
    </SettingsGroup>
  )
}

/** The admin's view: who else is connected, and any trouble. Loaded once, not with the page's polling. */
function Profiles({ me }: { me: string }) {
  const profiles = useApi((signal) => getTraktProfiles(signal).catch(() => null), [])
  const others = profiles.data?.filter((p) => p.state !== 'disconnected' && p.profileId !== me) ?? []
  if (others.length === 0) return null
  return (
    <SettingsGroup title="Other profiles">
      {others.map((p) => (
        <SettingRow
          key={p.profileId}
          as="div"
          label={p.profileName}
          help={p.state === 'needsSignIn' ? 'Signed out' : (p.lastError ?? (p.lastSyncAt ? `${p.username}\u2003synced ${relativeTime(p.lastSyncAt)}` : `${p.username}\u2003not synced yet`))}
        />
      ))}
    </SettingsGroup>
  )
}
