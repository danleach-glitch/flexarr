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

const guide = 'https://flexarr.io/guide/updating'

function day(iso: string) {
  return new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
}

/** Which Flexarr this is, whether there's a newer one, and installing it (by hand, or overnight by itself). */
export function UpdatesSection() {
  const loaded = useApi(getUpdates, [])
  const [updates, setUpdates] = useState<Updates>()
  const [error, setError] = useState<string>()
  const [confirming, setConfirming] = useState(false)
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
    setUpdates(await installUpdate())
    setConfirming(false)
  }

  return (
    <section>
      <SettingsHeader title="Updates">
        The version of Flexarr this server runs, and newer releases as they come out. Updating keeps your settings, profiles and watch history.
      </SettingsHeader>

      <SettingsGroup
        title="This server"
        note={
          current?.install === 'Docker'
            ? 'Docker containers are updated from outside Flexarr, so it tells you about new versions but can’t install them.'
            : current?.install === 'MacApp'
              ? 'The Mac App Store updates Flexarr Server, so Flexarr tells you about new versions but doesn’t install them itself.'
              : undefined
        }
        action={
          <AsyncButton className="button small" onClick={check}>
            Check for updates
          </AsyncButton>
        }
      >
        {loaded.error && !current && (
          <p className="error" role="alert">
            Couldn’t load updates: {loaded.error.message}
          </p>
        )}
        {!current && !loaded.error && <p className="muted">Loading…</p>}
        {current && (
          <>
            <SettingRow as="div" label="Version" help={current.isDevelopment ? 'A development build, between releases.' : undefined}>
              <span className="setting-value">{current.version}</span>
            </SettingRow>
            <SettingRow as="div" label="Status" help={checkedLine(current)}>
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
                Update now
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
            <a href={current.latest.url} target="_blank" rel="noreferrer">
              This release on GitHub <External />
            </a>
          </p>
        </div>
      )}

      {current?.install === 'MacLaunchAgent' && !current.isDevelopment && <Automatic updates={current} onSaved={setUpdates} />}

      {confirming && current?.latest && (
        <ConfirmDialog title={`Update to Flexarr ${current.latest.version}?`} confirmLabel={current.inUse.length ? 'Update anyway' : 'Update now'} onConfirm={install} onClose={() => setConfirming(false)}>
          <p>Flexarr downloads the update, then restarts to install it, which takes a minute or two. If the new version doesn’t start, the one you have now is put back.</p>
          {current.inUse.length > 0 && (
            <>
              <p>Restarting interrupts what’s going on now:</p>
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
  if (updates.isDevelopment) return updates.latest ? `Latest release: ${updates.latest.version}` : 'Updated by deploying'
  if (updates.available && updates.latest) return `Flexarr ${updates.latest.version} is available`
  if (updates.latest) return 'Up to date'
  return updates.lastChecked ? 'Unknown' : 'Not checked yet'
}

function checkedLine(updates: Updates) {
  if (updates.isDevelopment) return 'Development builds aren’t updated from here: a release could be older than this.'
  return `${updates.lastChecked ? `Checked ${relativeTime(updates.lastChecked)}. ` : ''}Flexarr checks once a day.`
}

/** How the last update went, for a week after. */
function LastResult({ result }: { result: NonNullable<Updates['lastResult']> }) {
  const [now] = useState(() => Date.now())
  if (now - new Date(result.at).getTime() > 7 * 24 * 3600 * 1000) return null
  return result.ok ? (
    <p className="muted update-result" role="status">
      Updated from {result.from} to {result.to} {relativeTime(result.at)}.
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
    <SettingsGroup title={`Downloading ${downloading ?? `Flexarr ${version ?? ''}`}`}>
      <div className="update-progress" role="status">
        <progress max={1} value={share} aria-label="Download progress" />
        <span className="muted">
          {formatSize(received)}
          {total ? ` of ${formatSize(total)}` : ''}. Flexarr restarts to install it once it’s downloaded.
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
        <p>Flexarr runs in Docker here, and a container can’t replace itself. To update, get the new image and recreate the container. With Docker Compose, in the folder with your <code>docker-compose.yml</code>:</p>
        <pre className="update-command">docker compose pull flexarr{'\n'}docker compose up -d flexarr</pre>
        <p className="muted">
          If your image’s tag names a version (like <code>:0.50</code>), change it first. On Synology or Unraid, update the container the way the platform does. To update automatically, a tool like Watchtower can do it.{' '}
          <a href={`${guide}#docker`} target="_blank" rel="noreferrer">
            More in the guide
          </a>
        </p>
      </div>
    )
  if (updates.install === 'MacApp')
    return (
      <div className="panel update-howto">
        <p>
          To update, open the App Store on the Mac running Flexarr Server and choose <strong>Updates</strong>. A new version can take a day or two to reach the App Store after it’s released.
        </p>
      </div>
    )
  if (updates.install === 'MacLaunchAgent')
    return (
      <div className="panel update-howto">
        <p>This release has no Mac build to install, so update with the install script, from the Mac you installed from:</p>
        <pre className="update-command">cd flexarr{'\n'}git pull{'\n'}make deploy DEPLOY_HOST=&lt;your-mac&gt;</pre>
      </div>
    )
  return (
    <div className="panel update-howto">
      <p>
        This server wasn’t installed in a way Flexarr can update by itself. Update it the way it was installed.{' '}
        <a href={guide} target="_blank" rel="noreferrer">
          How to update
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
      setSaved('Saved')
      setTimeout(() => setSaved(undefined), 3000)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <SettingsGroup
      title="Automatic updates"
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
        label="Install updates automatically"
        help="When a new version comes out, install it between 4 and 5 in the morning, once nothing is playing, being watched live or recording. A busy night waits for the next."
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
      <SettingsHeader title="Updating">Flexarr is restarting to install {to ? `version ${to}` : 'the update'}. This page reloads when it’s back.</SettingsHeader>
      {late && (
        <p className="muted" role="status">
          It’s taking a while, which a big library’s first start after an update can. If it doesn’t come back, the log of the update is in <code>~/.flexarr/logs/update.log</code> on the server.
        </p>
      )}
    </section>
  )
}
