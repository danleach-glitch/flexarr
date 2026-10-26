import { useEffect, useRef, useState } from 'react'
import { getLayout, saveLayout, type Layout, type RowChoice } from '../../api/client'
import { ChevronDown, ChevronUp } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingsHeader } from './shared'
import { errorText } from './useSettings'

export function LayoutSection() {
  const { data, error } = useApi(getLayout, [])
  const [draft, setDraft] = useState<Layout>()
  const [status, setStatus] = useState<{ saved?: string; error?: string }>({})
  const layout = draft ?? data
  // Saved a moment after the last change, so a row moved several places up is one save, not several.
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

  if (error) return <p className="error">{error.message}</p>
  if (!layout) return <p className="muted">Loading…</p>

  const change = (key: keyof Layout, rows: RowChoice[]) => {
    const next = { ...layout, [key]: rows }
    setDraft(next)
    setStatus({})
    clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      saveLayout(next).then(
        () => setStatus({ saved: 'Saved. Home and Browse show it next time they load.' }),
        (e: unknown) => setStatus({ error: errorText(e) }),
      )
    }, 600)
  }

  return (
    <section>
      <SettingsHeader title="Home and Browse">Which rows Home and each library’s Browse show, and in what order, for every profile.</SettingsHeader>
      <SaveStatus {...status} />
      <RowList title="Home" rows={layout.home} onChange={(rows) => change('home', rows)} />
      <RowList title="Film libraries" rows={layout.movies} onChange={(rows) => change('movies', rows)} />
      <RowList title="TV libraries" rows={layout.shows} onChange={(rows) => change('shows', rows)} />
    </section>
  )
}

function RowList({ title, rows, onChange }: { title: string; rows: RowChoice[]; onChange: (rows: RowChoice[]) => void }) {
  const move = (from: number, to: number) => {
    const next = [...rows]
    const [row] = next.splice(from, 1)
    next.splice(to, 0, row)
    onChange(next)
  }
  return (
    <fieldset className="settings-group row-list">
      <legend className="settings-subhead">{title}</legend>
      <ol className="panel">
        {rows.map((row, i) => (
          <li key={row.id}>
            <label className="row-list-toggle">
              <span className={row.enabled ? undefined : 'muted'}>{row.label}</span>
              <input
                type="checkbox"
                role="switch"
                className="switch"
                checked={row.enabled}
                onChange={(e) => onChange(rows.map((r) => (r.id === row.id ? { ...r, enabled: e.target.checked } : r)))}
              />
            </label>
            <span className="row-list-move">
              <button type="button" className="icon-button" aria-label={`Move ${row.label} up`} disabled={i === 0} onClick={() => move(i, i - 1)}>
                <ChevronUp />
              </button>
              <button type="button" className="icon-button" aria-label={`Move ${row.label} down`} disabled={i === rows.length - 1} onClick={() => move(i, i + 1)}>
                <ChevronDown />
              </button>
            </span>
          </li>
        ))}
      </ol>
    </fieldset>
  )
}
