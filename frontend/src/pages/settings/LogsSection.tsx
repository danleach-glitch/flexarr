import { useEffect, useRef, useState } from 'react'
import {
  diagnosticsUrl,
  getLogs,
  getLogSettings,
  getLogUploads,
  getSystemStatus,
  logStreamUrl,
  logUploadUrl,
  saveLogSettings,
  type LogEntry,
  type LogLevel,
  type LogSettings,
  type LogSettingsUpdate,
} from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { formatSize, relativeTime } from '../../lib/format'
import { configureSentry } from '../../lib/sentry'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

/** The levels worth choosing between; Verbose and Fatal still show up in the list. */
const levels: LogLevel[] = ['Debug', 'Information', 'Warning', 'Error']
const badge: Record<LogLevel, string> = { Verbose: 'VRB', Debug: 'DBG', Information: 'INF', Warning: 'WRN', Error: 'ERR', Fatal: 'FTL' }

/** The areas, until the server's settings say what they're called. */
const areaKeys = ['general', 'playback', 'library', 'integrations', 'metadata', 'livetv', 'podcasts', 'notifications', 'requests', 'clients']

const PAGE_SIZE = 200
/** Most entries kept in the list; the oldest go first. */
export const MAX_ENTRIES = 2000
const SEARCH_DELAY_MS = 300

/** Whether a streamed entry matches the search, as the server's would: anywhere in it, any case. */
function matches(entry: LogEntry, q: string) {
  if (!q) return true
  const text = q.toLowerCase()
  return [entry.message, entry.exception ?? '', entry.source, ...Object.values(entry.properties)].some((s) => s.toLowerCase().includes(text))
}

const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString()

/** "14:02:11.482", with the day in front when it wasn't today. */
function stamp(iso: string) {
  const at = new Date(iso)
  const time = at.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
  const ms = String(at.getMilliseconds()).padStart(3, '0')
  return sameDay(at, new Date()) ? `${time}.${ms}` : `${at.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} ${time}`
}

function useDebounced<T>(value: T, ms: number) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return debounced
}

/** One line; opened, its exception and what it was about. */
function LogRow({ entry, areaName, open, onToggle, onFilter }: { entry: LogEntry; areaName: string; open: boolean; onToggle: () => void; onFilter: (q: string) => void }) {
  const properties = Object.entries(entry.properties)
  return (
    <li className={`log-entry level-${entry.level.toLowerCase()}`}>
      <button type="button" className="log-line" aria-expanded={open} onClick={onToggle}>
        <time dateTime={entry.timestamp} title={new Date(entry.timestamp).toLocaleString()}>
          {stamp(entry.timestamp)}
        </time>
        <span className="log-level">{badge[entry.level]}</span>
        <span className="log-area">{areaName}</span>
        <span className="log-message">{entry.message}</span>
      </button>
      {open && (
        <div className="log-detail">
          <dl>
            <dt>Source</dt>
            <dd>{entry.source}</dd>
            {properties.map(([key, value]) => (
              <div key={key} className="log-property">
                <dt>{key}</dt>
                <dd>
                  {key === 'playbackId' ? (
                    <button type="button" className="text-button" title="Show only this playback" onClick={() => onFilter(value)}>
                      {value}
                    </button>
                  ) : (
                    value
                  )}
                </dd>
              </div>
            ))}
          </dl>
          {entry.exception && <pre className="log-exception">{entry.exception}</pre>}
        </div>
      )}
    </li>
  )
}

