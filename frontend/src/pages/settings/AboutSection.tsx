import { useState } from 'react'
import { getChangelog, getMacApp, getSystemStatus, setStartAtLogin, type MacApp } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { relativeTime } from '../../lib/format'
import { parse, ReleaseNotes } from './changelog'
import { SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText, useSettings } from './useSettings'

/** Which Flexarr this is, where it keeps things, and what's changed. */
export function AboutSection() {
  const { data: status } = useApi(getSystemStatus, [])
  const { settings } = useSettings()
  return (
    <section>
      <SettingsHeader title="About" />
      <SettingsGroup title="This server">
        {/* The server's version is "0.8.0+<commit>"; the commit is for bug reports, not people. */}
        <SettingRow as="div" label="Version">
          <span className="setting-value">{status ? status.version.split('+')[0] : '…'}</span>
        </SettingRow>
        {status && (
          <SettingRow as="div" label="Running on">
            <span className="setting-value">
              {status.osDescription}, since {relativeTime(status.startTime)}
            </span>
          </SettingRow>
        )}
        {settings && (
          <>
            <SettingRow as="div" label="Data and cache">
              <code className="setting-value">{settings.dataDirectory}</code>
            </SettingRow>
            <SettingRow as="div" label="FFmpeg">
              <code className="setting-value">{settings.ffmpegPath}</code>
            </SettingRow>
          </>
        )}
        <StartAtLogin />
      </SettingsGroup>
      <WhatsNew />
    </section>
  )
}

/** In the Mac app: opening it at login, so the server is back after the Mac restarts. Not shown anywhere else. */
function StartAtLogin() {
  const loaded = useApi(getMacApp, [])
  const [app, setApp] = useState<MacApp>()
  const [error, setError] = useState<string>()
  const current = app ?? loaded.data
  if (!current) return null

  async function change(on: boolean) {
    setError(undefined)
    try {
      setApp(await setStartAtLogin(on))
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <>
      <ToggleRow
        label="Start when you log in"
        help={
          current.startAtLogin === 'NeedsApproval'
            ? 'macOS needs your OK first: on the Mac, open System Settings › General › Login Items and turn on Flexarr Server.'
            : 'Opens Flexarr Server when you log in to this Mac, so the server is back after a restart.'
        }
        checked={current.startAtLogin !== 'Off'}
        onChange={(on) => void change(on)}
      />
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}

/** The latest release (and anything not in one yet), with the rest a click away. */
function WhatsNew() {
  const { data, error } = useApi(getChangelog, [])
  const [all, setAll] = useState(false)
  const releases = data ? parse(data) : []
  const latest = releases.findIndex((r) => !r.title.toLowerCase().startsWith('unreleased')) + 1
  const shown = all ? releases : releases.slice(0, Math.max(1, latest))

  return (
    <div className="settings-group">
      <div className="settings-group-head">
        <h3 className="settings-subhead">What’s new</h3>
      </div>
      {error && <p className="error">The changelog couldn’t be loaded: {error.message}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      <div className="changelog">
        {shown.map((release) => (
          <ReleaseNotes key={release.title} release={release} />
        ))}
      </div>
      {!all && releases.length > shown.length && (
        <button className="button quiet small changelog-more" onClick={() => setAll(true)}>
          Show older releases ({(releases.length - shown.length).toLocaleString()})
        </button>
      )}
    </div>
  )
}
