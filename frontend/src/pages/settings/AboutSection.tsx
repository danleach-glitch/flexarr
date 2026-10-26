import { useState } from 'react'
import { getChangelog, getSystemStatus } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { relativeTime } from '../../lib/format'
import { SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { useSettings } from './useSettings'

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
      </SettingsGroup>
      <WhatsNew />
    </section>
  )
}

interface Release {
  title: string
  blocks: { kind: 'title' | 'text'; text: string; points: string[] }[]
}

/** CHANGELOG.md's shape: "## version · date", "### change", "- point" (with **bold**), and plain paragraphs. */
function parse(markdown: string) {
  const releases: Release[] = []
  for (const line of markdown.split('\n')) {
    const release = releases.at(-1)
    if (line.startsWith('## ')) releases.push({ title: line.slice(3), blocks: [] })
    // Before the first release: the file's own title and introduction.
    else if (!release) continue
    else if (line.startsWith('### ')) release.blocks.push({ kind: 'title', text: line.slice(4), points: [] })
    else if (line.startsWith('- ') && release.blocks.at(-1)?.kind === 'title') release.blocks.at(-1)!.points.push(line.slice(2))
    else if (line.trim()) release.blocks.push({ kind: 'text', text: line, points: [] })
  }
  return releases
}

const inline = (text: string) => text.split(/(\*\*[^*]+\*\*)/).map((part, i) => (part.startsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part))

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
          <article key={release.title} className="panel changelog-release">
            <h4 className="changelog-version">{release.title}</h4>
            {release.blocks.map((b, i) =>
              b.kind === 'title' ? (
                <div key={i} className="changelog-entry">
                  <h5>{b.text}</h5>
                  <ul>
                    {b.points.map((p) => (
                      <li key={p}>{inline(p)}</li>
                    ))}
                  </ul>
                </div>
              ) : (
                <p key={i} className="muted">
                  {inline(b.text)}
                </p>
              ),
            )}
          </article>
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
