import { useState, type DragEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  ApiError,
  createCollection,
  deleteCollection,
  getCollection,
  getCollections,
  getCollectionSuggestions,
  imageUrl,
  removeFromCollection,
  reorderCollection,
  updateCollection,
  type CollectionSuggestion,
  type CollectionSummary,
  type ItemSummary,
} from '../api/client'
import { AsyncButton, IconButton } from '../components/AsyncButton'
import { PosterCard } from '../components/Cards'
import { CollectionEditor } from '../components/CollectionEditor'
import { EyeOff, Pencil, Play, Trash } from '../components/icons'
import { RequestButton } from '../components/RequestButton'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { usePlay } from '../hooks/usePlay'

/** All collections: suggestions to add, then yours, the household's and the franchises Radarr knows. */
export function CollectionsPage() {
  const [search] = useSearchParams()
  const suggested = search.get('view') === 'suggested'
  const collections = useApi(getCollections, [])
  // Only worked out when that tab is open (they take the server a moment).
  const suggestions = useApi((signal) => (suggested ? getCollectionSuggestions(signal) : Promise.resolve(null)), [suggested, collections.data])
  const { me } = useSession()
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()
  // The main profile, locked or not: household collections ask for the PIN when it's locked (see UnlockPrompt).
  const isAdmin = (me?.isAdmin || me?.canUnlock) ?? false

  const all = collections.data ?? []
  const sections = [
    { title: 'Yours', items: all.filter((c) => c.personal) },
    { title: 'For everyone', items: all.filter((c) => !c.personal && c.kind !== 'Franchise') },
    { title: 'Franchises', items: all.filter((c) => c.kind === 'Franchise') },
  ]

  async function adopt(suggestion: CollectionSuggestion) {
    const created = await createCollection({ name: suggestion.name, description: suggestion.description, rules: suggestion.rules, personal: !isAdmin })
    navigate(`/collection/${created.id}`)
  }

  return (
    <div className="collections-page">
      <header className="library-header">
        <div>
          <h1>Collections</h1>
          <p className="muted">Your own, smart ones that keep themselves up to date, and the film series you have.</p>
        </div>
        <button className="button" onClick={() => setCreating(true)}>
          New collection
        </button>
      </header>

      <nav className="view-switch" aria-label="Collections views">
        <Link to="/collections" aria-current={suggested ? undefined : 'page'}>
          Your collections
        </Link>
        <Link to="/collections?view=suggested" aria-current={suggested ? 'page' : undefined}>
          Suggested
        </Link>
      </nav>

      {suggested && (
        <section className="collections-section">
          <p className="muted">Ideas from what’s in your library. Adding one makes a smart collection that keeps itself up to date.</p>
          {!suggestions.data && <p className="muted">Looking through your library…</p>}
          {suggestions.data?.length === 0 && <p className="muted">No suggestions right now. They change as your library grows and through the year.</p>}
          <div className="collection-suggestions">
            {suggestions.data?.map((s) => (
              <div key={s.name} className="collection-suggestion">
                <Collage posters={s.posters} title={s.name} />
                <div>
                  <strong>{s.name}</strong>
                  <span className="muted">{s.description}</span>
                </div>
                <AsyncButton className="button small" onClick={() => adopt(s)}>
                  Add
                </AsyncButton>
              </div>
            ))}
          </div>
        </section>
      )}

      {!suggested && collections.error && <p className="error">{collections.error.message}</p>}
      {!suggested && sections.map(
        (section) =>
          section.items.length > 0 && (
            <section key={section.title} className="collections-section">
              <h2 className="settings-subhead">
                {section.title} <span className="health-count">{section.items.length}</span>
              </h2>
              <div className="poster-grid">
                {section.items.map((c) => (
                  <CollectionCard key={c.id} collection={c} />
                ))}
              </div>
            </section>
          ),
      )}
      {!suggested && collections.data && all.length === 0 && (
        <p className="muted collection-empty">
          No collections yet. Make one, or pick one from <Link to="/collections?view=suggested">Suggested</Link>.
        </p>
      )}

      {creating && (
        <CollectionEditor
          isAdmin={isAdmin}
          onClose={() => setCreating(false)}
          onSaved={(saved) => {
            setCreating(false)
            navigate(`/collection/${saved.id}`)
          }}
        />
      )}
    </div>
  )
}

