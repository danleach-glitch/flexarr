import { useState } from 'react'
import { ApiError, getPlexSource, importPlexAvatars, type PlexAvatarResult, getProfiles, importFromPlex, type PlexImportResult } from '../../api/client'
import { useApi } from '../../hooks/useApi'

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
      <header className="settings-header">
        <h2>Bring over from Plex</h2>
      </header>
      <p className="settings-lede">
        Copy what you've watched, and where you stopped, from Plex Media Server. Flexarr matches by file, reads a copy of Plex's database and never changes
        Plex itself. You can run this again at any time; anything you've watched more recently in Flexarr is kept.
      </p>
      <div className="panel">
        {source.loading && <p className="muted">Looking for Plex…</p>}
        {source.data && !source.data.found && (
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
              <button type="submit" className="button quiet small">
                Look there
              </button>
            </div>
          </form>
        )}
        {source.data?.found && (
          <>
            <p className="muted">
              Found Plex at <code>{source.data.path}</code>
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
              <button className="button" disabled={busy || chosen === undefined} onClick={run}>
                {busy ? 'Bringing history over…' : 'Bring history over'}
              </button>
            </div>
          </>
        )}
      </div>
      {source.data?.found && <PlexPictures path={source.data.path ?? undefined} />}
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
      <h3>Profile pictures</h3>
      <p className="muted">Use the pictures from your Plex Home users for the Flexarr profiles with the same names. It replaces any picture those profiles already have.</p>
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
        <button className="button" disabled={busy} onClick={run}>
          {busy ? 'Fetching pictures…' : 'Use Plex profile pictures'}
        </button>
      </div>
    </div>
  )
}
