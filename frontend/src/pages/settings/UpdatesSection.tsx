import { useEffect, useState } from 'react'
import { checkForUpdates, getUpdates, installUpdate, saveUpdateSettings, type Updates } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { External } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { formatSize, relativeTime } from '../../lib/format'
import { parse, ReleaseNotes } from './changelog'
import { SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'
import { webLink } from '../../lib/links'
import { locale, t, Words } from '../../i18n'

const guide = 'https://flexarr.io/guide/updating'

function day(iso: string) {
  return new Date(iso).toLocaleDateString(locale(), { day: 'numeric', month: 'long', year: 'numeric' })
}

/** Which Flexarr this is, whether there's a newer one, and installing it (by hand, or overnight by itself). */
export function UpdatesSection() {
  const loaded = useApi(getUpdates, [])
  const [updates, setUpdates] = useState<Updates>()
  const [error, setError] = useState<string>()
  const [confirming, setConfirming] = useState(false)
  const [installError, setInstallError] = useState<string>()
  const current = updates ?? loaded.data

  // While a download runs, follow it; once the server's restarting, the page waits for it instead.
  const downloading = current?.progress.stage === 'Downloading'
  useEffect(() => {
    if (!downloading) return
    const timer = setInterval(() => {
      getUpdates().then(setUpdates, () => undefined)
    }, 1000)
    return () => clearInterval(timer)
  }, [downloading])

  if (current?.progress.stage === 'Installing') return <Restarting to={current.progress.version ?? current.latest?.version ?? ''} />

  async function check() {
    setError(undefined)
    try {
      setUpdates(await checkForUpdates())
    } catch (e) {
      setError(errorText(e))
    }
  }

  async function install() {
    setInstallError(undefined)
    try {
      setUpdates(await installUpdate())
      setConfirming(false)
    } catch (e) {
      setInstallError(errorText(e))
    }
  }

  return (
    <section>
      <SettingsHeader title={t('Updates')}>
        {t('The version this server runs, and new releases. Updating keeps your settings, profiles and history.')}
      </SettingsHeader>

      <SettingsGroup
        title={t('This server')}
        note={
          current?.install === 'Docker'
            ? t('Docker updates Flexarr from outside, so it tells you about new versions but can’t install them.')
            : current?.install === 'MacApp'
              ? t('The Mac App Store installs new versions of Flexarr Server.')
              : undefined
        }
        action={
          <AsyncButton className="button small" onClick={check}>
            {t('Check for updates')}
          </AsyncButton>
        }
      >
        {loaded.error && !current && (
          <p className="error" role="alert">
            {t('Couldn’t load updates: {message}', { message: loaded.error.message })}
          </p>
        )}
        {!current && !loaded.error && <p className="muted">{t('Loading…')}</p>}
        {current && (
          <>
            <SettingRow as="div" label={t(Words.version)} help={current.isDevelopment ? t('A development build, between releases.') : undefined}>
              <span className="setting-value">{current.version}</span>
            </SettingRow>
            <SettingRow as="div" label={t('Status')} help={checkedLine(current)}>
              <span className="setting-value">{statusLine(current)}</span>
            </SettingRow>
          </>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        {current?.checkError && (
          <p className="error" role="alert">
            {current.checkError}
          </p>
        )}
        {current?.progress.error && (
          <p className="error" role="alert">
            {current.progress.error}
          </p>
        )}
        {current?.lastResult && <LastResult result={current.lastResult} />}
      </SettingsGroup>

      {current?.progress.stage === 'Downloading' && <Downloading updates={current} />}

      {current?.available && current.latest && current.progress.stage === 'Idle' && (
        <div className="settings-group">
          <div className="settings-group-head">
            <h3 className="settings-subhead">Flexarr {current.latest.version}</h3>
            {current.canInstall && (
              <button className="button small" onClick={() => setConfirming(true)}>
                {t('Update now')}
              </button>
            )}
          </div>
          {!current.canInstall && <HowToUpdate updates={current} />}
          <div className="changelog">
            {parse(`## ${current.latest.version}${current.latest.publishedAt ? ` · ${day(current.latest.publishedAt)}` : ''}\n${current.latest.notes}`).map((r) => (
              <ReleaseNotes key={r.title} release={r} />
            ))}
          </div>
          <p className="settings-note">
            <a href={webLink(current.latest.url)} target="_blank" rel="noreferrer">
              {t('This release on GitHub')} <External />
            </a>
          </p>
        </div>
      )}

      {current?.install === 'MacLaunchAgent' && !current.isDevelopment && <Automatic updates={current} onSaved={setUpdates} />}

      {confirming && current?.latest && (
        <ConfirmDialog title={t('Update to Flexarr {version}?', { version: current.latest.version })} confirmLabel={current.inUse.length ? t('Update anyway') : t('Update now')} error={installError}
          onConfirm={install}
          onClose={() => {
            setConfirming(false)
            setInstallError(undefined)
          }}
        >
          <p>{t('Flexarr downloads the update, then restarts to install it, which takes a minute or two. If the new version doesn’t start, the one you have now is put back.')}</p>
          {current.inUse.length > 0 && (
            <>
              <p>{t('Restarting interrupts what’s going on now:')}</p>
              <ul>
                {current.inUse.map((reason) => (
                  <li key={reason}>{reason}</li>
                ))}
              </ul>
            </>
          )}
        </ConfirmDialog>
      )}
    </section>
  )
}

function statusLine(updates: Updates) {
  if (updates.isDevelopment) return updates.latest ? t('Latest release: {version}', { version: updates.latest.version }) : t('Updated by deploying')
  if (updates.available && updates.latest) return t('Flexarr {version} is available', { version: updates.latest.version })
  if (updates.latest) return t('Up to date')
  return updates.lastChecked ? t('Unknown') : t('Not checked yet')
}

function checkedLine(updates: Updates) {
  if (updates.isDevelopment) return t('Development builds aren’t updated from here: a release could be older than this.')
  return `${updates.lastChecked ? `${t('Checked {time}.', { time: relativeTime(updates.lastChecked) })} ` : ''}${t('Flexarr checks once a day.')}`
}

/** How the last update went, for a week after. */
function LastResult({ result }: { result: NonNullable<Updates['lastResult']> }) {
  const [now] = useState(() => Date.now())
  if (now - new Date(result.at).getTime() > 7 * 24 * 3600 * 1000) return null
  return result.ok ? (
    <p className="muted update-result" role="status">
      {t('Updated from {from} to {to} {time}.', { from: result.from, to: result.to, time: relativeTime(result.at) })}
    </p>
  ) : (
    <p className="error update-result" role="alert">
      {result.message}
    </p>
  )
}

function Downloading({ updates }: { updates: Updates }) {
  const { received, total, version, downloading } = updates.progress
  const share = total ? Math.min(1, received / total) : undefined
  return (
    <SettingsGroup title={t('Downloading {what}', { what: downloading ?? `Flexarr ${version ?? ''}` })}>
      <div className="update-progress" role="status">
        <progress max={1} value={share} aria-label={t('Download progress')} />
        <span className="muted">
          {total
            ? t('{received} of {total}. Flexarr restarts to install it once it’s downloaded.', { received: formatSize(received), total: formatSize(total) })
            : t('{received}. Flexarr restarts to install it once it’s downloaded.', { received: formatSize(received) })}
        </span>
      </div>
    </SettingsGroup>
  )
}

/** For a server that can't update itself: what to do instead, for the way it was installed. */
function HowToUpdate({ updates }: { updates: Updates }) {
  if (updates.install === 'Docker')
    return (
      <div className="panel update-howto">
        <p>
          {t.rich('Flexarr runs in Docker here, and a container can’t replace itself. To update, get the new image and recreate the container. With Docker Compose, in the folder with your {file}:', {
            file: <code>docker-compose.yml</code>,
          })}
        </p>
        <pre className="update-command">docker compose pull flexarr{'\n'}docker compose up -d flexarr</pre>
        <p className="muted">
          {t.rich('If your image’s tag names a version (like {tag}), change it first. On Synology or Unraid, update the container the way the platform does. To update automatically, a tool like Watchtower can do it.', {
            tag: <code>:0.50</code>,
          })}{' '}
          <a href={`${guide}#docker`} target="_blank" rel="noreferrer">
            {t('More in the guide')}
          </a>
        </p>
      </div>
    )
  if (updates.install === 'MacApp')
    return (
      <div className="panel update-howto">
        <p>
          {t.rich('To update, open the App Store on the Mac running Flexarr Server and choose {updates}. A new version can take a day or two to reach the App Store after it’s released.', {
            updates: <strong>{t('Updates')}</strong>,
          })}
        </p>
      </div>
    )
  if (updates.install === 'MacLaunchAgent')
    return (
      <div className="panel update-howto">
        <p>{t('This release has no Mac build to install, so update with the install script, from the Mac you installed from:')}</p>
        <pre className="update-command">cd flexarr{'\n'}git pull{'\n'}make deploy DEPLOY_HOST=&lt;your-mac&gt;</pre>
      </div>
    )
  return (
    <div className="panel update-howto">
      <p>
        {t('This server wasn’t installed in a way Flexarr can update by itself. Update it the way it was installed.')}{' '}
        <a href={guide} target="_blank" rel="noreferrer">
          {t('How to update')}
        </a>
      </p>
    </div>
  )
}

/** Installing new releases by themselves, where Flexarr can update itself. Checking for them is always on. */
function Automatic({ updates, onSaved }: { updates: Updates; onSaved: (updates: Updates) => void }) {
  const [error, setError] = useState<string>()
  const [saved, setSaved] = useState<string>()

  async function save(update: { automatic: boolean }) {
    setError(undefined)
    try {
      onSaved(await saveUpdateSettings(update))
      setSaved(t('Saved'))
      setTimeout(() => setSaved(undefined), 3000)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <SettingsGroup
      title={t('Automatic updates')}
      note={
        error ? (
          <span className="error" role="alert">
            {error}
          </span>
        ) : (
          saved
        )
      }
    >
      <ToggleRow
        label={t('Install updates automatically')}
        help={t('Between 4 and 5 in the morning, once nothing is playing or recording. A busy night waits for the next.')}
        checked={updates.automatic}
        onChange={(on) => void save({ automatic: on })}
      />
    </SettingsGroup>
  )
}

/** Waits for the server to go away and come back on the new version, then reloads the page. */
function Restarting({ to }: { to: string }) {
  const [late, setLate] = useState(false)

  useEffect(() => {
    let stopped = false
    void (async () => {
      const start = Date.now()
      let wentDown = false
      // A first start after an update can be migrating the database: give it a good while.
      while (!stopped && Date.now() - start < 360_000) {
        await new Promise((done) => setTimeout(done, 2000))
        try {
          const response = await fetch('/health', { cache: 'no-store' })
          if (!response.ok) wentDown = true
          else if (wentDown) {
            window.location.reload()
            return
          }
        } catch {
          wentDown = true
        }
        if (Date.now() - start > 120_000) setLate(true)
      }
      setLate(true)
    })()
    return () => {
      stopped = true
    }
  }, [])

  return (
    <section>
      <SettingsHeader title={t('Updating')}>
        {to
          ? t('Flexarr is restarting to install version {version}. This page reloads when it’s back.', { version: to })
          : t('Flexarr is restarting to install the update. This page reloads when it’s back.')}
      </SettingsHeader>
      {late && (
        <p className="muted" role="status">
          {t.rich('It’s taking a while, which a big library’s first start after an update can. If it doesn’t come back, the log of the update is in {file} on the server.', {
            file: <code>~/.flexarr/logs/update.log</code>,
          })}
        </p>
      )}
    </section>
  )
}
