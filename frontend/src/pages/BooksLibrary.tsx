import { Fragment, useState, type ReactNode } from 'react'
import { PageError } from '../components/PageStatus'
import { Link, useSearchParams } from 'react-router'
import {
  getBookGenres,
  getBookPeople,
  getBookSeriesList,
  getBookShelves,
  getLibraryBooks,
  getPerson,
  imageUrl,
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
  if (error) return <PageError error={error} onRetry={reload} />
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
  if (!order.some((id) => shelves[id])) return <AllBooks library={library} view="all" />
  return (
    <div className="book-shelves">
      {order.map((id) => (
        <Fragment key={id}>{shelves[id]}</Fragment>
      ))}
    </div>
  )
}

/**
 * The library's genres, A–Z, as tiles like the films' and shows' Genres: books have no backdrops, so a genre's newest
 * covers stand fanned in the tile, over the front one blurred to fill it with its colours. Each opens a page of its books.
 */
function Genres({ library }: { library: Library }) {
  const { data, error, reload } = useApi((signal) => getBookGenres(library.id, signal), [library.id, library.itemCount])
  if (error) return <PageError error={error} onRetry={reload} />
  if (!data) return <div className="genre-grid" aria-busy="true" />
  if (data.length === 0) return <p className="page-message muted">No genres here yet.</p>
  const genres = [...data].sort((a, b) => a.name.localeCompare(b.name))
  return (
    <nav className="genre-grid" aria-label="Genres">
      {genres.map((g) => {
        const covers = (g.covers ?? []).slice(0, 3)
        return (
          <Link key={g.name} to={`/library/${library.id}?view=genre&genre=${encodeURIComponent(g.name)}`} className="genre-tile book-genre-tile">
            {covers[0] && <img className="book-genre-wash" src={imageUrl(covers[0], 185)} alt="" loading="lazy" />}
            {covers.length > 0 && (
              <span className={`book-genre-fan book-genre-fan-${covers.length}`} aria-hidden="true">
                {covers.map((cover, i) => (
                  <img key={i} src={imageUrl(cover, 342)} alt="" loading="lazy" />
                ))}
              </span>
            )}
            <span className="genre-name">{g.name}</span>
            <span className="genre-count">{g.count === 1 ? '1 book' : `${g.count.toLocaleString()} books`}</span>
          </Link>
        )
      })}
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

/** Every book, filtered and sorted by what the address says, so a filter can be linked to and comes back on Back. */
function AllBooks({ library, view }: { library: Library; view: 'all' | 'genre' }) {
  const [params, setParams] = useSearchParams()
  const { reload: reloadLibraries } = useLibraries()
  // Only what a link brings (a shelf's See all, an author's books, a genre): no search box or filter menus.
  const progress = (params.get('progress') as BookFilters['progress']) ?? undefined
  const filters: BookFilters = {
    progress,
    author: params.get('author') ?? undefined,
    genre: params.get('genre') ?? undefined,
    sort: (params.get('sort') as BookFilters['sort']) ?? undefined,
  }
  const key = JSON.stringify(filters)
  // The author filtered by, named (a link to it carries only the id).
  const author = useApi((signal) => (filters.author ? getPerson(filters.author, signal) : Promise.resolve(null)), [filters.author])
  const authorName = author.data?.name
  const [pages, setPages] = useState<{ key: string; books: Paged<ItemSummary> } | null>(null)
  const first = useApi((signal) => getLibraryBooks(library.id, { ...filters, take: PAGE_SIZE }, signal), [library.id, key])
  useWatchedChanges(first.reload)
  const shown = pages?.key === key ? pages.books : first.data

  function set(name: string, value: string) {
    const next = new URLSearchParams(params)
    next.set('view', view)
    if (value) next.set(name, value)
    else next.delete(name)
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
      {((filters.genre && view === 'all') || filters.author || progress) && (
        <p className="chips">
          {progress && progressLabels[progress] && (
            <button className="chip" aria-pressed="true" onClick={() => set('progress', '')} aria-label={`${progressLabels[progress]}: clear`}>
              {progressLabels[progress]} ×
            </button>
          )}
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
      {shown?.total === 0 && <p className="page-message muted">{filtered ? 'No books match.' : 'No books yet.'}</p>}
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

const progressLabels: Record<string, string> = { notstarted: 'Not started', inprogress: 'In progress', finished: 'Finished' }

function Choice({ label, value, onChange, children }: { label: string; value: string; onChange: (value: string) => void; children: ReactNode }) {
  return (
    <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      {children}
    </select>
  )
}

/** A paged grid, for series and people. */
function usePaged<T>(load: (skip: number, signal?: AbortSignal) => Promise<Paged<T>>, deps: unknown[]) {
  const [extra, setExtra] = useState<T[]>([])
  // eslint-disable-next-line react-hooks/exhaustive-deps -- callers pass what `load` closes over
  const first = useApi((signal) => load(0, signal), deps)
  const items = first.data ? [...first.data.items, ...extra] : undefined
  const more = async () => {
    const page = await load(items?.length ?? 0)
    setExtra([...extra, ...page.items])
  }
  return { items, total: first.data?.total, error: first.error, more }
}

function SeriesList({ library }: { library: Library }) {
  const paged = usePaged((skip, signal) => getBookSeriesList(library.id, { skip, take: PAGE_SIZE }, signal), [library.id])
  return (
    <PagedGrid paged={paged} empty="No series here yet." className="poster-grid square book-series-grid">
      {paged.items?.map((s) => (
        <BookSeriesCard key={s.id} series={s} />
      ))}
    </PagedGrid>
  )
}

function People({ library, role }: { library: Library; role: 'author' | 'narrator' }) {
  const paged = usePaged((skip, signal) => getBookPeople(library.id, { role, skip, take: PAGE_SIZE }, signal), [library.id, role])
  return (
    <PagedGrid paged={paged} empty={`No ${role}s here yet.`} className="poster-grid people-grid">
      {paged.items?.map((p) => (
        <BookPersonCard key={p.id} person={p} />
      ))}
    </PagedGrid>
  )
}

function PagedGrid<T>({ paged, empty, className, children }: { paged: ReturnType<typeof usePaged<T>>; empty: string; className: string; children: ReactNode }) {
  return (
    <>
      {paged.error && <p className="error">{paged.error.message}</p>}
      {paged.total === 0 && <p className="page-message muted">{empty}</p>}
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
