import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, fixMatch, imageUrl, resetMatch, searchMatches, type MatchCandidate } from '../api/client'
import { useApi } from '../hooks/useApi'
import { Search } from './icons'

interface Props {
  item: { id: string; title: string; kind: string }
  /** The folder on disk, so it's clear which copy is being matched. */
  path?: string | null
  /** Already matched by hand: offer going back to automatic matching. */
  locked?: boolean
  onFixed: () => void
  onClose: () => void
}

/**
 * Choose which show or film a library folder really is, from Sonarr's or Radarr's search. Titles they don't manage
 * yet are added to them from the folder, unmonitored and with no search, so nothing is downloaded.
 */
export function FixMatchDialog({ item, path, locked, onFixed, onClose }: Props) {
  const arr = item.kind === 'Movie' ? 'Radarr' : 'Sonarr'
  const noun = item.kind === 'Movie' ? 'film' : 'show'
  const [draft, setDraft] = useState(item.title)
  const [query, setQuery] = useState(item.title)
  const [chosen, setChosen] = useState<MatchCandidate>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const results = useApi((signal) => searchMatches(item.id, query, signal), [item.id, query])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && !busy && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [busy, onClose])

  function search(e: FormEvent) {
    e.preventDefault()
    if (!draft.trim()) return
    setChosen(undefined)
    setQuery(draft.trim())
  }

  async function run(work: () => Promise<unknown>) {
    setBusy(true)
    setError(undefined)
    try {
      await work()
      onFixed()
      onClose()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      setBusy(false)
    }
  }

  const searchError = results.error instanceof ApiError && results.error.messages[0] ? results.error.messages[0] : results.error?.message

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel fix-match" role="dialog" aria-modal="true" aria-labelledby="fix-match-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="fix-match-title">Fix match</h2>
        <p className="muted">
          Which {noun} is {path ? <code>{path.split('/').at(-1)}</code> : item.title}? The one you choose stays matched, and {arr} manages it.
        </p>

        <form className="fix-match-search" onSubmit={search} role="search">
          <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} aria-label={`Search ${arr}`} autoFocus disabled={busy} />
          <button className="button quiet" type="submit" disabled={busy}>
            <Search /> Search
          </button>
        </form>

        <div className="fix-match-results" aria-busy={results.loading}>
          {results.loading && !results.data && <p className="muted">Searching {arr}…</p>}
          {searchError && <p className="error">{searchError}</p>}
          {results.data?.length === 0 && <p className="muted">Nothing found. Try the title as it was first released, or add the year.</p>}
          <ul role="listbox" aria-label="Results">
            {results.data?.map((c) => (
              <li key={c.id}>
                <button
                  type="button"
                  role="option"
                  aria-selected={chosen?.id === c.id}
                  className="fix-match-result"
                  disabled={busy || c.current}
                  onClick={() => setChosen(c)}
                >
                  {c.poster ? <img src={imageUrl(c.poster, 92)} alt="" loading="lazy" /> : <span className="fix-match-noposter" aria-hidden="true" />}
                  <span className="fix-match-text">
                    <strong>
                      {c.title}
                      {c.year && <span className="muted"> ({c.year})</span>}
                    </strong>
                    {c.network && <span className="muted">{c.network}</span>}
                    {c.overview && <span className="fix-match-overview">{c.overview}</span>}
                  </span>
                  <span className={c.current ? 'tag keep' : 'tag'}>{c.current ? 'Current match' : c.managedBy ? `In ${c.managedBy}` : `Adds to ${arr}`}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>

        {chosen && (
          <p className="fix-match-note" role="status">
            {chosen.managedBy
              ? `${chosen.managedBy} already has ${chosen.title}${chosen.managedPath ? ` (in ${chosen.managedPath})` : ''}. Flexarr takes its details from there.`
              : `${arr} will manage ${chosen.title} from this folder: added unmonitored and without a search, so nothing is downloaded.`}
          </p>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}

        <div className="form-actions">
          {locked && (
            <button className="text-button fix-match-reset" disabled={busy} onClick={() => run(() => resetMatch(item.id))}>
              Match automatically again
            </button>
          )}
          <button className="button quiet" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button className="button" disabled={!chosen || busy} onClick={() => chosen && run(() => fixMatch(item.id, chosen.id))}>
            {busy ? (chosen && !chosen.managedBy ? `Adding to ${arr}…` : 'Matching…') : chosen ? `Use ${chosen.title}${chosen.year ? ` (${chosen.year})` : ''}` : 'Choose one'}
          </button>
        </div>
      </div>
    </div>
  )
}
