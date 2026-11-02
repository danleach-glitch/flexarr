import { useState, type FormEvent } from 'react'
import { ApiError, fixBookMatch, fixMatch, getItem, imageUrl, resetMatch, searchBookMatches, searchMatches, type BookMatch, type MatchCandidate, type MediaKind } from '../api/client'
import { formatDuration } from '../lib/format'
import { arrFor } from '../lib/apps'
import { useApi } from '../hooks/useApi'
import { AsyncButton } from './AsyncButton'
import { Search } from './icons'
import { useModal } from '../hooks/useModal'

interface Props {
  item: { id: string; title: string; kind: string }
  onFixed: () => void
  onClose: () => void
}

/**
 * Choose which show or film a library folder really is, from Sonarr's or Radarr's search. Titles they don't manage
 * yet are added to them from the folder, unmonitored and with no search, so nothing is downloaded. Without them, the
 * search is TMDB's (films) or TVmaze's (shows), and the choice is only kept in Flexarr.
 */
export function FixMatchDialog(props: Props) {
  return props.item.kind === 'Audiobook' ? <BookMatchDialog {...props} /> : <TitleMatchDialog {...props} />
}

function TitleMatchDialog({ item, onFixed, onClose }: Props) {
  // Whose title it is: films are Radarr's and shows Sonarr's.
  const arr = arrFor(item.kind as MediaKind)
  const noun = item.kind === 'Movie' ? 'movie' : item.kind === 'Audiobook' ? 'book' : 'show'
  const [draft, setDraft] = useState(item.title)
  const [query, setQuery] = useState(item.title)
  const [chosen, setChosen] = useState<MatchCandidate>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const results = useApi((signal) => searchMatches(item.id, query, signal), [item.id, query])
  // Whose search this is: Sonarr's or Radarr's, which then manage the choice, or TMDB's or TVmaze's.
  const source = results.data?.[0]?.source || arr
  const managed = source === arr
  // Where it is on disk, so it's clear what's being matched: a film's files, or a show's folder.
  const detail = useApi((signal) => getItem(item.id, signal), [item.id])
  const paths = detail.data ? (item.kind === 'Movie' && detail.data.files.length > 0 ? detail.data.files.map((f) => f.path) : [detail.data.path].filter((p) => !!p)) : []
  const locked = detail.data?.matchLocked === true

  const dialog = useModal<HTMLDivElement>(onClose, !busy)

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
      <div className="panel fix-match" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="fix-match-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="fix-match-title">Fix match</h2>
        <p className="muted">
          Which {noun} is this? The one you choose stays matched{managed ? `, and ${arr} manages it` : `, with details from ${source}`}.
        </p>
        <ul className="fix-match-paths" aria-label={item.kind === 'Movie' ? 'Files' : 'Folder'}>
          {paths.map((p) => (
            <li key={p}>
              <code>{p}</code>
            </li>
          ))}
        </ul>

        <form className="fix-match-search" onSubmit={search} role="search">
          <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} aria-label={`Search ${source}`} autoFocus disabled={busy} />
          <AsyncButton className="button quiet" type="submit" disabled={busy} busy={results.loading}>
            <Search /> Search
          </AsyncButton>
        </form>

        <div className="fix-match-results" aria-busy={results.loading}>
          {results.loading && !results.data && <p className="muted">Searching…</p>}
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
                  {(c.current || managed) && (
                    <span className={c.current ? 'tag keep' : 'tag'}>{c.current ? 'Current match' : c.managedBy ? `In ${c.managedBy}` : `Adds to ${arr}`}</span>
                  )}
                </button>
              </li>
            ))}
          </ul>
        </div>

        {chosen && (
          <p className="fix-match-note" role="status">
            {!managed
              ? `Flexarr takes ${chosen.title}’s details from ${source}, and nothing is added anywhere.`
              : chosen.managedBy
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
            <AsyncButton className="text-button fix-match-reset" disabled={busy} onClick={() => run(() => resetMatch(item.id))}>
              Match automatically again
            </AsyncButton>
          )}
          <button className="button quiet" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <AsyncButton className="button" disabled={!chosen || busy} onClick={() => chosen && run(() => fixMatch(item.id, chosen.id))}>
            {chosen ? `Use ${chosen.title}${chosen.year ? ` (${chosen.year})` : ''}` : 'Choose one'}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}