function CollectionCard({ collection: c }: { collection: CollectionSummary }) {
  return (
    <Link to={`/collection/${c.id}`} className="card collection-card">
      <div className="card-frame">{c.poster ? <img className="collection-poster" src={imageUrl(c.poster, 342)} alt="" loading="lazy" /> : <Collage posters={c.posters} title={c.name} />}</div>
      <span className="card-title">{c.name}</span>
      <span className="card-subtitle">{describe(c)}</span>
    </Link>
  )
}

/** "12 of 25 watched", "Smart, 10 titles". */
function describe(c: CollectionSummary) {
  const count = `${c.itemCount} ${c.itemCount === 1 ? 'title' : 'titles'}`
  if (c.kind === 'Smart') return `Smart, ${count}`
  return c.watchedCount > 0 ? `${c.watchedCount} of ${c.itemCount} watched` : count
}

/** Artwork from a collection's titles: four posters in a grid, or as many as there are. */
function Collage({ posters, title }: { posters: string[]; title: string }) {
  if (posters.length === 0) return <div className="collage collage-empty">{title}</div>
  const shown = posters.length >= 4 ? posters.slice(0, 4) : posters.slice(0, 1)
  return (
    <div className={shown.length === 4 ? 'collage collage-four' : 'collage'} aria-hidden="true">
      {shown.map((p, i) => (
        <img key={i} src={imageUrl(p, 342)} alt="" loading="lazy" />
      ))}
    </div>
  )
}

