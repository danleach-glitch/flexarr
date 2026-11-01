import { useState, type FormEvent } from 'react'
import { ApiError, createCollection, getGenres, updateCollection, type CollectionSummary, type SmartRules } from '../api/client'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { useModal } from '../hooks/useModal'

const thisYear = new Date().getFullYear()

/**
 * Making or editing a collection: by hand (titles are added from their menus) or smart, filled by rules that keep
 * it up to date. The main profile chooses whether it's for everyone or just them.
 */
export function CollectionEditor({
  isAdmin,
  collection,
  rules: initialRules,
  onClose,
  onSaved,
}: {
  isAdmin: boolean
  collection?: CollectionSummary
  rules?: SmartRules | null
  onClose: () => void
  onSaved: (saved: CollectionSummary) => void
}) {
  const [name, setName] = useState(collection?.name ?? '')
  const [description, setDescription] = useState(collection?.description ?? '')
  const [personal, setPersonal] = useState(collection ? collection.personal : !isAdmin)
  const [smart, setSmart] = useState(collection ? collection.kind === 'Smart' : false)
  const [rules, setRules] = useState<SmartRules>(initialRules ?? { sort: 'rating', limit: 50 })
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const genres = useApi(getGenres, [])
  const set = (change: Partial<SmartRules>) => setRules((r) => ({ ...r, ...change }))

  const dialog = useModal<HTMLFormElement>(onClose)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setSaving(true)
    try {
      const saved = collection
        ? await updateCollection(collection.id, { name, description, rules: smart ? rules : undefined })
        : await createCollection({ name, description, personal, rules: smart ? rules : undefined })
      onSaved(saved)
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  const number = (value: string) => (value.trim() === '' ? null : Number(value))

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <form className="panel collection-editor" ref={dialog} role="dialog" aria-modal="true" aria-label={collection ? `Edit ${collection.name}` : 'New collection'} onClick={(e) => e.stopPropagation()} onSubmit={submit}>
        <h2>{collection ? `Edit ${collection.name}` : 'New collection'}</h2>
        <label className="field">
          Name
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} required maxLength={120} autoFocus placeholder="Sunday films" />
        </label>
        <label className="field">
          Description (optional)
          <input type="text" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} />
        </label>

        {!collection && (
          <fieldset className="field choice-row">
            <legend className="field-label">What goes in it</legend>
            <label className="checkbox">
              <input type="radio" name="kind" checked={!smart} onChange={() => setSmart(false)} /> Titles I choose
            </label>
            <label className="checkbox">
              <input type="radio" name="kind" checked={smart} onChange={() => setSmart(true)} /> Smart: titles matching rules, kept up to date
            </label>
          </fieldset>
        )}

        {!collection && isAdmin && (
          <fieldset className="field choice-row">
            <legend className="field-label">Who it’s for</legend>
            <label className="checkbox">
              <input type="radio" name="who" checked={!personal} onChange={() => setPersonal(false)} /> Everyone in the house
            </label>
            <label className="checkbox">
              <input type="radio" name="who" checked={personal} onChange={() => setPersonal(true)} /> Just me
            </label>
          </fieldset>
        )}

        {smart && (
          <fieldset className="smart-rules">
            <legend className="field-label">Rules</legend>
            <div className="field-row">
              <label>
                Type
                <select value={rules.type ?? ''} onChange={(e) => set({ type: (e.target.value || null) as SmartRules['type'] })}>
                  <option value="">Films and shows</option>
                  <option value="movie">Films</option>
                  <option value="series">Shows</option>
                  <option value="book">Audiobooks</option>
                </select>
              </label>
              <label className="grow">
                Genre
                <select value={rules.genres?.[0] ?? ''} onChange={(e) => set({ genres: e.target.value ? [e.target.value] : null })}>
                  <option value="">Any genre</option>
                  {genres.data?.map((g) => (
                    <option key={g.name}>{g.name}</option>
                  ))}
                </select>
              </label>
            </div>
            <div className="field-row">
              <label className="checkbox">
                <input type="checkbox" checked={rules.thisYear ?? false} onChange={(e) => set({ thisYear: e.target.checked, yearFrom: null, yearTo: null })} /> This year ({thisYear})
              </label>
              {!rules.thisYear && (
                <>
                  <label>
                    From year
                    <input type="number" min={1900} max={thisYear + 1} value={rules.yearFrom ?? ''} onChange={(e) => set({ yearFrom: number(e.target.value) })} />
                  </label>
                  <label>
                    To year
                    <input type="number" min={1900} max={thisYear + 1} value={rules.yearTo ?? ''} onChange={(e) => set({ yearTo: number(e.target.value) })} />
                  </label>
                </>
              )}
            </div>
            <div className="field-row">
              <label>
                Rated at least
                <input type="number" min={0} max={10} step={0.1} value={rules.minRating ?? ''} placeholder="Any" onChange={(e) => set({ minRating: number(e.target.value) })} />
              </label>
              <label>
                {rules.type === 'book' ? 'Finished' : 'Watched'}
                <select value={rules.watched ?? ''} onChange={(e) => set({ watched: (e.target.value || null) as SmartRules['watched'] })}>
                  <option value="">Either</option>
                  <option value="unwatched">Not yet</option>
                  <option value="watched">{rules.type === 'book' ? 'Already finished' : 'Already watched'}</option>
                </select>
              </label>
              <label>
                Added in the last
                <select value={rules.addedWithinDays ?? ''} onChange={(e) => set({ addedWithinDays: number(e.target.value) })}>
                  <option value="">Any time</option>
                  <option value="7">Week</option>
                  <option value="30">Month</option>
                  <option value="90">3 months</option>
                  <option value="365">Year</option>
                </select>
              </label>
            </div>
            <div className="field-row">
              <label className="grow">
                Title or description mentions
                <input type="text" value={rules.contains ?? ''} placeholder="e.g. Christmas" onChange={(e) => set({ contains: e.target.value || null })} />
              </label>
              <label>
                Order
                <select value={rules.sort ?? 'rating'} onChange={(e) => set({ sort: e.target.value as SmartRules['sort'] })}>
                  <option value="rating">Best rated</option>
                  <option value="added">Recently added</option>
                  <option value="year">Newest</option>
                  <option value="popularity">Most popular</option>
                  <option value="title">A–Z</option>
                </select>
              </label>
              <label>
                At most
                <input type="number" min={1} max={200} value={rules.limit ?? 50} onChange={(e) => set({ limit: number(e.target.value) ?? 50 })} />
              </label>
            </div>
            {rules.personId && <p className="muted">Also limited to one person’s films and shows (from the suggestion this came from).</p>}
          </fieldset>
        )}

        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button type="button" className="button quiet" onClick={onClose}>
            Cancel
          </button>
          <AsyncButton type="submit" className="button" busy={saving}>
            {collection ? 'Save' : 'Create collection'}
          </AsyncButton>
        </div>
      </form>
    </div>
  )
}
