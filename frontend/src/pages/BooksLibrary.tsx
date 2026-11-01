import { Fragment, useEffect, useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router'
import {
  getBookGenres,
  getBookLanguages,
  getBookPeople,
  getBookSeriesList,
  getBookShelves,
  getLibraryBooks,
  getPerson,
  scanLibrary,
  type BookFilters,
  type ItemSummary,
  type Library,
  type Paged,
} from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { BookPersonCard, BookSeriesCard } from '../components/BookCards'
import { PosterCard } from '../components/Cards'
import { ChevronLeft } from '../components/icons'
import { Row } from '../components/Row'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'
import { languageOptions } from '../lib/books'

const PAGE_SIZE = 60

type View = 'browse' | 'all' | 'series' | 'authors' | 'genres' | 'narrators' | 'genre'

/** The same views, in the same order, as the iPhone and Android apps. */
const views: { view: View; label: string }[] = [
  { view: 'browse', label: 'Browse' },
  { view: 'all', label: 'All books' },
  { view: 'genres', label: 'Genres' },
  { view: 'series', label: 'Series' },
  { view: 'authors', label: 'Authors' },
]

/** Views without a pill, still opened by their address: one genre's books has a page of its own. */
const hidden: View[] = ['narrators', 'genre']

/**
 * An audiobooks library: shelves (Browse), every book with filters and sorts (All), and its series, authors and
 * genres (narrators too, by address only). Covers are square.
 */
export function BooksLibrary({ library }: { library: Library }) {
  const [params] = useSearchParams()
  const asked = params.get('view')
  const asking: View = views.some((v) => v.view === asked) || hidden.includes(asked as View) ? (asked as View) : params.has('sort') || params.has('genre') ? 'all' : 'browse'
  const genre = params.get('genre')
  const view: View = asking === 'genre' && !genre ? 'genres' : asking
  if (view === 'genre' && genre) return <GenrePage library={library} genre={genre} />
  return (
    <div className="library-page books-library">
      <header className="library-header">
        <div className="library-heading">
          <h1 className="library-title">{library.name}</h1>
          <nav className="view-switch" aria-label={`${library.name} views`}>
            {views.map((v) => (
              <Link key={v.view} to={v.view === 'browse' ? `/library/${library.id}` : `/library/${library.id}?view=${v.view}`} aria-current={view === v.view ? 'page' : undefined}>
                {v.label}
              </Link>
            ))}
          </nav>
        </div>
      </header>
      {library.scan && <ScanProgress scan={library.scan} />}
      {view === 'browse' && <Shelves key={library.id} library={library} />}
      {view === 'all' && <AllBooks key={library.id} library={library} view="all" />}
      {view === 'series' && <SeriesList key={library.id} library={library} />}
      {view === 'genres' && <Genres key={library.id} library={library} />}
      {(view === 'authors' || view === 'narrators') && <People key={`${library.id}-${view}`} library={library} role={view === 'authors' ? 'author' : 'narrator'} />}
    </div>
  )
}

function Shelves({ library }: { library: Library }) {
  const { data, error, reload } = useApi((signal) => getBookShelves(library.id, signal), [library.id, library.itemCount])
  useWatchedChanges(reload)
  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="rows" aria-busy="true" />
  const all = `/library/${library.id}?view=all`
  const empty = [data.continueListening, data.nextInSeries, data.recentlyAdded, data.finished].every((s) => s.length === 0) && data.series.length === 0
  if (empty) return <p className="page-message muted">{library.scan ? 'Books appear here as the scan finds them.' : 'No books yet.'}</p>
  const books = (title: string, items: ItemSummary[], link?: string) =>
    items.length > 0 && (
      <Row title={title} link={link}>
        {items.map((item) => (
          <PosterCard key={item.id} item={item} />
        ))}
      </Row>
    )
  const shelves: Record<string, ReactNode> = {
    continue: books('Continue listening', data.continueListening, `${all}&progress=inprogress&sort=listened`),
    next: books('Next in series', data.nextInSeries),
    recent: books('Recently added', data.recentlyAdded, `${all}&sort=added`),
    series: data.series.length > 0 && (
      <Row title="Series" link={`/library/${library.id}?view=series`}>
        {data.series.map((s) => (
          <BookSeriesCard key={s.id} series={s} />
        ))}
      </Row>
    ),
    authors: data.authors.length > 0 && (
      <Row title="Authors" link={`/library/${library.id}?view=authors`} style="people">
        {data.authors.map((p) => (
          <BookPersonCard key={p.id} person={p} />
        ))}
      </Row>
    ),
    finished: books('Finished', data.finished, `${all}&progress=finished`),
  }
  // In the profile's order, without the ones it switched off (Settings › Home & browse).
  const order = (data.order ?? Object.keys(shelves)).filter((id) => id in shelves)
  // None left with anything on it: the whole library instead.
  if (!order.some((id) => shelves[id])) return <AllBooks library={library} />
  return (
    <div className="book-shelves">
      {order.map((id) => (
        <Fragment key={id}>{shelves[id]}</Fragment>
      ))}
    </div>
  )
}

/** The library's genres, A–Z, each opening a page of its books. */
function Genres({ library }: { library: Library }) {
  const { data, error } = useApi((signal) => getBookGenres(library.id, signal), [library.id, library.itemCount])
  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="chips" aria-busy="true" />
  if (data.length === 0) return <p className="page-message muted">No genres here yet.</p>
  const genres = [...data].sort((a, b) => a.name.localeCompare(b.name))
  return (
    <nav className="chips book-genres" aria-label="Genres">
      {genres.map((g) => (
        <Link key={g.name} className="chip" to={`/library/${library.id}?view=genre&genre=${encodeURIComponent(g.name)}`}>
          {g.name}
          <span className="chip-count">{g.count.toLocaleString()}</span>
        </Link>
      ))}
    </nav>
  )
}

/** One genre's books: its name for a title and a way back to Genres in place of the views, then every book in it. */
function GenrePage({ library, genre }: { library: Library; genre: string }) {
  return (
    <div className="library-page books-library">
      <header className="library-header">
        <div className="library-heading">
          <Link className="genre-back" to={`/library/${library.id}?view=genres`}>
            <ChevronLeft /> Genres
          </Link>
          <h1 className="library-title">{genre}</h1>
        </div>
      </header>
      <AllBooks key={`${library.id}-${genre}`} library={library} view="genre" />
    </div>
  )
}

const sorts: { value: NonNullable<BookFilters['sort']>; label: string }[] = [
  { value: 'title', label: 'A–Z' },
  { value: 'author', label: 'By author' },
  { value: 'series', label: 'By series' },
  { value: 'added', label: 'Recently added' },
  { value: 'released', label: 'Newest first' },
  { value: 'length', label: 'Longest first' },
  { value: 'listened', label: 'Recently listened' },
]

/** Length choices, as hours from and to. */
const lengths: { value: string; label: string; min?: number; max?: number }[] = [
  { value: '', label: 'Any length' },
  { value: 'short', label: 'Under 5 hours', max: 5 },
  { value: 'medium', label: '5–10 hours', min: 5, max: 10 },
  { value: 'long', label: '10–20 hours', min: 10, max: 20 },
  { value: 'epic', label: 'Over 20 hours', min: 20 },
]

/** Every book, filtered and sorted by what the address says, so a filter can be linked to and comes back on Back. */
function AllBooks({ library, view }: { library: Library; view: 'all' | 'genre' }) {
  const [params, setParams] = useSearchParams()
  const { reload: reloadLibraries } = useLibraries()
  const [search, setSearch] = useState(params.get('search') ?? '')
  const length = lengths.find((l) => l.value === params.get('length')) ?? lengths[0]
  const abridged = params.get('abridged')
  const filters: BookFilters = {
    search: params.get('search') ?? undefined,
    progress: (params.get('progress') as BookFilters['progress']) ?? undefined,
    author: params.get('author') ?? undefined,
    narrator: params.get('narrator') ?? undefined,
    genre: params.get('genre') ?? undefined,
    language: params.get('language') ?? undefined,
    abridged: abridged === 'yes' ? true : abridged === 'no' ? false : undefined,
    minHours: length.min,
    maxHours: length.max,
    sort: (params.get('sort') as BookFilters['sort']) ?? undefined,
  }
  const key = JSON.stringify(filters)
  const narrators = useApi((signal) => getBookPeople(library.id, { role: 'narrator', take: 200 }, signal), [library.id])
  // The languages its books are in, one per name, the one chosen always among them.
  const bookLanguages = useApi((signal) => getBookLanguages(library.id, signal), [library.id])
  const languages = languageOptions(bookLanguages.data ?? [], filters.language)
  // The author filtered by, named (a link to it carries only the id).
  const author = useApi((signal) => (filters.author ? getPerson(filters.author, signal) : Promise.resolve(null)), [filters.author])
  const authorName = author.data?.name
  const [pages, setPages] = useState<{ key: string; books: Paged<ItemSummary> } | null>(null)
  const first = useApi((signal) => getLibraryBooks(library.id, { ...filters, take: PAGE_SIZE }, signal), [library.id, key])
  useWatchedChanges(first.reload)
  const shown = pages?.key === key ? pages.books : first.data

  // The search box waits for a pause in typing before it changes the address.
  useEffect(() => {
    const timer = setTimeout(() => {
      if ((params.get('search') ?? '') !== search.trim()) set('search', search.trim())
    }, 300)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only the typing starts it
  }, [search])

  function set(name: string, value: string) {
    const next = new URLSearchParams(params)
    next.set('view', view)
    if (value) next.set(name, value)
    else next.delete(name)
    setParams(next, { replace: true })
  }

  /** Every filter off (the search too), keeping the sort. */
  function clearFilters() {
    const next = new URLSearchParams()
    next.set('view', view)
    // A genre's page keeps its genre.
    if (view === 'genre' && filters.genre) next.set('genre', filters.genre)
    const sort = params.get('sort')
    if (sort) next.set('sort', sort)
    setSearch('')
    setParams(next, { replace: true })
  }

  async function more() {
    if (!shown) return
    const page = await getLibraryBooks(library.id, { ...filters, skip: shown.items.length, take: PAGE_SIZE })
    setPages({ key, books: { total: page.total, items: [...shown.items, ...page.items] } })
  }

  const filtered = Object.entries(filters).some(([name, value]) => name !== 'sort' && !(view === 'genre' && name === 'genre') && value !== undefined)
  return (
    <>
      <div className="library-tools book-filters">
        <input type="search" placeholder="Search" aria-label={`Search ${library.name}`} value={search} onChange={(e) => setSearch(e.target.value)} />
        <Choice label="Progress" value={params.get('progress') ?? ''} onChange={(v) => set('progress', v)}>
          <option value="">Any progress</option>
          <option value="notstarted">Not started</option>
          <option value="inprogress">In progress</option>
          <option value="finished">Finished</option>
        </Choice>
        {(narrators.data?.items.length ?? 0) > 1 && (
          <Choice label="Narrator" value={params.get('narrator') ?? ''} onChange={(v) => set('narrator', v)}>
            <option value="">Any narrator</option>
            {narrators.data!.items.map((n) => (
              <option key={n.id} value={n.id}>
                {n.name}
              </option>
            ))}
          </Choice>
        )}
        <Choice label="Length" value={length.value} onChange={(v) => set('length', v)}>
          {lengths.map((l) => (
            <option key={l.value} value={l.value}>
              {l.label}
            </option>
          ))}
        </Choice>
        {(languages.length > 1 || filters.language) && (
          <Choice label="Language" value={filters.language ?? ''} onChange={(v) => set('language', v)}>
            <option value="">Any language</option>
            {languages.map((l) => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </Choice>
        )}
        <Choice label="Abridged" value={abridged ?? ''} onChange={(v) => set('abridged', v)}>
          <option value="">Any edition</option>
          <option value="no">Unabridged</option>
          <option value="yes">Abridged</option>
        </Choice>
        <Choice label="Sort by" value={params.get('sort') ?? 'title'} onChange={(v) => set('sort', v === 'title' ? '' : v)}>
          {sorts.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
        </Choice>
        <AsyncButton
          className="button quiet small"
          busy={!!library.scan}
          onClick={() => scanLibrary(library.id).then(() => new Promise((done) => setTimeout(done, 2500))).then(reloadLibraries)}
        >
          Scan for changes
        </AsyncButton>
      </div>
      {((filters.genre && view === 'all') || filters.author) && (
        <p className="chips">
          {filters.genre && view === 'all' && (
            <button className="chip" aria-pressed="true" onClick={() => set('genre', '')} aria-label={`Genre ${filters.genre}: clear`}>
              {filters.genre} ×
            </button>
          )}
          {filters.author && (
            <button className="chip" aria-pressed="true" onClick={() => set('author', '')} aria-label={authorName ? `Author ${authorName}: clear` : 'Author: clear'}>
              {authorName ?? 'Author'} ×
            </button>
          )}
        </p>
      )}
      {first.error && <p className="error">{first.error.message}</p>}
      {shown && (
        <p className="muted book-count">
          {shown.total.toLocaleString()} {filtered ? 'matching' : shown.total === 1 ? 'book' : 'books'}
        </p>
      )}
      {shown?.total === 0 &&
        (filtered ? (
          <div className="page-message muted">
            <p>No books match.</p>
            <button className="button quiet small" onClick={clearFilters}>
              Clear filters
            </button>
          </div>
        ) : (
          <p className="page-message muted">No books yet.</p>
        ))}
      <div className="poster-grid square" aria-busy={!shown}>
        {shown?.items.map((item) => (
          <div key={item.id} data-id={item.id}>
            <PosterCard item={item} />
          </div>
        ))}
      </div>
      {shown && shown.items.length < shown.total && (
        <div className="load-more">
          <AsyncButton className="button quiet" onClick={more}>
            Show {Math.min(PAGE_SIZE, shown.total - shown.items.length)} more
          </AsyncButton>
        </div>
      )}
    </>
  )
}

function Choice({ label, value, onChange, children }: { label: string; value: string; onChange: (value: string) => void; children: ReactNode }) {
  return (
    <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      {children}
    </select>
  )
}

/** A paged grid with a search box, for series and people. */
function usePaged<T>(load: (search: string, skip: number, signal?: AbortSignal) => Promise<Paged<T>>, deps: unknown[]) {
  const [search, setSearch] = useState('')
  const [typed, setTyped] = useState('')
  const [extra, setExtra] = useState<{ search: string; items: T[] } | null>(null)
  useEffect(() => {
    const timer = setTimeout(() => setSearch(typed.trim()), 300)
    return () => clearTimeout(timer)
  }, [typed])
  // eslint-disable-next-line react-hooks/exhaustive-deps -- callers pass what `load` closes over
  const first = useApi((signal) => load(search, 0, signal), [...deps, search])
  const items = first.data ? [...first.data.items, ...(extra?.search === search ? extra.items : [])] : undefined
  const more = async () => {
    const page = await load(search, items?.length ?? 0)
    setExtra({ search, items: [...(extra?.search === search ? extra.items : []), ...page.items] })
  }
  return { typed, setTyped, items, total: first.data?.total, error: first.error, more }
}

function SeriesList({ library }: { library: Library }) {
  const paged = usePaged((search, skip, signal) => getBookSeriesList(library.id, { search: search || undefined, skip, take: PAGE_SIZE }, signal), [library.id])
  return (
    <PagedGrid paged={paged} label="series" empty="No series here yet." className="poster-grid square book-series-grid">
      {paged.items?.map((s) => (
        <BookSeriesCard key={s.id} series={s} />
      ))}
    </PagedGrid>
  )
}

function People({ library, role }: { library: Library; role: 'author' | 'narrator' }) {
  const paged = usePaged((search, skip, signal) => getBookPeople(library.id, { role, search: search || undefined, skip, take: PAGE_SIZE }, signal), [library.id, role])
  return (
    <PagedGrid paged={paged} label={role === 'author' ? 'authors' : 'narrators'} empty={`No ${role}s here yet.`} className="poster-grid people-grid">
      {paged.items?.map((p) => (
        <BookPersonCard key={p.id} person={p} />
      ))}
    </PagedGrid>
  )
}

function PagedGrid<T>({ paged, label, empty, className, children }: { paged: ReturnType<typeof usePaged<T>>; label: string; empty: string; className: string; children: ReactNode }) {
  return (
    <>
      <div className="library-tools book-filters">
        <input type="search" placeholder="Search" aria-label={`Search ${label}`} value={paged.typed} onChange={(e) => paged.setTyped(e.target.value)} />
      </div>
      {paged.error && <p className="error">{paged.error.message}</p>}
      {paged.total === 0 && <p className="page-message muted">{paged.typed ? 'Nothing matches.' : empty}</p>}
      <div className={className} aria-busy={!paged.items}>
        {children}
      </div>
      {paged.items && paged.total !== undefined && paged.items.length < paged.total && (
        <div className="load-more">
          <AsyncButton className="button quiet" onClick={paged.more}>
            Show more
          </AsyncButton>
        </div>
      )}
    </>
  )
}