/** The log itself: newest first, filtered on the server, with older pages on request and new lines as they're written. */
function LogViewer({ areaNames }: { areaNames: Record<string, string> }) {
  const [level, setLevel] = useState<LogLevel>('Information')
  const [area, setArea] = useState('')
  const [text, setText] = useState('')
  const q = useDebounced(text.trim(), SEARCH_DELAY_MS)
  const [live, setLive] = useState(false)
  const [streamState, setStreamState] = useState<'open' | 'reconnecting'>()

  const [entries, setEntries] = useState<LogEntry[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  // Which filters the list (or the error) is for: until they match the ones chosen, it's loading.
  const filters = `${level} ${area} ${q}`
  const [loadedFor, setLoadedFor] = useState<string>()
  const loading = loadedFor !== filters
  const [error, setError] = useState<string>()
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set())
  const search = useRef(q)
  useEffect(() => {
    search.current = q
  }, [q])

  useEffect(() => {
    const controller = new AbortController()
    getLogs({ level, area, q, limit: PAGE_SIZE }, controller.signal)
      .then((page) => {
        setEntries(page.entries)
        setCursor(page.nextCursor)
        setError(undefined)
      })
      .catch((e: unknown) => !controller.signal.aborted && setError(errorText(e)))
      .finally(() => !controller.signal.aborted && setLoadedFor(`${level} ${area} ${q}`))
    return () => controller.abort()
  }, [level, area, q])

  // Live: new lines at the top as the server writes them. The stream takes level and area; the search is applied here.
  useEffect(() => {
    if (!live || typeof EventSource === 'undefined') return
    const source = new EventSource(logStreamUrl({ level, area }))
    source.onopen = () => setStreamState('open')
    source.onerror = () => setStreamState('reconnecting')
    source.onmessage = (event: MessageEvent<string>) => {
      let entry: LogEntry
      try {
        entry = JSON.parse(event.data) as LogEntry
      } catch {
        return
      }
      if (!matches(entry, search.current)) return
      setEntries((list) => (list.some((e) => e.id === entry.id) ? list : [entry, ...list].slice(0, MAX_ENTRIES)))
    }
    return () => {
      source.close()
      setStreamState(undefined)
    }
  }, [live, level, area])

  async function loadOlder() {
    if (!cursor) return
    try {
      const page = await getLogs({ level, area, q, before: cursor, limit: PAGE_SIZE })
      // Kept at the cap from the newest end: what's being read is the older pages, so newer ones give way.
      setEntries((list) => {
        const ids = new Set(list.map((e) => e.id))
        const all = [...list, ...page.entries.filter((e) => !ids.has(e.id))]
        return all.length > MAX_ENTRIES ? all.slice(all.length - MAX_ENTRIES) : all
      })
      setCursor(page.nextCursor)
    } catch (e) {
      setError(errorText(e))
    }
  }

  function toggle(id: string) {
    setOpen((was) => {
      const next = new Set(was)
      if (!next.delete(id)) next.add(id)
      return next
    })
  }

  return (
    <div className="settings-group">
      <div className="log-filters">
        <select aria-label="Minimum level" value={level} onChange={(e) => setLevel(e.target.value as LogLevel)}>
          {levels.map((l) => (
            <option key={l} value={l}>
              {l}
            </option>
          ))}
        </select>
        <select aria-label="Area" value={area} onChange={(e) => setArea(e.target.value)}>
          <option value="">All areas</option>
          {Object.entries(areaNames).map(([key, name]) => (
            <option key={key} value={key}>
              {name}
            </option>
          ))}
        </select>
        <input type="search" className="log-search" placeholder="Search" aria-label="Search the log" value={text} onChange={(e) => setText(e.target.value)} />
        <label className="log-live">
          <input type="checkbox" role="switch" className="switch" checked={live} onChange={(e) => setLive(e.target.checked)} />
          Live
        </label>
      </div>
      <div className="panel log-panel">
        {error && !loading ? (
          <p className="error" role="alert">
            {error}
          </p>
        ) : loading && !entries.length ? (
          <p className="muted">Loading…</p>
        ) : !entries.length ? (
          <p className="muted">Nothing logged{q ? ' matches that' : ''}.</p>
        ) : (
          <ol className="log-list" aria-label="Log">
            {entries.map((entry) => (
              <LogRow
                key={entry.id}
                entry={entry}
                areaName={areaNames[entry.area] ?? entry.area}
                open={open.has(entry.id)}
                onToggle={() => toggle(entry.id)}
                onFilter={setText}
              />
            ))}
          </ol>
        )}
      </div>
      <div className="log-foot">
        <p className="muted settings-status" role="status">
          {live ? (streamState === 'reconnecting' ? 'Reconnecting…' : 'Live') : ''}
          {live && entries.length >= MAX_ENTRIES ? ` · showing the newest ${MAX_ENTRIES}` : ''}
        </p>
        {cursor && entries.length < MAX_ENTRIES && (
          <AsyncButton className="button quiet small" onClick={loadOlder}>
            Load older
          </AsyncButton>
        )}
      </div>
    </div>
  )
}

