import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigationType, useParams, useSearchParams } from 'react-router'
import { freshly, getItem, getLibraryBrowse, getLibraryGenres, getLibraryItems, keepUntilChanged, kept, scanLibrary, type ItemSummary, type Library } from '../api/client'
import { PosterCard } from '../components/Cards'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'

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

/** `value`, but changing at most once every `ms` (0: straight away); the latest value always lands in the end. */
function useThrottled<T>(value: T, ms: number): T {
  const [shown, setShown] = useState(value)
  const last = useRef(0)
  useEffect(() => {
    if (Object.is(value, shown)) return
    const timer = setTimeout(
      () => {
        last.current = Date.now()
        setShown(value)
      },
      Math.max(0, ms - (Date.now() - last.current)),
    )
    return () => clearTimeout(timer)
  }, [value, shown, ms])
  return shown
}

function LibraryBrowse({ library }: { library: Library }) {
  // A scan changes the count every few seconds; these rows are heavy, so refresh them at most every 30 seconds while
  // it runs, and once more when it's done.
  const itemCount = useThrottled(library.itemCount, library.scan ? 30_000 : 0)
  const { data, error, reload } = useApi((signal) => getLibraryBrowse(library.id, signal), [library.id, itemCount])
  useWatchedChanges(reload)
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

const JUMP_PAGE = 500
const JUMP_PARALLEL = 4

/** A title's letter on the A–Z rail, ignoring a leading "The", "A" or "An" as the sort does. */
function letterOf(item: ItemSummary) {
  const first = item.title.replace(/^(the|a|an)\s+/i, '').charAt(0).toUpperCase()
  return /[A-Z]/.test(first) ? first : '#'
}

/** What going back to a grid restores: the titles it had loaded and where it was scrolled to. */
interface GridSnapshot {
  items: ItemSummary[]
  total: number | undefined
  itemCount: number | undefined
  scrollY: number
}

/** Adds pages to a grid, skipping titles it already has (a scan can shift what's on each page). */
function append(loaded: ItemSummary[], pages: ItemSummary[][]) {
  const seen = new Set(loaded.map((i) => i.id))
  return [...loaded, ...pages.flat().filter((i) => !seen.has(i.id))]
}

function LibraryAll() {
  const { id = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const sort = params.get('sort') ?? ''
  const genre = params.get('genre') ?? ''
  const { libraries, reload: reloadLibraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)
  const genres = useApi((signal) => getLibraryGenres(id, signal), [id])

  // Coming back (Back from a title) picks up where you were, unless something changed meanwhile.
  const snapshotKey = `library-all ${id} ${sort} ${genre}`
  const navigation = useNavigationType()
  const [restored] = useState(() => (navigation === 'POP' ? kept<GridSnapshot>(snapshotKey) : undefined))

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<ItemSummary[]>(restored?.items ?? [])
  const [total, setTotal] = useState<number | undefined>(restored?.total)
  // The library's size when the grid loaded, to notice titles a scan adds since.
  const [loadedCount, setLoadedCount] = useState<number | undefined>(restored?.itemCount)
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)
  // Bumped whenever the grid starts over, so pages still arriving for the old one are dropped.
  const generation = useRef(0)
  const grid = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250)
    return () => clearTimeout(timer)
  }, [search])

  // While every title fits on one page, nothing is lost by starting over, so titles a scan finds just appear (the
  // count is followed during render, React's way to adjust state to a prop). Beyond that, starting over would throw
  // away "Show more" pages and the scroll position: offer it instead.
  const small = total !== undefined && total < PAGE_SIZE && items.length >= total
  const grown = library !== undefined && loadedCount !== undefined && library.itemCount !== loadedCount
  const [followed, setFollowed] = useState(library?.itemCount)
  if (small && library && library.itemCount !== followed) {
    setFollowed(library.itemCount)
    setVersion((v) => v + 1)
  }

  const depsKey = `${id}|${query}|${sort}|${genre}|${version}`
  const restoredFor = useRef(restored ? depsKey : null)
  useEffect(() => {
    if (restoredFor.current === depsKey) return
    restoredFor.current = null
    const run = ++generation.current
    const countAtLoad = library?.itemCount
    const controller = new AbortController()
    const load = () => getLibraryItems(id, { search: query, sort, genre, take: PAGE_SIZE }, controller.signal)
    // Starting over on purpose (Refresh, or new titles) skips any cached first page.
    const pending = version ? freshly(load) : load()
    pending
      .then((page) => {
        if (run !== generation.current) return
        setItems(page.items)
        setTotal(page.total)
        setLoadedCount(countAtLoad)
        setError(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- depsKey stands for the inputs; the count is read as it is
  }, [depsKey])

  // Marking something watched updates just the titles it touches, in place.
  useWatchedChanges(
    useCallback(
      (ids: string[]) => {
        const run = generation.current
        for (const changed of ids.filter((i) => items.some((item) => item.id === i))) {
          getItem(changed)
            .then((detail) => run === generation.current && setItems((current) => current.map((item) => (item.id === changed ? detail.item : item))))
            .catch(() => undefined)
        }
      },
      [items],
    ),
  )

  // Remember the grid on the way out, and scroll back to where it was when it's restored.
  const latest = useRef<GridSnapshot>(undefined)
  useEffect(() => {
    latest.current = { items, total, itemCount: loadedCount, scrollY: 0 }
  })
  useEffect(() => {
    if (restored) requestAnimationFrame(() => window.scrollTo(0, restored.scrollY))
    return () => {
      if (latest.current && !query) keepUntilChanged(snapshotKey, { ...latest.current, scrollY: window.scrollY })
    }
  }, [restored, query, snapshotKey])

  async function loadMore() {
    if (busy) return
    const run = generation.current
    setBusy(true)
    try {
      const page = await getLibraryItems(id, { search: query, sort, genre, skip: items.length, take: PAGE_SIZE })
      if (run !== generation.current) return
      setItems((current) => append(current, [page.items]))
      setTotal(page.total)
    } catch (e) {
      if (run === generation.current) setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  /** Loads everything up to the letter if needed (a few big pages at a time), then scrolls to its first title. */
  async function jump(letter: string) {
    if (busy) return
    const run = generation.current
    // Sorted A–Z, so once the grid ends past the letter there's nothing with it to find.
    const passed = (loaded: ItemSummary[]) => {
      const last = loaded.length ? letterOf(loaded[loaded.length - 1]) : '#'
      return letter !== '#' && last !== '#' && last > letter
    }
    let loaded = items
    setBusy(true)
    try {
      while (!loaded.some((i) => letterOf(i) === letter) && !passed(loaded) && total !== undefined && loaded.length < total) {
        const skips: number[] = []
        for (let skip = loaded.length; skip < total && skips.length < JUMP_PARALLEL; skip += JUMP_PAGE) skips.push(skip)
        const pages = await Promise.all(skips.map((skip) => getLibraryItems(id, { search: query, sort, genre, skip, take: JUMP_PAGE })))
        if (run !== generation.current) return
        const next = append(loaded, pages.map((p) => p.items))
        if (next.length === loaded.length) break
        loaded = next
        setItems(loaded)
      }
    } catch (e) {
      if (run === generation.current) setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
    const target = loaded.find((i) => letterOf(i) === letter)
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
      {grown && !small && (
        <p className="library-new" role="status">
          {library.itemCount > (loadedCount ?? 0) ? 'New titles have arrived.' : 'Some titles have gone.'}{' '}
          <button className="button quiet small" onClick={() => setVersion((v) => v + 1)}>
            Refresh
          </button>
        </p>
      )}
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
              <button key={l} onClick={() => jump(l)} disabled={busy}>
                {l}
              </button>
            ))}
          </nav>
        )}
      </div>

      {total !== undefined && items.length < total && (
        <div className="load-more">
          <button className="button quiet" onClick={loadMore} disabled={busy}>
            Show {Math.min(PAGE_SIZE, total - items.length)} more
          </button>
        </div>
      )}
    </div>
  )
}
