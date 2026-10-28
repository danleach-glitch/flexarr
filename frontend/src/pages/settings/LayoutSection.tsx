import { useEffect, useId, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { getLayout, saveLayout, type Layout, type RowChoice } from '../../api/client'
import { ChevronDown, ChevronUp, Grip } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingsHeader, ToggleRow } from './shared'
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

  const save = (next: Layout) =>
    saveLayout(next).then(
      () => setStatus({ saved: 'Saved. Home and Browse show it next time they load.' }),
      (e: unknown) => setStatus({ error: errorText(e) }),
    )
  const change = (key: 'home' | 'movies' | 'shows', rows: RowChoice[]) => {
    const next = { ...layout, [key]: rows }
    setDraft(next)
    setStatus({})
    clearTimeout(timer.current)
    timer.current = setTimeout(() => void save(next), 600)
  }
  // Straight away, and Home's rows redrawn from the answer: combining swaps the libraries' rows for one of each kind.
  const combine = (on: boolean) => {
    clearTimeout(timer.current)
    const next = { ...layout, combineLibraries: on }
    setDraft(next)
    setStatus({})
    saveLayout(next).then(
      (saved) => {
        setDraft(saved)
        setStatus({ saved: 'Saved. Home and Browse show it next time they load.' })
      },
      (e: unknown) => {
        setDraft(layout)
        setStatus({ error: errorText(e) })
      },
    )
  }

  return (
    <section>
      <SettingsHeader title="Home and Browse">Which rows Home and each library’s Browse show, for every profile. Drag a row to move it.</SettingsHeader>
      <SaveStatus {...status} />
      <RowList title="Home" rows={layout.home} onChange={(rows) => change('home', rows)}>
        {layout.combinable && (
          <div className="panel setting-rows">
            <ToggleRow
              label="Combine libraries of the same kind"
              help="One Latest unwatched row for all your film libraries and one for all your TV ones. A title in more than one library shows once."
              checked={layout.combineLibraries}
              onChange={combine}
            />
          </div>
        )}
      </RowList>
      <RowList title="Film libraries" rows={layout.movies} onChange={(rows) => change('movies', rows)} />
      <RowList title="TV libraries" rows={layout.shows} onChange={(rows) => change('shows', rows)} />
    </section>
  )
}

/**
 * A list of rows to turn on or off and put in order: dragged by the grip, or moved with the arrows (from the keyboard,
 * or on a touch screen, where dragging doesn't work).
 */
function RowList({ title, rows, onChange, children }: { title: string; rows: RowChoice[]; onChange: (rows: RowChoice[]) => void; children?: ReactNode }) {
  const listId = useId()
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  const move = (from: number, to: number) => {
    const next = [...rows]
    const [row] = next.splice(from, 1)
    next.splice(to, 0, row)
    onChange(next)
  }

  const dragStart = (at: number, row: RowChoice) => (e: DragEvent) => {
    setDragging(at)
    e.dataTransfer.effectAllowed = 'move'
    e.dataTransfer.setData('text/plain', row.label)
  }
  const dragOver = (at: number) => (e: DragEvent) => {
    if (dragging === null) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (over !== at) setOver(at)
  }
  const drop = (at: number) => (e: DragEvent) => {
    e.preventDefault()
    if (dragging !== null && dragging !== at) move(dragging, at)
    setDragging(null)
    setOver(null)
  }
  const dragEnd = () => {
    setDragging(null)
    setOver(null)
  }
  // The line shows where the row will land: under the one it's dropped on when moving down, over it when moving up.
  const landing = (at: number) => (dragging === null || over !== at || dragging === at ? undefined : dragging < at ? 'drop-below' : 'drop-above')

  return (
    <fieldset className="settings-group row-list">
      <legend className="settings-subhead">{title}</legend>
      {children}
      <ol className="panel">
        {rows.map((row, i) => (
          <li
            key={row.id}
            className={[dragging === i && 'dragging', landing(i)].filter(Boolean).join(' ') || undefined}
            draggable
            onDragStart={dragStart(i, row)}
            onDragOver={dragOver(i)}
            onDragLeave={() => over === i && setOver(null)}
            onDrop={drop(i)}
            onDragEnd={dragEnd}
          >
            <span className="row-list-grip" aria-hidden="true">
              <Grip />
            </span>
            <label className="row-list-name" htmlFor={`${listId}-${row.id}`}>
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
            <input
              id={`${listId}-${row.id}`}
              type="checkbox"
              role="switch"
              className="switch"
              checked={row.enabled}
              onChange={(e) => onChange(rows.map((r) => (r.id === row.id ? { ...r, enabled: e.target.checked } : r)))}
            />
          </li>
        ))}
      </ol>
    </fieldset>
  )
}
