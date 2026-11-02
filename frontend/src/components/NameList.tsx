import { useId, type KeyboardEvent } from 'react'
import { withName } from '../lib/names'

/**
 * Names as chips, each with a way to remove it, and a box to add one (Enter or Tab adds it; saving adds what's left
 * typed). Names can hold commas ("Martin Luther King, Jr."), so nothing is split.
 */
export function NameList({ label, names, typed, disabled, onType, onChange }: { label: string; names: string[]; typed: string; disabled: boolean; onType: (text: string) => void; onChange: (names: string[]) => void }) {
  const id = useId()
  function add() {
    const next = withName(names, typed)
    if (next !== names) onChange(next)
    onType('')
  }
  function key(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter' && typed.trim()) {
      e.preventDefault()
      add()
    }
  }
  const one = label === 'Genres' ? 'genre' : label === 'Authors' ? 'author' : 'narrator'
  const article = one === 'author' ? 'an' : 'a'
  return (
    <div className="field wide">
      <span className="field-label" id={id}>
        {label}
      </span>
      <div className="book-editor-names" role="group" aria-labelledby={id}>
        {names.map((n) => (
          <button key={n} type="button" className="chip" disabled={disabled} aria-label={`Remove ${n}`} onClick={() => onChange(names.filter((x) => x !== n))}>
            {n} ×
          </button>
        ))}
        <input type="text" value={typed} onChange={(e) => onType(e.target.value)} onKeyDown={key} onBlur={add} disabled={disabled} placeholder={`Add ${names.length ? 'another' : article} ${one}`} aria-label={`Add ${article} ${one}`} maxLength={200} />
      </div>
    </div>
  )
}