/** One collection: what's in it, what's next to watch, and (for a franchise) what's missing. */
export function CollectionPage() {
  const { id = '' } = useParams()
  const [version, setVersion] = useState(0)
  const { data, error } = useApi((signal) => getCollection(id, signal), [id, version])
  const { me } = useSession()
  const [editing, setEditing] = useState(false)
  const [dragging, setDragging] = useState<string>()
  const [order, setOrder] = useState<ItemSummary[]>()
  const [message, setMessage] = useState<string>()
  const navigate = useNavigate()
  const play = usePlay()
  const reload = () => {
    setOrder(undefined)
    setVersion((v) => v + 1)
  }

  if (error) return <p className="page-message error">{error.message.includes('404') ? 'That collection isn’t here any more.' : error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  const c = data.summary
  const items = order ?? data.items
  const manual = c.kind === 'Manual' && c.canEdit
  const next = items.find((i) => !i.userData.watched) ?? items[0]
  const kindLabel = { Manual: 'Collection', Smart: 'Smart collection', Franchise: 'Film series', TraktList: 'Trakt list' }[c.kind]

  async function act(work: () => Promise<unknown>) {
    try {
      await work()
      reload()
    } catch (e) {
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    }
  }

  // Drag a poster onto another to move it there.
  function drop(e: DragEvent, target: string) {
    e.preventDefault()
    if (!dragging || dragging === target) return
    const moved = [...items]
    const from = moved.findIndex((i) => i.id === dragging)
    const [item] = moved.splice(from, 1)
    moved.splice(
      moved.findIndex((i) => i.id === target),
      0,
      item,
    )
    setOrder(moved)
    setDragging(undefined)
    void reorderCollection(c.id, moved.map((i) => i.id))
  }

  return (
    <article className="collection-page">
      {data.backdrop && (
        <div className="collection-backdrop" aria-hidden="true">
          <img src={imageUrl(data.backdrop, 1920)} alt="" />
        </div>
      )}
      <header className="collection-header">
        <div className="collection-art">{c.poster ? <img src={imageUrl(c.poster, 500)} alt="" /> : <Collage posters={c.posters} title={c.name} />}</div>
        <div className="collection-text">
          <p className="detail-kicker">
            <Link to="/collections">Collections</Link> <span className="muted">{kindLabel}</span>
          </p>
          <h1 className="detail-title">{c.name}</h1>
          <p className="facts">
            <span>
              {c.itemCount} {c.itemCount === 1 ? 'title' : 'titles'}
            </span>
            {c.watchedCount > 0 && <span>{c.watchedCount} watched</span>}
            {data.missing.length > 0 && <span>{data.missing.length} not in your library</span>}
            {c.kind === 'TraktList' ? <span>From Trakt</span> : c.personal && <span>Just for you</span>}
          </p>
          {c.description && <p className="detail-overview">{c.description}</p>}
          <div className="detail-actions">
            {next && (
              <AsyncButton className="button" onClick={() => (next.kind === 'Series' ? play(next) : navigate(`/play/${next.id}`))}>
                <Play /> {next.userData.watched ? 'Play' : `Play ${next.title}`}
              </AsyncButton>
            )}
            {items.length > 1 && (
              <AsyncButton
                className="button quiet"
                onClick={() => {
                  const pool = items.filter((i) => !i.userData.watched)
                  const pick = (pool.length ? pool : items)[Math.floor(Math.random() * (pool.length || items.length))]
                  if (pick.kind === 'Series') return play(pick)
                  navigate(`/play/${pick.id}`)
                }}
              >
                Shuffle
              </AsyncButton>
            )}
            {c.canEdit && (
              <AsyncButton className="button quiet" aria-pressed={c.showOnHome} onClick={() => act(() => updateCollection(c.id, { showOnHome: !c.showOnHome }))}>
                {c.showOnHome ? 'On Home' : 'Show on Home'}
              </AsyncButton>
            )}
            {c.canEdit && c.kind !== 'Franchise' && c.kind !== 'TraktList' && (
              <IconButton label="Edit" icon={<Pencil />} className="button quiet icon" onClick={() => setEditing(true)} />
            )}
            {c.canEdit && (
              <IconButton
                label={c.kind === 'Franchise' ? 'Hide' : 'Delete'}
                icon={c.kind === 'Franchise' ? <EyeOff /> : <Trash />}
                className="button quiet icon danger-icon"
                onClick={() => {
                  if (!confirm(c.kind === 'Franchise' ? `Hide ${c.name}? It won’t come back with the next sync.` : c.kind === 'TraktList' ? `Stop showing ${c.name}? It stays on Trakt.` : `Delete ${c.name}? The titles stay in your library.`)) return
                  return deleteCollection(c.id).then(() => navigate('/collections', { replace: true }))
                }}
              />
            )}
          </div>
          {message && (
            <p className="error" role="alert">
              {message}
            </p>
          )}
        </div>
      </header>

      {items.length === 0 && (
        <p className="muted collection-empty">
          {c.kind === 'Smart' ? 'Nothing matches its rules yet.' : c.kind === 'TraktList' ? 'Nothing on this list is here yet.' : 'Nothing in it yet. Add titles from their ⋯ menu or their page: Add to collection.'}
        </p>
      )}
      {manual && items.length > 1 && <p className="muted collection-hint">Drag posters to reorder.</p>}
      <div className="poster-grid">
        {items.map((item) => (
          <div
            key={item.id}
            className={manual ? (dragging === item.id ? 'collection-item dragging' : 'collection-item') : undefined}
            draggable={manual}
            onDragStart={() => setDragging(item.id)}
            onDragOver={(e) => manual && e.preventDefault()}
            onDrop={(e) => drop(e, item.id)}
          >
            <PosterCard item={item} />
            {manual && (
              <AsyncButton className="text-button collection-remove" onClick={() => act(() => removeFromCollection(c.id, item.id))}>
                Remove
              </AsyncButton>
            )}
          </div>
        ))}
      </div>

      {data.missing.length > 0 && (
        <section className="collections-section">
          <h2 className="settings-subhead">Not in your library</h2>
          <div className="poster-grid">
            {data.missing.map((m) => (
              <div key={m.tmdbId} className="card external-card missing-card">
                <Link to={`/discover/movie/${m.tmdbId}`} aria-label={`${m.title}, more about it`}>
                  <div className="card-frame">
                    <div className="artwork artwork-poster">{m.poster ? <img src={imageUrl(m.poster, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{m.title}</span>}</div>
                  </div>
                </Link>
                <span className="card-title">{m.title}</span>
                <span className="card-subtitle">{m.year}</span>
                <RequestButton title={{ mediaType: 'movie', tmdbId: m.tmdbId, availability: 'None', libraryItemId: null }} />
              </div>
            ))}
          </div>
        </section>
      )}

      {editing && (
        <CollectionEditor
          isAdmin={(me?.isAdmin || me?.canUnlock) ?? false}
          collection={c}
          rules={data.rules}
          onClose={() => setEditing(false)}
          onSaved={() => {
            setEditing(false)
            reload()
          }}
        />
      )}
    </article>
  )
}
