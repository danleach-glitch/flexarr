import { useState } from 'react'
import {
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
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { relativeTime } from '../../lib/format'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'

const ended: Record<Exclude<TraktPendingConnect['state'], 'Waiting' | 'Connected'>, string> = {
  Expired: 'The code ran out. Try again.',
  Denied: 'Trakt said no.',
  InUse: 'That Trakt account is connected to another profile.',
  Failed: 'That didn’t work. Try again.',
}

/** This profile's Trakt account: connect by code, how the sync is going, the first import, and the admin's overview. */
export function TraktSection() {
  const { me } = useSession()
  const [version, setVersion] = useState(0)
  const status = useApi((signal) => getTraktStatus(signal), [version])
  const [error, setError] = useState<string | null>(null)
  const reload = () => setVersion((v) => v + 1)
  const s = status.data

  // While a code is waiting, or the import's running, keep looking.
  const waiting = s?.pending?.state === 'Waiting' || s?.importing === true
  usePolling(reload, waiting ? 2000 : null)

  async function act(work: () => Promise<unknown>, failed: string) {
    setError(null)
    try {
      await work()
    } catch (e) {
      setError(e instanceof Error && e.message ? e.message : failed)
    } finally {
      reload()
    }
  }

  return (
    <section>
      <SettingsHeader title="Trakt">Keep what you watch, your place and your watchlist in step with Trakt.</SettingsHeader>
      {s && !s.available && <p className="muted">Trakt isn’t available on this server.</p>}
      {s?.available && (s.state === 'connected' ? <Connected status={s} act={act} /> : <Connect status={s} act={act} />)}
      {s?.available && s.state === 'connected' && !s.importedAt && <Import importing={s.importing} act={act} />}
      {s?.available && s.state === 'connected' && <Lists />}
      {s?.available && me?.isAdmin && <Profiles version={version} />}
      {error && <SaveStatus error={error} />}
    </section>
  )
}

type Act = (work: () => Promise<unknown>, failed: string) => Promise<void>

function Connect({ status, act }: { status: TraktStatus; act: Act }) {
  const pending = status.pending
  if (pending?.state === 'Waiting')
    return (
      <SettingsGroup title="Connect" note="Flexarr waits for you, so you can close this.">
        <SettingRow as="div" label={<span className="trakt-code">{pending.userCode}</span>} help={<>Enter this code at <a href={pending.verificationUrl} target="_blank" rel="noreferrer">{pending.verificationUrl.replace(/^https?:\/\//, '')}</a></>}>
          <AsyncButton className="button quiet" onClick={() => act(cancelTraktConnect, 'That didn’t cancel.')}>
            Cancel
          </AsyncButton>
        </SettingRow>
      </SettingsGroup>
    )

  const problem = status.state === 'needsSignIn' ? 'Trakt signed Flexarr out.' : pending && pending.state !== 'Connected' ? ended[pending.state] : null
  return (
    <SettingsGroup title="Account">
      <SettingRow as="div" label={status.state === 'needsSignIn' ? `Connect ${status.username ?? 'Trakt'} again` : 'Connect Trakt'} help={problem ?? 'Each profile connects its own account.'}>
        <AsyncButton className="button" onClick={() => act(connectTrakt, 'Couldn’t reach Trakt. Try again.')}>
          Connect
        </AsyncButton>
      </SettingRow>
    </SettingsGroup>
  )
}

function Connected({ status, act }: { status: TraktStatus; act: Act }) {
  const synced = status.lastSyncAt ? `Synced ${relativeTime(status.lastSyncAt)}` : 'Not synced yet'
  const queued = status.queued > 0 ? ` · ${status.queued} waiting to send` : ''
  return (
    <SettingsGroup title="Account">
      <SettingRow as="div" label={status.username ?? 'Connected'} help={status.lastError ?? synced + queued}>
        <span className="library-row-actions">
          <AsyncButton className="button quiet" onClick={() => act(syncTraktNow, 'Couldn’t sync.')}>
            Sync now
          </AsyncButton>
          <AsyncButton className="button quiet" onClick={() => act(disconnectTrakt, 'That didn’t disconnect.')}>
            Disconnect
          </AsyncButton>
        </span>
      </SettingRow>
      {status.watchlistFull && (
        <SettingRow as="div" label="Trakt’s watchlist is full" help="A free account holds a limited number. New titles stay on Flexarr’s list." />
      )}
    </SettingsGroup>
  )
}

function Import({ importing, act }: { importing: boolean; act: Act }) {
  const [preview, setPreview] = useState<TraktImportPreview | null>(null)
  if (importing)
    return (
      <SettingsGroup title="Your history">
        <SettingRow as="div" label="Bringing your history across…" />
      </SettingsGroup>
    )

  return (
    <SettingsGroup title="Your history" note={preview ? 'Nothing is marked unwatched, on either side.' : undefined}>
      {!preview ? (
        <SettingRow as="div" label="Bring your Trakt history across" help="See what changes first.">
          <AsyncButton className="button" onClick={() => act(async () => setPreview(await previewTraktImport()), 'Couldn’t reach Trakt. Try again.')}>
            Look
          </AsyncButton>
        </SettingRow>
      ) : (
        <SettingRow as="div" label={summary(preview)} help={details(preview)}>
          <AsyncButton className="button" onClick={() => act(confirmTraktImport, 'That didn’t start. Try again.')}>
            Bring across
          </AsyncButton>
        </SettingRow>
      )}
    </SettingsGroup>
  )
}

const n = (count: number, one: string, many = `${one}s`) => `${count.toLocaleString()} ${count === 1 ? one : many}`

function summary(p: TraktImportPreview) {
  return `Trakt has ${n(p.traktFilms, 'film')} and ${n(p.traktEpisodes, 'episode')}`
}

function details(p: TraktImportPreview) {
  const parts = [`${p.inLibrary.toLocaleString()} are here, ${p.newToFlexarr.toLocaleString()} not yet watched in Flexarr`]
  if (p.notOnServer > 0) parts.push(`${p.notOnServer.toLocaleString()} will show as watched if they arrive`)
  if (p.toTrakt > 0) parts.push(`${n(p.toTrakt, 'play')} from Flexarr ${p.toTrakt === 1 ? 'goes' : 'go'} to Trakt`)
  return parts.join('. ') + '.'
}

/** Trakt lists (this profile's own, and ones it liked) to show as its collections, kept in step. */
function Lists() {
  const lists = useApi((signal) => getTraktLists(signal).catch(() => null), [])
  const [chosen, setChosen] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  if (!lists.data || lists.data.length === 0) return null
  const selected = chosen ?? lists.data.filter((l) => l.selected).map((l) => l.id)

  async function toggle(id: string, on: boolean) {
    const next = on ? [...selected, id] : selected.filter((x) => x !== id)
    setChosen(next)
    setError(null)
    try {
      await saveTraktLists(next)
    } catch {
      setChosen(null)
      setError('That didn’t save. Try again.')
    }
  }

  return (
    <SettingsGroup title="Lists" note={error ? <SaveStatus error={error} /> : 'Chosen lists show in Collections, just for you.'}>
      {lists.data.map((l) => (
        <ToggleRow
          key={l.id}
          label={l.name}
          help={l.owner ? `${l.owner} · ${n(l.itemCount, 'title')}` : n(l.itemCount, 'title')}
          checked={selected.includes(l.id)}
          onChange={(on) => void toggle(l.id, on)}
        />
      ))}
    </SettingsGroup>
  )
}

/** The admin's view: which profiles are connected, and any trouble. */
function Profiles({ version }: { version: number }) {
  const profiles = useApi((signal) => getTraktProfiles(signal).catch(() => null), [version])
  const connected = profiles.data?.filter((p) => p.state !== 'disconnected') ?? []
  if (connected.length === 0) return null
  return (
    <SettingsGroup title="Profiles">
      {connected.map((p) => (
        <SettingRow key={p.profileId} as="div" label={p.profileName} help={p.state === 'needsSignIn' ? 'Needs to connect again' : (p.lastError ?? `${p.username} · synced ${relativeTime(p.lastSyncAt ?? null)}`)} />
      ))}
    </SettingsGroup>
  )
}
