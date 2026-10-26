import { useState } from 'react'
import { getLayout, saveLayout, type Layout, type RowChoice } from '../../api/client'
import { ChevronDown, ChevronUp } from '../../components/icons'
import { useApi } from '../../hooks/useApi'

export function LayoutSection() {
  const { data, error } = useApi(getLayout, [])
  const [draft, setDraft] = useState<Layout>()
  const [saved, setSaved] = useState(false)
  const layout = draft ?? data

  if (error) return <p className="error">{error.message}</p>
  if (!layout) return <p className="muted">Loading…</p>

  const change = (key: keyof Layout, rows: RowChoice[]) => {
    setDraft({ ...layout, [key]: rows })
    setSaved(false)
  }

  return (
    <section>
      <header className="settings-header">
        <h2>Customise display</h2>
      </header>
      <p className="settings-lede">Choose which rows appear on Home and on each library’s Browse view, and in what order. This applies to every profile.</p>
      <form
        onSubmit={async (e) => {
          e.preventDefault()
          await saveLayout(layout)
          setSaved(true)
        }}
      >
        <RowList title="Home" rows={layout.home} onChange={(rows) => change('home', rows)} />
        <RowList title="Film libraries (Browse)" rows={layout.movies} onChange={(rows) => change('movies', rows)} />
        <RowList title="TV libraries (Browse)" rows={layout.shows} onChange={(rows) => change('shows', rows)} />
        <div className="form-actions">
          {saved && (
            <span className="muted" role="status">
              Saved. Home and Browse show the new layout next time they load.
            </span>
          )}
          <button type="submit" className="button" disabled={!draft}>
            Save layout
          </button>
        </div>
      </form>
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
    <fieldset className="panel row-list">
      <legend className="settings-subhead">{title}</legend>
      <ol>
        {rows.map((row, i) => (
          <li key={row.id}>
            <label className="checkbox">
              <input type="checkbox" checked={row.enabled} onChange={(e) => onChange(rows.map((r) => (r.id === row.id ? { ...r, enabled: e.target.checked } : r)))} />{' '}
              <span className={row.enabled ? undefined : 'muted'}>{row.label}</span>
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