/**
 * Which book this is, from the online providers (Audible, Apple Books, Open Library): searched by title and
 * author, and the one chosen stays matched, its details taking the place of what was found before.
 */
function BookMatchDialog({ item, onFixed, onClose }: Props) {
  const [draft, setDraft] = useState(item.title)
  const [author, setAuthor] = useState('')
  const [query, setQuery] = useState({ q: item.title, author: '' })
  const [chosen, setChosen] = useState<BookMatch>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const results = useApi((signal) => searchBookMatches(item.id, query.q, query.author || undefined, signal), [item.id, query])
  const dialog = useModal<HTMLDivElement>(onClose, !busy)
  const key = (m: BookMatch) => `${m.provider}:${m.id}`

  function search(e: FormEvent) {
    e.preventDefault()
    if (!draft.trim()) return
    setChosen(undefined)
    setQuery({ q: draft.trim(), author: author.trim() })
  }

  async function use(match: BookMatch) {
    setBusy(true)
    setError(undefined)
    try {
      await fixBookMatch(item.id, { provider: match.provider, id: match.id, query: query.q, author: query.author || undefined })
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
      <div className="panel fix-match" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="fix-match-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="fix-match-title">Fix match</h2>
        <p className="muted">Which book is this?</p>
        <form className="fix-match-search" onSubmit={search} role="search">
          <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} aria-label="Title" placeholder="Title" autoFocus disabled={busy} />
          <input type="search" value={author} onChange={(e) => setAuthor(e.target.value)} aria-label="Author" placeholder="Author" disabled={busy} />
          <AsyncButton className="button quiet" type="submit" disabled={busy} busy={results.loading}>
            <Search /> Search
          </AsyncButton>
        </form>
        <div className="fix-match-results" aria-busy={results.loading}>
          {results.loading && !results.data && <p className="muted">Searching…</p>}
          {searchError && <p className="error">{searchError}</p>}
          {results.data?.length === 0 && <p className="muted">Nothing found. Try fewer words, or the author.</p>}
          <ul role="listbox" aria-label="Results">
            {results.data?.map((m) => (
              <li key={key(m)}>
                <button type="button" role="option" aria-selected={chosen && key(chosen) === key(m)} className="fix-match-result" disabled={busy} onClick={() => setChosen(m)}>
                  {m.image ? <img src={imageUrl(m.image, 92)} alt="" loading="lazy" /> : <span className="fix-match-noposter" aria-hidden="true" />}
                  <span className="fix-match-text">
                    <strong>
                      {m.title}
                      {m.year && <span className="muted"> ({m.year})</span>}
                    </strong>
                    {m.subtitle && <span className="muted">{m.subtitle}</span>}
                    <span className="muted meta-line">
                      {[m.authors.join(', '), m.narrators.length ? `Read by ${m.narrators.join(', ')}` : null, m.series, m.durationSeconds ? formatDuration(m.durationSeconds) : null]
                        .filter((fact): fact is string => !!fact)
                        .map((fact) => (
                          <span key={fact}>{fact}</span>
                        ))}
                    </span>
                  </span>
                  <span className="tag">{providerName[m.provider] ?? m.provider}</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <AsyncButton className="button" disabled={!chosen || busy} onClick={() => chosen && use(chosen)}>
            {chosen ? `Use ${chosen.title}` : 'Choose one'}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}

const providerName: Record<string, string> = { audible: 'Audible', itunes: 'Apple Books', openlibrary: 'Open Library' }
