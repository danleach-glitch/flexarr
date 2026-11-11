import { errorText } from '../lib/errorText'
import { useEffect, useState, type FormEvent } from 'react'
import { addToCollection, createCollection, getItemCollections, removeFromCollection, type ItemCollection } from '../api/client'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { useModal } from '../hooks/useModal'
import { t, Words } from '../i18n'

/** Asks for the Add to collection dialog, from anywhere (a card's menu closes before it opens). */
const OPEN = 'flexarr:add-to-collection'

export function openAddToCollection(item: { id: string; title: string }) {
  window.dispatchEvent(new CustomEvent(OPEN, { detail: item }))
}

/** Lives once in the layout and shows the dialog when asked. */
export function AddToCollectionHost() {
  const [item, setItem] = useState<{ id: string; title: string }>()
  useEffect(() => {
    const open = (e: Event) => setItem((e as CustomEvent<{ id: string; title: string }>).detail)
    window.addEventListener(OPEN, open)
    return () => window.removeEventListener(OPEN, open)
  }, [])
  return item ? <AddToCollectionDialog key={item.id} item={item} onClose={() => setItem(undefined)} /> : null
}

/** Tick the collections a title should be in, or start a new one with it. */
function AddToCollectionDialog({ item, onClose }: { item: { id: string; title: string }; onClose: () => void }) {
  const [version, setVersion] = useState(0)
  const collections = useApi((signal) => getItemCollections(item.id, signal), [item.id, version])
  const { me } = useSession()
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()
  const [creating, setCreating] = useState(false)
  // Ticks show at once; the server catches up (and a failure puts them back).
  const [ticked, setTicked] = useState<Record<string, boolean>>({})

  const dialog = useModal<HTMLDivElement>(onClose)

  async function run(work: () => Promise<unknown>) {
    try {
      setError(undefined)
      await work()
      setVersion((v) => v + 1)
    } catch (e) {
      setError(errorText(e))
    }
  }

  const contains = (c: ItemCollection) => ticked[c.id] ?? c.contains
  async function toggle(c: ItemCollection) {
    const now = !contains(c)
    setTicked((t) => ({ ...t, [c.id]: now }))
    try {
      setError(undefined)
      await (now ? addToCollection(c.id, item.id) : removeFromCollection(c.id, item.id))
    } catch (e) {
      setTicked((t) => ({ ...t, [c.id]: !now }))
      setError(errorText(e))
    }
  }

  function create(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setCreating(true)
    void run(async () => {
      const created = await createCollection({ name: name.trim(), personal: !(me?.isAdmin || me?.canUnlock) })
      await addToCollection(created.id, item.id)
      setName('')
    }).finally(() => setCreating(false))
  }

  const editable = collections.data?.filter((c) => c.canEdit) ?? []
  const others = collections.data?.filter((c) => !c.canEdit && c.contains) ?? []

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="panel add-to-collection" ref={dialog} role="dialog" aria-modal="true" aria-label={t('Add {title} to a collection', { title: item.title })} onClick={(e) => e.stopPropagation()}>
        <h2>{t('Add {title} to a collection', { title: item.title })}</h2>
        {collections.data && editable.length === 0 && <p className="muted">{t('No collections of yours yet. Start one below.')}</p>}
        <ul className="collection-choices">
          {editable.map((c) => (
            <li key={c.id}>
              <label className="checkbox">
                <input type="checkbox" checked={contains(c)} onChange={() => toggle(c)} /> {c.name}
                <span className="muted">{c.personal ? t('Just you') : t('Everyone')}</span>
              </label>
            </li>
          ))}
        </ul>
        {others.length > 0 && <p className="muted">{t('Also in {names}.', { names: others.map((c) => c.name).join(', ') })}</p>}
        <form className="field-row" onSubmit={create}>
          <label className="grow">
            {t('New collection')}
            <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} placeholder={t('Name it, then press Create')} />
          </label>
          <AsyncButton className="button" type="submit" disabled={!name.trim()} busy={creating}>
            {t('Create')}
          </AsyncButton>
        </form>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose}>
            {t(Words.done)}
          </button>
        </div>
      </div>
    </div>
  )
}
