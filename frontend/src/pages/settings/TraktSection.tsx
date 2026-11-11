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
import { useConfirm } from '../../hooks/useConfirm'
import { relativeTime } from '../../lib/format'
import { webLink } from '../../lib/links'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { t, tk, Words } from '../../i18n'

/** Why a connect ended without connecting. */
const ended: Record<Exclude<TraktPendingConnect['state'], 'Waiting' | 'Connected'>, string> = {
  Expired: tk('The code ran out. Try again.'),
  Denied: tk('Trakt said no.'),
  InUse: tk('That Trakt account is connected to another profile.'),
  Failed: tk('That didn’t work. Try again.'),
}

/** The server's own words for what went wrong (its problem's detail), or that it couldn't be reached. */
const problemText = (e: unknown, fallback?: string) => {
  if (e instanceof ApiError && typeof e.problem?.detail === 'string' && e.problem.detail) return e.problem.detail
  if (e instanceof TypeError) return t('Couldn’t reach the server.')
  return fallback ?? t('That didn’t work. Try again.')
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
      <SettingsHeader title={t('Trakt')}>{t('Keep what you watch, your place and your watchlist in step with Trakt.')}</SettingsHeader>
      {s === undefined && status.loading && <p className="muted">{t('Loading…')}</p>}
      {s === undefined && !status.loading && status.error && (
        <p className="error settings-status" role="alert">
          {t('Couldn’t reach the server.')}{' '}
          <button type="button" className="text-button" onClick={reload}>
            {t('Try again')}
          </button>
        </p>
      )}
      {(s === null || s?.available === false) && <p className="muted">{t('Trakt isn’t available on this server.')}</p>}
      {s?.available && (s.state === 'connected' ? <Connected status={s} reload={reload} /> : <Connect status={s} reload={reload} />)}
      {s?.available && s.state === 'connected' && <History status={s} reload={reload} />}
      {s?.available && s.state === 'connected' && <Lists />}
      {s?.available && me?.isAdmin && <Profiles me={me.profileId} />}
      {s?.available && <p className="muted field-hint">{t('Flexarr uses Trakt but is not endorsed or certified by Trakt.')}</p>}
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
      <SettingsGroup title={t('Connect')} note={error ? <SaveStatus error={error} /> : t('Flexarr waits, so you can leave this.')}>
        <SettingRow
          as="div"
          label={
            <span className="trakt-code" aria-label={pending.userCode.split('').join(' ')}>
              {pending.userCode}
            </span>
          }
          help={
            <>
              {t.rich('Enter this code at {link}.', {
                link: (
                  <a href={webLink(pending.verificationUrl)} target="_blank" rel="noreferrer">
                    {where}
                  </a>
                ),
              })}
            </>
          }
        >
          <span className="library-row-actions">
            <CopyButton text={pending.userCode} label={t('Copy code')} />
            <AsyncButton className="button quiet small" onClick={() => act(cancelTraktConnect)}>
              {t(Words.cancel)}
            </AsyncButton>
          </span>
        </SettingRow>
      </SettingsGroup>
    )
  }

  const problem = status.state === 'needsSignIn' ? t('Trakt signed Flexarr out.') : pending && pending.state !== 'Connected' ? (pending.error ?? t(ended[pending.state])) : null
  return (
    <SettingsGroup title={t('Account')} note={error ? <SaveStatus error={error} /> : undefined}>
      <SettingRow as="div" label={status.state === 'needsSignIn' ? t('Connect {name} again', { name: status.username ?? 'Trakt' }) : t('Connect Trakt')} help={problem ?? t('Each profile connects its own account.')}>
        <AsyncButton className="button" onClick={() => act(connectTrakt)}>
          {t('Connect')}
        </AsyncButton>
      </SettingRow>
    </SettingsGroup>
  )
}

