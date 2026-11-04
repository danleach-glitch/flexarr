import { useId, type KeyboardEvent } from 'react'
import { withName } from '../lib/names'
import { t, Words } from '../i18n'

/**
 * Names as chips, each with a way to remove it, and a box to add one (Enter or Tab adds it; saving adds what's left
 * typed). Names can hold commas ("Martin Luther King, Jr."), so nothing is split.
 */
export function NameList({ kind, names, typed, disabled, onType, onChange }: { kind: 'genres' | 'authors' | 'narrators'; names: string[]; typed: string; disabled: boolean; onType: (text: string) => void; onChange: (names: string[]) => void }) {
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
  const label = kind === 'genres' ? t(Words.genres) : kind === 'authors' ? t('Authors') : t('Narrators')
  const placeholder = names.length
    ? kind === 'genres'
      ? t('Add another genre')
      : kind === 'authors'
        ? t('Add another author')
        : t('Add another narrator')
    : kind === 'genres'
      ? t('Add a genre')
      : kind === 'authors'
        ? t('Add an author')
        : t('Add a narrator')
  const addOne = kind === 'genres' ? t('Add a genre') : kind === 'authors' ? t('Add an author') : t('Add a narrator')
  return (
    <div className="field wide">
      <span className="field-label" id={id}>
        {label}
      </span>
      <div className="book-editor-names" role="group" aria-labelledby={id}>
        {names.map((n) => (
          <button key={n} type="button" className="chip" disabled={disabled} aria-label={t('Remove {name}', { name: n })} onClick={() => onChange(names.filter((x) => x !== n))}>
            {n} ×
          </button>
        ))}
        <input type="text" value={typed} onChange={(e) => onType(e.target.value)} onKeyDown={key} onBlur={add} disabled={disabled} placeholder={placeholder} aria-label={addOne} maxLength={200} />
      </div>
    </div>
  )
}