export const errorReportsDocs = 'https://github.com/danleach-glitch/flexarr/blob/main/docs/logging.md#error-reports'

/** This browser follows the server's switch at once, without waiting for a reload. */
async function followSwitch(on: boolean) {
  const status = await getSystemStatus()
  configureSentry(status.errorReports ?? { enabled: on, installId: null }, status.version.split('+')[0])
}

/**
 * The one switch for opt-in error reports to Flexarr's developer, for the server and every app, with what's sent and
 * what never is in plain sight beside it.
 */
function ErrorReports({ settings, onSaved }: { settings: LogSettings; onSaved: (settings: LogSettings) => void }) {
  const [on, setOn] = useState(settings.errorReports)
  const [saved, setSaved] = useState<string>()
  const [error, setError] = useState<string>()

  async function change(next: boolean) {
    setOn(next)
    setError(undefined)
    // Off stops this browser's reports before the server has even answered.
    if (!next) configureSentry({ enabled: false, installId: null }, '')
    try {
      const result = await saveLogSettings({ errorReports: next })
      onSaved(result)
      setOn(result.errorReports)
      setSaved(result.errorReports ? 'On' : 'Off')
      setTimeout(() => setSaved(undefined), 3000)
      await followSwitch(result.errorReports).catch(() => {})
    } catch (e) {
      setOn(settings.errorReports)
      setError(errorText(e))
      await followSwitch(settings.errorReports).catch(() => {})
    }
  }

  return (
    <SettingsGroup title="Error reports" note={saved || error ? <SaveStatus saved={saved} error={error} /> : undefined}>
      <ToggleRow label="Send error reports to Flexarr’s developer" checked={on} onChange={(next) => void change(next)} />
      <div className="error-reports-privacy">
        <p>Off by default. When on, errors from this server and its apps (not your logs) go to Sentry so bugs can be fixed sooner.</p>
        <p>
          <strong>Sent:</strong> what went wrong and where in the code; Flexarr version; OS and device model; a random id for this server.
        </p>
        <p>
          <strong>Never sent:</strong> titles, file names or paths, what you watch, profile or device names, addresses (IP, server, network), keys or
          passwords. These are removed on your server or device before anything leaves it.
        </p>
        <p>
          Switching off stops it at once, on every app.{' '}
          <a href={errorReportsDocs} target="_blank" rel="noreferrer">
            What exactly is sent
          </a>
        </p>
      </div>
    </SettingsGroup>
  )
}

