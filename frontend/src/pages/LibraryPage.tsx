import { useEffect, useRef, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router'
import { getLibraryBrowse, getLibraryGenres, getLibraryItems, scanLibrary, type ItemSummary, type Library } from '../api/client'
import { PosterCard } from '../components/Cards'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'

const PAGE_SIZE = 200
const LETTERS = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ']

const sorts = [
  { value: '', label: 'A–Z' },
  { value: 'added', label: 'Recently added' },
  { value: 'year', label: 'Newest first' },
  { value: 'rating', label: 'Highest rated' },
] as const

/**
 * Two ways into a library: Browse (a home page of rows for just this library) and All (every title, with sort,
 * genre and letter jumps). Links that sort or filter open All.
 */
export function LibraryPage() {
  const { id = '' } = useParams()
  const [params] = useSearchParams()
  const { libraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)
  if (!library) return <p className="page-message muted">{libraries.length ? 'This library no longer exists.' : 'Loading…'}</p>

  const all = library.type === 'Music' || params.get('view') === 'all' || params.has('sort') || params.has('genre')
  return all ? <LibraryAll key={id} /> : <LibraryBrowse key={id} library={library} />
}

function ViewSwitch({ library, all }: { library: Library; all: boolean }) {
  if (library.type === 'Music') return null
  return (
    <nav className="view-switch" aria-label={`${library.name} views`}>
      <Link to={`/library/${library.id}`} aria-current={all ? undefined : 'page'}>
        Browse
      </Link>
      <Link to={`/library/${library.id}?view=all`} aria-current={all ? 'page' : undefined}>
        {library.type === 'Movies' ? 'All films' : 'All shows'}
      </Link>
    </nav>
  )
}

function LibraryBrowse({ library }: { library: Library }) {
  const { data, error } = useApi((signal) => getLibraryBrowse(library.id, signal), [library.id, library.itemCount])
  const heading = (
    <>
      <h1 className="library-title">{library.name}</h1>
      <ViewSwitch library={library} all={false} />
    </>
  )

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="hero hero-loading" aria-busy="true" />
  if (data.rows.length === 0) {
    return (
      <div className="library-page">
        <header className="library-header">
          <div className="library-heading">{heading}</div>
        </header>
        <p className="page-message muted">{library.scan ? 'Titles appear here as the scan finds them.' : 'Nothing here yet.'}</p>
      </div>
    )
  }

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} top={heading} />
      <div className="rows">
        <HomeRows rows={data.rows} />
      </div>
    </>
  )
}

function LibraryAll() {
  const { id = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const sort = params.get('sort') ?? ''
  const genre = params.get('genre') ?? ''
  const { libraries, reload: reloadLibraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)
  const genres = useApi((signal) => getLibraryGenres(id, signal), [id])

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<ItemSummary[]>([])
  const [total, setTotal] = useState<number>()
  const [error, setError] = useState<string>()
  const grid = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250)
    return () => clearTimeout(timer)
  }, [search])

  // Reload when filters change, and as a scan adds items.
  const itemCount = library?.itemCount
  useEffect(() => {
    const controller = new AbortController()
    getLibraryItems(id, { search: query, sort, genre, take: PAGE_SIZE }, controller.signal)
      .then((page) => {
        setItems(page.items)
        setTotal(page.total)
        setError(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
  }, [id, query, sort, genre, itemCount])

  async function loadMore() {
    const page = await getLibraryItems(id, { search: query, sort, genre, skip: items.length, take: PAGE_SIZE })
    setItems((current) => [...current, ...page.items])
    setTotal(page.total)
  }

  /** Loads everything up to the letter if needed, then scrolls to its first title. */
  async function jump(letter: string) {
    const startsWith = (item: ItemSummary) => {
      const first = item.title.replace(/^(the|a|an)\s+/i, '').charAt(0).toUpperCase()
      return letter === '#' ? !/[A-Z]/.test(first) : first === letter
    }
    let loaded = items
    while (!loaded.some(startsWith) && total !== undefined && loaded.length < total) {
      const page = await getLibraryItems(id, { search: query, sort, genre, skip: loaded.length, take: PAGE_SIZE })
      loaded = [...loaded, ...page.items]
      setItems(loaded)
      if (page.items.length === 0) break
    }
    const target = loaded.find(startsWith)
    if (target) {
      requestAnimationFrame(() => {
        const el = grid.current?.querySelector<HTMLElement>(`[data-id="${target.id}"] a`)
        el?.scrollIntoView({ behavior: 'smooth', block: 'center' })
        el?.focus({ preventScroll: true })
      })
    }
  }

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  if (!library) return null

  return (
    <div className="library-page">
      <header className="library-header">
        <div>
          <div className="library-heading">
            <h1>{library.name}</h1>
            <ViewSwitch library={library} all />
          </div>
          <p className="muted">
            {(total ?? library.itemCount).toLocaleString()} {query || genre ? 'matching' : { Movies: 'films', TvShows: 'shows', Music: 'artists' }[library.type]}
          </p>
        </div>
        <div className="library-tools">
          <input type="search" placeholder={`Search ${library.name}`} aria-label={`Search ${library.name}`} value={search} onChange={(e) => setSearch(e.target.value)} />
          <select aria-label="Sort by" value={sort} onChange={(e) => setParam('sort', e.target.value)}>
            {sorts.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </select>
          <button
            className="button quiet small"
            disabled={!!library.scan}
            onClick={() => scanLibrary(library.id).then(() => setTimeout(reloadLibraries, 2500))}
          >
            Scan for changes
          </button>
        </div>
      </header>

      {genres.data && genres.data.length > 0 && (
        <div className="genre-chips" role="group" aria-label="Filter by genre">
          <button className="chip" aria-pressed={!genre} onClick={() => setParam('genre', '')}>
            All
          </button>
          {genres.data.slice(0, 16).map((g) => (
            <button key={g.name} className="chip" aria-pressed={genre === g.name} onClick={() => setParam('genre', genre === g.name ? '' : g.name)}>
              {g.name}
            </button>
          ))}
        </div>
      )}

      {library.scan && <ScanProgress scan={library.scan} />}
      {error && <p className="error">{error}</p>}
      {items.length === 0 && total === 0 && (
        <p className="page-message muted">
          {query ? `Nothing in ${library.name} matches “${query}”.` : library.scan ? 'Titles appear here as the scan finds them.' : 'Nothing here yet.'}
        </p>
      )}

      <div className="library-body">
        <div className="poster-grid" ref={grid}>
          {items.map((item) => (
            <div key={item.id} data-id={item.id}>
              <PosterCard item={item} />
            </div>
          ))}
        </div>
        {!sort && !query && items.length > 40 && (
          <nav className="alpha-rail" aria-label="Jump to letter">
            {LETTERS.map((l) => (
              <button key={l} onClick={() => jump(l)}>
                {l}
              </button>
            ))}
          </nav>
        )}
      </div>

      {total !== undefined && items.length < total && (
        <div className="load-more">
          <button className="button quiet" onClick={loadMore}>
            Show {Math.min(PAGE_SIZE, total - items.length)} more
          </button>
        </div>
      )}
    </div>
  )
}
