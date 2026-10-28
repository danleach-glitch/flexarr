import { useState } from 'react'
import {
  ApiError,
  browseFilesystem,
  chooseFolderOnMac,
  getPlexSource,
  importPlexAvatars,
  type PlexAvatarResult,
  getProfiles,
  importFromPlex,
  type PlexImportResult,
} from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { SettingsHeader } from './shared'

const database = 'com.plexapp.plugins.library.db'

/** Plex's database inside a folder chosen on the Mac: Plex Media Server's own folder, or anywhere down to Databases. */
function databaseIn(folder: string) {
  const below = ['Plex Media Server', 'Plug-in Support', 'Databases']
  const last = folder.replace(/\/+$/, '').split('/').pop() ?? ''
  const at = below.indexOf(last)
  return [folder.replace(/\/+$/, ''), ...below.slice(at === -1 ? 1 : at + 1), database].join('/')
}

export function ImportSection() {
  const profiles = useApi(getProfiles, [])
  const [target, setTarget] = useState<string>()
  const [path, setPath] = useState('')
  const [lookup, setLookup] = useState(0)
  const source = useApi((signal) => getPlexSource(path || undefined, signal), [lookup])
  const [account, setAccount] = useState<number>()
  const [result, setResult] = useState<PlexImportResult>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const chosen = account ?? source.data?.accounts[0]?.id
  // In the Mac app, the server can only read Plex's folder once it's been chosen in macOS's folder window.
  const onMac = useApi((signal) => browseFilesystem(undefined, signal), []).data?.chooseOnMac === true

  async function chooseOnMac() {
    const picked = await chooseFolderOnMac('~/Library/Application Support/Plex Media Server')
    if (!picked) return
    setPath(databaseIn(picked.path))
    setLookup((n) => n + 1)
  }

  async function run() {
    if (chosen === undefined) return
    setBusy(true)
    setError(undefined)
    try {
      setResult(await importFromPlex(chosen, source.data?.path ?? undefined, target ?? profiles.data?.find((p) => p.isMain)?.id))
    } catch (e) {
      setError(e instanceof ApiError && e.messages.length ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section>
      <SettingsHeader title="Bring over from Plex" back={{ to: '/settings/libraries', label: 'Libraries' }}>
        Copies what you’ve watched, and where you stopped, from Plex Media Server. Plex itself is never changed, and it’s safe to run again: anything watched
        more recently in Flexarr is kept.
      </SettingsHeader>
      <div className="panel">
        {source.loading && <p className="muted">Looking for Plex…</p>}
        {source.data && !source.data.found && onMac && (
          <div className="field">
            <span className="field-label">
              Choose Plex’s folder: <code>Plex Media Server</code>, in your Library’s Application Support folder. Flexarr opens it in a window on the Mac.
            </span>
            {source.error && <p className="error">{source.error.message}</p>}
            <div className="form-actions">
              <AsyncButton className="button small" onClick={chooseOnMac}>
                Choose Plex’s folder on the Mac…
              </AsyncButton>
            </div>
          </div>
        )}
        {source.data && !source.data.found && !onMac && (
          <form
            className="field"
            onSubmit={(e) => {
              e.preventDefault()
              setLookup((n) => n + 1)
            }}
          >
            <span className="field-label">Plex wasn't found in the usual places. Where is com.plexapp.plugins.library.db?</span>
            <input type="text" value={path} onChange={(e) => setPath(e.target.value)} placeholder="/path/to/Plug-in Support/Databases/com.plexapp.plugins.library.db" />
            <div className="form-actions">
              <AsyncButton type="submit" className="button quiet small" busy={source.loading}>
                Look there
              </AsyncButton>
            </div>
          </form>
        )}
        {source.data?.found && (
          <>
            <p className="muted">
              Found Plex at <code className="folder">{source.data.path}</code>
            </p>
            {source.data.accounts.length === 0 ? (
              <p>That Plex server has no watch history to bring over.</p>
            ) : (
              <label className="field">
                <span className="field-label">Whose history</span>
                <select value={chosen} onChange={(e) => setAccount(Number(e.target.value))}>
                  {source.data.accounts.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name}: {a.watched.toLocaleString()} watched, {a.inProgress.toLocaleString()} in progress
                    </option>
                  ))}
                </select>
              </label>
            )}
            {profiles.data && profiles.data.length > 1 && (
              <label className="field">
                <span className="field-label">Into which Flexarr profile</span>
                <select value={target ?? profiles.data.find((p) => p.isMain)?.id} onChange={(e) => setTarget(e.target.value)}>
                  {profiles.data.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {result && (
              <p role="status" className="import-result">
                Brought over {result.matched.toLocaleString()} titles: {result.watched.toLocaleString()} watched and {result.inProgress.toLocaleString()} to resume.
                {result.keptNewer > 0 && ` ${result.keptNewer} you'd watched more recently in Flexarr were left as they were.`}
                {result.unmatched > 0 && ` ${result.unmatched.toLocaleString()} files Plex knows about aren't in your Flexarr libraries.`}
              </p>
            )}
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <div className="form-actions">
              <AsyncButton className="button" busy={busy} disabled={chosen === undefined} onClick={run}>
                Bring history over
              </AsyncButton>
            </div>
          </>
        )}
      </div>
      {/* Plex's sign-in, which the pictures need, is out of the Mac app's reach. */}
      {source.data?.found && !onMac && <PlexPictures path={source.data.path ?? undefined} />}
    </section>
  )
}

function PlexPictures({ path }: { path?: string }) {
  const [result, setResult] = useState<PlexAvatarResult>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  async function run() {
    setBusy(true)
    setError(undefined)
    try {
      setResult(await importPlexAvatars(path))
    } catch (e) {
      setError(e instanceof ApiError && e.messages.length ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel">
      <h3 className="panel-title">Profile pictures</h3>
      <p className="muted">Uses your Plex Home users’ pictures for the Flexarr profiles with the same names, in place of any picture they have.</p>
      {result && (
        <p role="status" className="import-result">
          {result.matched.length > 0 ? `Updated ${result.matched.map((m) => m.profile).join(' and ')}.` : 'No profiles matched a Plex user’s name.'}
          {result.unmatchedPlexUsers.length > 0 && ` No profile is called ${result.unmatchedPlexUsers.join(' or ')}; add one and run this again.`}
        </p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="form-actions">
        <AsyncButton className="button quiet small" busy={busy} onClick={run}>
          Use Plex profile pictures
        </AsyncButton>
      </div>
    </div>
  )
}
