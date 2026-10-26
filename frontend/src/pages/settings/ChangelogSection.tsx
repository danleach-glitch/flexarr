import { getChangelog, getSystemStatus } from '../../api/client'
import { useApi } from '../../hooks/useApi'

export function ChangelogSection() {
  const { data, error } = useApi(getChangelog, [])
  const { data: status } = useApi(getSystemStatus, [])
  return (
    <section>
      <header className="settings-header">
        <h2>Changelog</h2>
      </header>
      {/* The server's version is "0.8.0+<commit>"; the commit is for bug reports, not people. */}
      {status && <p className="settings-lede">You’re running Flexarr {status.version.split('+')[0]}.</p>}
      {error && <p className="error">The changelog couldn’t be loaded: {error.message}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      {data && <Changelog markdown={data} />}
    </section>
  )
}

/** CHANGELOG.md's shape: "## date", "### change", "- point" (with **bold**), and plain paragraphs. */
function Changelog({ markdown }: { markdown: string }) {
  const blocks: { kind: 'date' | 'title' | 'text'; text: string; points?: string[] }[] = []
  for (const line of markdown.split('\n')) {
    if (line.startsWith('# ')) continue
    if (line.startsWith('## ')) blocks.push({ kind: 'date', text: line.slice(3) })
    else if (line.startsWith('### ')) blocks.push({ kind: 'title', text: line.slice(4), points: [] })
    else if (line.startsWith('- ') && blocks.at(-1)?.points) blocks.at(-1)!.points!.push(line.slice(2))
    else if (line.trim()) blocks.push({ kind: 'text', text: line })
  }
  const inline = (text: string) =>
    text.split(/(\*\*[^*]+\*\*)/).map((part, i) => (part.startsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part))
  return (
    <div className="changelog">
      {blocks.map((b, i) =>
        b.kind === 'date' ? (
          <h3 key={i} className="settings-subhead">
            {b.text}
          </h3>
        ) : b.kind === 'title' ? (
          <div key={i} className="changelog-entry">
            <h4>{b.text}</h4>
            <ul>
              {b.points!.map((p) => (
                <li key={p}>{inline(p)}</li>
              ))}
            </ul>
          </div>
        ) : (
          <p key={i} className="settings-lede">
            {inline(b.text)}
          </p>
        ),
      )}
    </div>
  )
}