function Connected({ status, reload }: { status: TraktStatus; reload: () => void }) {
  const { error, act } = useAction(reload)
  const [ask, confirmDialog] = useConfirm()
  const synced = status.lastSyncAt ? t('Synced {time}', { time: relativeTime(status.lastSyncAt) }) : t('Not synced yet')
  const queued = status.queued > 0 ? `\u2003${t('{count, number} waiting to send', { count: status.queued })}` : ''
  return (
    <SettingsGroup title={t('Account')} note={error ? <SaveStatus error={error} /> : undefined}>
      <SettingRow as="div" label={status.username ?? t('Connected')} help={status.lastError ?? synced + queued}>
        <span className="library-row-actions">
          <AsyncButton className="button quiet" onClick={() => act(syncTraktNow)}>
            {t('Sync now')}
          </AsyncButton>
          <AsyncButton
            className="button danger"
            onClick={async () =>
              (await ask({ title: t('Disconnect Trakt? What you’ve watched stays as it is, here and on Trakt.'), confirmLabel: t('Disconnect'), danger: true })) ? act(disconnectTrakt) : undefined
            }
          >
            {t('Disconnect')}
          </AsyncButton>
        </span>
      </SettingRow>
      {confirmDialog}
      {status.watchlistFull && <SettingRow as="div" label={t('Trakt’s watchlist is full')} help={t('New titles stay on Flexarr’s list.')} />}
    </SettingsGroup>
  )
}

/** The first import: what it would do, then doing it, then that it's done. */
function History({ status, reload }: { status: TraktStatus; reload: () => void }) {
  const { error, act } = useAction(reload)
  const [preview, setPreview] = useState<TraktImportPreview | null>(null)
  const failed = error ?? (!status.importing && !status.importedAt ? status.importError : null)
  const note = failed ? <SaveStatus error={failed} /> : preview && !status.importedAt && !status.importing ? t('Nothing is marked unwatched, on either side.') : undefined

  let row
  if (status.importing) row = <SettingRow as="div" label={t('Bringing your history across…')} />
  else if (status.importedAt) row = <SettingRow as="div" label={t('History brought across')} help={relativeTime(status.importedAt)} />
  else if (preview)
    row = (
      <SettingRow as="div" label={summary(preview)} help={details(preview)}>
        <AsyncButton className="button" onClick={() => act(confirmTraktImport)}>
          {t('Bring across')}
        </AsyncButton>
      </SettingRow>
    )
  else
    row = (
      <SettingRow as="div" label={t('Bring your Trakt history across')} help={t('See what changes first.')}>
        <AsyncButton className="button" onClick={() => act(async () => setPreview(await previewTraktImport()))}>
          {t('See what changes')}
        </AsyncButton>
      </SettingRow>
    )

  return (
    <SettingsGroup title={t('Your history')} note={note}>
      {row}
    </SettingsGroup>
  )
}

function summary(p: TraktImportPreview) {
  return t('Trakt has {films, plural, one {# movie} other {# movies}} and {episodes, plural, one {# episode} other {# episodes}}', { films: p.traktFilms, episodes: p.traktEpisodes })
}

const titles = (count: number) => t('{count, plural, one {# title} other {# titles}}', { count })

function details(p: TraktImportPreview) {
  const parts = [
    p.inLibrary === 0
      ? t('None are here yet')
      : p.newToFlexarr > 0
        ? t('{count, number} are here, {fresh, number} not yet watched in Flexarr', { count: p.inLibrary, fresh: p.newToFlexarr })
        : t('{count, number} are here', { count: p.inLibrary }),
  ]
  if (p.notOnServer > 0) parts.push(t('{count, number} will show as watched if they arrive', { count: p.notOnServer }))
  if (p.toTrakt > 0) parts.push(t('{count, plural, one {# play from Flexarr goes to Trakt} other {# plays from Flexarr go to Trakt}}', { count: p.toTrakt }))
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
      <SettingsGroup title={t('Lists')}>
        <SettingRow as="div" label={t('Couldn’t load your Trakt lists')} help={problemText(lists.error, t('Try again in a moment.'))} />
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
    <SettingsGroup title={t('Lists')} note={error ? <SaveStatus error={error} /> : t('Chosen lists show in Collections, just for you.')}>
      {lists.data.map((l) => (
        <ToggleRow
          key={l.id}
          label={l.name}
          help={l.owner ? `${l.owner}\u2003${titles(l.itemCount)}` : titles(l.itemCount)}
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
    <SettingsGroup title={t('Other profiles')}>
      {others.map((p) => (
        <SettingRow
          key={p.profileId}
          as="div"
          label={p.profileName}
          help={p.state === 'needsSignIn' ? t('Signed out') : (p.lastError ?? (p.lastSyncAt ? `${p.username}\u2003${t('synced {time}', { time: relativeTime(p.lastSyncAt) })}` : `${p.username}\u2003${t('not synced yet')}`))}
        />
      ))}
    </SettingsGroup>
  )
}