/** How much the server writes, overall and area by area, and how long it keeps it. Each change saves as it's made. */
function LogLevels({ settings, onSaved }: { settings: LogSettings; onSaved: (settings: LogSettings) => void }) {
  const [draft, setDraft] = useState(settings)
  const [saved, setSaved] = useState<string>()
  const [error, setError] = useState<string>()

  async function save(changes: LogSettingsUpdate, next: LogSettings) {
    setDraft(next)
    setError(undefined)
    try {
      const result = await saveLogSettings(changes)
      setDraft(result)
      onSaved(result)
      setSaved('Saved')
      setTimeout(() => setSaved(undefined), 3000)
    } catch (e) {
      setDraft(settings)
      setError(errorText(e))
    }
  }

  function setArea(key: string, level: LogLevel | null) {
    void save({ areas: [{ key, level }] }, { ...draft, areas: draft.areas.map((a) => (a.key === key ? { ...a, level } : a)) })
  }

  /** Whole numbers of at least 1, saved when the field is left. */
  function number(field: 'retentionDays' | 'maxSizeMb', text: string) {
    const value = Math.round(Number(text))
    if (!Number.isFinite(value) || value < 1 || value === settings[field]) return setDraft({ ...draft, [field]: settings[field] })
    void save({ [field]: value }, { ...draft, [field]: value })
  }

  const status = saved || error ? <SaveStatus saved={saved} error={error} /> : undefined
  return (
    <>
      <SettingsGroup title="Levels" note={status}>
        <SettingRow label="Default level">
          <select value={draft.level} onChange={(e) => void save({ level: e.target.value as LogLevel }, { ...draft, level: e.target.value as LogLevel })}>
            {levels.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </select>
        </SettingRow>
        {draft.areas.map((a) => (
          <SettingRow key={a.key} label={a.name}>
            <select value={a.level ?? ''} onChange={(e) => setArea(a.key, (e.target.value || null) as LogLevel | null)}>
              <option value="">Default ({draft.level})</option>
              {levels.map((l) => (
                <option key={l} value={l}>
                  {l}
                </option>
              ))}
            </select>
          </SettingRow>
        ))}
      </SettingsGroup>
      <SettingsGroup title="Retention">
        <SettingRow label="Keep for (days)">
          <input
            type="number"
            className="log-number"
            min={1}
            value={draft.retentionDays}
            onChange={(e) => setDraft({ ...draft, retentionDays: Number(e.target.value) })}
            onBlur={(e) => number('retentionDays', e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          />
        </SettingRow>
        <SettingRow label="Largest size (MB)">
          <input
            type="number"
            className="log-number"
            min={1}
            value={draft.maxSizeMb}
            onChange={(e) => setDraft({ ...draft, maxSizeMb: Number(e.target.value) })}
            onBlur={(e) => number('maxSizeMb', e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
          />
        </SettingRow>
      </SettingsGroup>
    </>
  )
}

/** Diagnostics the Apple TV and iPhone apps sent from their settings. */
function DeviceUploads() {
  const uploads = useApi(getLogUploads, [])
  return (
    <SettingsGroup title="Diagnostics from apps">
      {uploads.error ? (
        <p className="error">{errorText(uploads.error)}</p>
      ) : !uploads.data ? (
        <p className="muted">Loading…</p>
      ) : !uploads.data.length ? (
        <p className="muted">None yet.</p>
      ) : (
        uploads.data.map((u) => (
          <SettingRow
            key={u.id}
            as="div"
            label={u.device}
            help={[u.platform, u.profile, u.appVersion && `v${u.appVersion}`, relativeTime(u.uploadedAt), formatSize(u.sizeBytes)].filter(Boolean).join(' · ')}
          >
            <a className="button quiet small" href={logUploadUrl(u.id)} download>
              Download
            </a>
          </SettingRow>
        ))
      )}
    </SettingsGroup>
  )
}

/** The server's log, how much of it to keep, and diagnostics to attach to a bug report. */
export function LogsSection() {
  const loaded = useApi(getLogSettings, [])
  // As loaded, then as the server answered each change.
  const [changed, setSettings] = useState<LogSettings>()
  const settings = changed ?? loaded.data
  const areaNames = Object.fromEntries((settings?.areas ?? areaKeys.map((key) => ({ key, name: key }))).map((a) => [a.key, a.name]))

  return (
    <section className="logs">
      <SettingsHeader
        title="Logs"
        action={
          <a className="button quiet small" href={diagnosticsUrl} download>
            Download diagnostics
          </a>
        }
      />
      {settings && <ErrorReports settings={settings} onSaved={setSettings} />}
      <LogViewer areaNames={areaNames} />
      {settings ? <LogLevels settings={settings} onSaved={setSettings} /> : loaded.error && <SaveStatus error={errorText(loaded.error)} />}
      <DeviceUploads />
    </section>
  )
}
