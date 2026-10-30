import { useEffect, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { getWatchlist, imageUrl, type WatchlistEntry, type WatchlistStatus } from '../api/client'
import { PosterCard } from '../components/Cards'
import { RequestButton } from '../components/RequestButton'
import { WatchlistButton } from '../components/WatchlistButton'
import { useApi } from '../hooks/useApi'

const STATUSES: [WatchlistStatus | '', string][] = [
  ['', 'Everything'],
  ['Ready', 'Ready to watch'],
  ['OnItsWay', 'On its way'],
  ['Coming', 'Coming'],
  ['NotAvailable', 'Not available'],
]

type Sort = 'status' | 'added' | 'release' | 'title'

/** Ready first, then what's on its way, then what's coming, then the rest. */
const statusOrder: Record<WatchlistStatus, number> = { Ready: 0, OnItsWay: 1, Coming: 2, NotAvailable: 3 }

const releaseOf = (e: WatchlistEntry) => e.item?.releaseDate ?? e.releaseDate ?? (e.year ? `${e.year}-01-01` : '9999')

const sorters: Record<Sort, (a: WatchlistEntry, b: WatchlistEntry) => number> = {
  status: (a, b) => (statusOrder[a.status] ?? 4) - (statusOrder[b.status] ?? 4) || b.addedAt.localeCompare(a.addedAt),
  added: (a, b) => b.addedAt.localeCompare(a.addedAt),
  release: (a, b) => releaseOf(a).localeCompare(releaseOf(b)),
  title: (a, b) => a.title.localeCompare(b.title, undefined, { sensitivity: 'base', ignorePunctuation: true }),
}

/** The profile's watchlist: what's ready to play, and where everything else has got to. */
export function WatchlistPage() {
  const [params, setParams] = useSearchParams()
  const status = (params.get('status') ?? '') as WatchlistStatus | ''
  const kind = params.get('kind') ?? ''
  const sort = (params.get('sort') ?? 'status') as Sort
  // Taking something off (here, or on another page) reloads the list.
  const [version, setVersion] = useState(0)
  useEffect(() => {
    const changed = () => setVersion((v) => v + 1)
    window.addEventListener('flexarr:watchlist', changed)
    return () => window.removeEventListener('flexarr:watchlist', changed)
  }, [])
  const { data, error } = useApi(getWatchlist, [version])
  // A card taken off by keyboard: focus moves to the bookmark of the card that takes its place.
  const focusAfter = useRef<number | null>(null)
  useEffect(() => {
    const at = focusAfter.current
    if (at === null || !data) return
    focusAfter.current = null
    const toggles = document.querySelectorAll<HTMLButtonElement>('.watchlist-page .watchlist-toggle')
    toggles[Math.min(at, toggles.length - 1)]?.focus()
  }, [data])

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const shown = (data ?? [])
    .filter((e) => !status || e.status === status)
    .filter((e) => !kind || e.mediaType === kind)
    .sort(sorters[sort] ?? sorters.status)

  return (
    <div className="watchlist-page">
      <header className="library-header">
        <h1>Watchlist</h1>
        {data && data.length > 0 && (
          <>
            <div className="genre-chips" role="group" aria-label="Show">
              {STATUSES.map(([value, label]) => (
                <button key={value} className="chip" aria-pressed={status === value} onClick={() => setParam('status', value)}>
                  {label}
                </button>
              ))}
            </div>
            <div className="watchlist-tools">
              <div className="genre-chips" role="group" aria-label="Kind">
                {[
                  ['', 'Films and shows'],
                  ['movie', 'Films'],
                  ['tv', 'Shows'],
                ].map(([value, label]) => (
                  <button key={value} className="chip" aria-pressed={kind === value} onClick={() => setParam('kind', value)}>
                    {label}
                  </button>
                ))}
              </div>
              <select aria-label="Sort by" value={sort} onChange={(e) => setParam('sort', e.target.value === 'status' ? '' : e.target.value)}>
                <option value="status">Ready first</option>
                <option value="added">Recently added</option>
                <option value="release">Release date</option>
                <option value="title">Title</option>
              </select>
            </div>
          </>
        )}
      </header>
      {error && <p className="error">{error.message}</p>}
      {!data && !error && <p className="page-message muted">Loading…</p>}
      {data && data.length === 0 && <p className="muted">Nothing here yet.</p>}
      {data && data.length > 0 && shown.length === 0 && <p className="muted">Nothing on your watchlist matches.</p>}
      <div className="poster-grid">
        {shown.map((entry, index) => (
          <WatchlistCard key={entry.id} entry={entry} onRemoved={() => (focusAfter.current = index)} />
        ))}
      </div>
    </div>
  )
}

const statusLabel = (entry: WatchlistEntry) => {
  switch (entry.status) {
    case 'OnItsWay':
      return 'On its way'
    case 'Coming':
      return entry.releaseDate ? `Out ${new Date(entry.releaseDate).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })}` : 'Coming'
    default:
      return null
  }
}

/** A title ready to play is its usual card; anything else says where it's got to, with a request when nobody has asked. */
function WatchlistCard({ entry, onRemoved }: { entry: WatchlistEntry; onRemoved: () => void }) {
  const key = entry.tmdbId ? { mediaType: entry.mediaType, tmdbId: entry.tmdbId } : entry.libraryItemId ? { libraryItemId: entry.libraryItemId } : null
  const remove = key && <WatchlistButton compact titleKey={key} title={entry.title} on onRemoved={onRemoved} />

  if (entry.item)
    return (
      <div className="watchlist-card">
        <PosterCard item={entry.item} />
        <div className="external-card-actions watchlist-ready-actions">{remove}</div>
      </div>
    )

  const label = statusLabel(entry)
  return (
    <div className="card external-card watchlist-card">
      <Link to={entry.tmdbId ? `/discover/${entry.mediaType}/${entry.tmdbId}` : '#'} aria-label={`${entry.title}, more about it`}>
        <div className="card-frame">
          <div className="artwork artwork-poster">
            {entry.poster ? <img src={imageUrl(entry.poster, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{entry.title}</span>}
          </div>
        </div>
      </Link>
      <span className="card-title">{entry.title}</span>
      <span className="card-subtitle">{[entry.mediaType === 'tv' ? 'Show' : 'Film', entry.year].filter(Boolean).join(', ')}</span>
      {entry.streaming && entry.streaming.length > 0 && (
        <span className="card-subtitle watchlist-streaming" title={entry.streaming.join(', ')}>
          {entry.streaming.length === 1 ? `On ${entry.streaming[0]}` : entry.streaming.join(', ')}
        </span>
      )}
      <div className="external-card-actions">
        {label ? (
          <span className="external-state">{label}</span>
        ) : (
          entry.tmdbId && <RequestButton title={{ mediaType: entry.mediaType, tmdbId: entry.tmdbId, availability: 'None', libraryItemId: null }} />
        )}
        {remove}
      </div>
    </div>
  )
}
