import { useEffect, useState } from 'react'
import { getArrStatus, getRequestStatus, testArr, type ArrKind, type ArrStatus, type Settings } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { appLogos } from '../../lib/apps'
import { useSettings, SaveBar, OpenApp } from './shared'

interface DraftConnection {
  name: string
  kind: ArrKind
  baseUrl: string
  apiKey: string
  hasApiKey: boolean
}

export function ConnectionsSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  // Asked once the settings are in (and again after a save), not also while they load.
  const status = useApi((signal) => (settings ? getArrStatus(signal) : Promise.resolve(undefined)), [settings])
  const seerrStatus = useApi((signal) => (settings ? getRequestStatus(signal) : Promise.resolve(undefined)), [settings])
  const [connections, setConnections] = useState<DraftConnection[]>([])
  const [mappings, setMappings] = useState<Settings['pathMappings']>([])
  const [seerr, setSeerr] = useState({ baseUrl: '', apiKey: '', hasApiKey: false })
  const [tests, setTests] = useState<Record<number, ArrStatus | 'testing'>>({})

  useEffect(() => {
    if (!settings) return
    setConnections(settings.arr.map((a) => ({ ...a, apiKey: '' })))
    setMappings(settings.pathMappings)
    setSeerr({ baseUrl: settings.seerr?.baseUrl ?? '', apiKey: '', hasApiKey: settings.seerr?.hasApiKey ?? false })
  }, [settings])

  if (!settings) return waiting

  const update = (i: number, change: Partial<DraftConnection>) => setConnections((c) => c.map((x, j) => (j === i ? { ...x, ...change } : x)))

  return (
    <section>
      <header className="settings-header">
        <h2>Connections</h2>
      </header>
      <p className="settings-lede">
        Flexarr reads artwork, descriptions, cast and trailers from your Sonarr and Radarr, so no other accounts or API keys are needed. Seerr adds
        Discover and requests.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          void save({
            arr: connections.map((c) => ({ name: c.name, kind: c.kind, baseUrl: c.baseUrl, apiKey: c.apiKey || undefined })),
            pathMappings: mappings,
            seerr: { baseUrl: seerr.baseUrl, apiKey: seerr.apiKey || undefined },
          })
        }}
      >
        {connections.map((c, i) => {
          const live = status.data?.find((s) => s.name === c.name)
          const test = tests[i]
          const shown = test && test !== 'testing' ? test : live
          return (
            <div key={i} className="panel connection">
              <div className="connection-status">
                <span className={shown?.ok ? 'status-dot ok' : shown ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
                <img className="app-logo" src={appLogos[c.kind]} alt="" />
                <strong>{c.name || c.kind}</strong>
                <span className="muted">{test === 'testing' ? 'Testing…' : shown?.ok ? `Connected, version ${shown.version}` : shown?.error}</span>
                <OpenApp baseUrl={c.baseUrl} name={c.name || c.kind} />
              </div>
              <div className="field-row">
                <label>
                  Kind
                  <select value={c.kind} onChange={(e) => update(i, { kind: e.target.value as ArrKind })}>
                    <option>Sonarr</option>
                    <option>Radarr</option>
                    <option>Lidarr</option>
                  </select>
                </label>
                <label>
                  Name
                  <input type="text" value={c.name} onChange={(e) => update(i, { name: e.target.value })} />
                </label>
                <label className="grow">
                  Address
                  <input type="url" value={c.baseUrl} placeholder="http://localhost:8989" onChange={(e) => update(i, { baseUrl: e.target.value })} />
                </label>
                <label className="grow">
                  API key
                  <input
                    type="password"
                    value={c.apiKey}
                    placeholder={c.hasApiKey ? 'Saved; type to replace' : 'Settings › General in Sonarr or Radarr'}
                    onChange={(e) => update(i, { apiKey: e.target.value })}
                  />
                </label>
              </div>
              <div className="form-actions">
                <button
                  type="button"
                  className="button quiet small"
                  onClick={async () => {
                    setTests((t) => ({ ...t, [i]: 'testing' }))
                    const result = await testArr({ name: c.name, kind: c.kind, baseUrl: c.baseUrl, apiKey: c.apiKey || undefined }).catch(
                      (e: unknown): ArrStatus => ({ name: c.name, kind: c.kind, ok: false, version: null, error: e instanceof Error ? e.message : String(e) }),
                    )
                    setTests((t) => ({ ...t, [i]: result }))
                  }}
                >
                  Test connection
                </button>
                <button type="button" className="button danger small" onClick={() => setConnections((all) => all.filter((_, j) => j !== i))}>
                  Remove
                </button>
              </div>
            </div>
          )
        })}
        <div className="connections-add">
          <button
            type="button"
            className="button quiet small"
            onClick={() => setConnections((c) => [...c, { name: 'Sonarr', kind: 'Sonarr', baseUrl: 'http://localhost:8989', apiKey: '', hasApiKey: false }])}
          >
            Add a connection
          </button>
        </div>

        <h3 className="settings-subhead">Requests</h3>
        <p className="settings-lede">
          Connect Seerr (or Overseerr or Jellyseerr) to see what's trending that you don't have yet, and request it in one click. Requests follow your Seerr rules.
        </p>
        <div className="panel connection">
          <div className="connection-status">
            <span className={seerrStatus.data?.reachable ? 'status-dot ok' : seerrStatus.data?.configured ? 'status-dot bad' : 'status-dot'} aria-hidden="true" />
            <img className="app-logo" src={appLogos.Seerr} alt="" />
            <strong>Seerr</strong>
            <span className="muted">
              {seerrStatus.loading
                ? 'Checking…'
                : !seerrStatus.data?.configured
                  ? 'Not set up'
                  : seerrStatus.data.reachable
                    ? 'Connected'
                    : 'Flexarr can’t reach Seerr at this address, or the API key isn’t right'}
            </span>
            <OpenApp baseUrl={seerr.baseUrl} name="Seerr" />
          </div>
          <div className="field-row">
            <label className="grow">
              Seerr address
              <input type="url" value={seerr.baseUrl} placeholder="http://localhost:5055" onChange={(e) => setSeerr({ ...seerr, baseUrl: e.target.value })} />
            </label>
            <label className="grow">
              API key
              <input
                type="password"
                value={seerr.apiKey}
                placeholder={seerr.hasApiKey ? 'Saved; type to replace' : 'Settings › General in Seerr'}
                onChange={(e) => setSeerr({ ...seerr, apiKey: e.target.value })}
              />
            </label>
          </div>
        </div>

        <h3 className="settings-subhead">Path mappings</h3>
        <p className="settings-lede">
          If Sonarr and Radarr run in Docker, they see your media under different paths. Map their paths to the ones on this server.
        </p>
        <div className="panel">
          {mappings.map((m, i) => (
            <div key={i} className="field-row mapping">
              <label className="grow">
                Path in Sonarr or Radarr
                <input type="text" value={m.from} placeholder="/data" onChange={(e) => setMappings(mappings.map((x, j) => (j === i ? { ...x, from: e.target.value } : x)))} />
              </label>
              <label className="grow">
                Path on this server
                <input type="text" value={m.to} placeholder="/Volumes/Media/data" onChange={(e) => setMappings(mappings.map((x, j) => (j === i ? { ...x, to: e.target.value } : x)))} />
              </label>
              <button type="button" className="text-button" onClick={() => setMappings(mappings.filter((_, j) => j !== i))}>
                Remove
              </button>
            </div>
          ))}
          <button type="button" className="button quiet small" onClick={() => setMappings([...mappings, { from: '', to: '' }])}>
            Add a mapping
          </button>
        </div>
        <SaveBar saved={saved} error={saveError} />
      </form>
    </section>
  )
}
