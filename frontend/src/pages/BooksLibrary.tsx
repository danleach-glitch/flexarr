import { Fragment, useEffect, useState, type ReactNode } from 'react'
import { PageError } from '../components/PageStatus'
import { Link, useSearchParams } from 'react-router'
import {
  getBookGenres,
  getBookPeople,
  getBookSeriesList,
  getBookShelves,
  getLibraryBooks,
  getPerson,
 
  type BookFilters,
  type BookShelves,
  type ItemSummary,
  type Library,
  type Paged,
} from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { BookPersonCard, BookSeriesCard } from '../components/BookCards'
import { PosterCard } from '../components/Cards'
import { GenreCovers } from '../components/GenreCovers'
import { Hero } from '../components/Hero'
import { ChevronLeft } from '../components/icons'
import { Row } from '../components/Row'
import { ScanProgress } from '../components/ScanProgress'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'
import { AlphaPicker, AlphaRail, ShowMore, useLibraryGrid } from './libraryGrid'
import { locale, t, tk, Words } from '../i18n'

const PAGE_SIZE = 60
/** The most books the server gives in one page (BookEndpoints' MaxPage). */
const MAX_PAGE = 200

type View = 'browse' | 'all' | 'series' | 'authors' | 'genres' | 'narrators' | 'genre'

/** The same views, in the same order, as the iPhone and Android apps. */
const views: { view: View; label: string }[] = [
  { view: 'browse', label: Words.browse },
  { view: 'all', label: tk('All books') },
  { view: 'genres', label: Words.genres },
  { view: 'series', label: Words.series },
  { view: 'authors', label: tk('Authors') },
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
  const heading = (
    <>
      <h1 className="library-title">{library.name}</h1>
      <nav className="view-switch" aria-label={t('{name} views', { name: library.name })}>
        {views.map((v) => (
          <Link key={v.view} to={v.view === 'browse' ? `/library/${library.id}` : `/library/${library.id}?view=${v.view}`} aria-current={view === v.view ? 'page' : undefined}>
            {t(v.label)}
          </Link>
        ))}
      </nav>
    </>
  )
  // Browse with a hero: the heading sits over it, as a film or music library's does.
  if (view === 'browse') return <Shelves key={library.id} library={library} heading={heading} />
  return (
    <div className="library-page books-library">
      <header className="library-header">
        <div className="library-heading">{heading}</div>
      </header>
      {library.scan && <ScanProgress scan={library.scan} />}
      {view === 'all' && <AllBooks key={library.id} library={library} view="all" />}
      {view === 'series' && <SeriesList key={library.id} library={library} />}
      {view === 'genres' && <Genres key={library.id} library={library} />}
      {(view === 'authors' || view === 'narrators') && <People key={`${library.id}-${view}`} library={library} role={view === 'authors' ? 'author' : 'narrator'} />}
    </div>
  )
}

/** Browse: the hero (servers from 0.59, with books to show) under the heading, then the shelves. */
function Shelves({ library, heading }: { library: Library; heading: ReactNode }) {
  const { data, error, reload } = useApi((signal) => getBookShelves(library.id, signal), [library.id, library.itemCount])
  useWatchedChanges(reload)
  const plain = (content: ReactNode) => (
    <div className="library-page books-library">
      <header className="library-header">
        <div className="library-heading">{heading}</div>
      </header>
      {library.scan && <ScanProgress scan={library.scan} />}
      {content}
    </div>
  )
  if (error) return plain(<PageError error={error} onRetry={reload} />)
  if (!data) return plain(<div className="rows" aria-busy="true" />)
  const shelves = <ShelfRows library={library} data={data} />
  if (!data.featured?.length) return plain(shelves)
  return (
    <>
      <Hero items={data.featured} overviews={data.featuredOverviews} top={heading} />
      <div className="library-page books-library">
        {library.scan && <ScanProgress scan={library.scan} />}
        {shelves}
      </div>
    </>
  )
}

function ShelfRows({ library, data }: { library: Library; data: BookShelves }) {
  const all = `/library/${library.id}?view=all`
  const empty = [data.continueListening, data.nextInSeries, data.recentlyAdded, data.finished].every((s) => s.length === 0) && data.series.length === 0
  if (empty) return <p className="page-message muted">{library.scan ? t('Books appear here as the scan finds them.') : t('No books yet.')}</p>
  const books = (title: string, items: ItemSummary[], link?: string) =>
    items.length > 0 && (
      <Row title={title} link={link}>
        {items.map((item) => (
          <PosterCard key={item.id} item={item} />
        ))}
      </Row>
    )
  const shelves: Record<string, ReactNode> = {
    continue: books(t('Continue listening'), data.continueListening, `${all}&progress=inprogress&sort=listened`),
    next: books(t('Next in series'), data.nextInSeries),
    recent: books(t(Words.recentlyAdded), data.recentlyAdded, `${all}&sort=added`),
    series: data.series.length > 0 && (
      <Row title={t(Words.series)} link={`/library/${library.id}?view=series`}>
        {data.series.map((s) => (
          <BookSeriesCard key={s.id} series={s} />
        ))}
      </Row>
    ),
    authors: data.authors.length > 0 && (
      <Row title={t('Authors')} link={`/library/${library.id}?view=authors`} style="people">
        {data.authors.map((p) => (
          <BookPersonCard key={p.id} person={p} />
        ))}
      </Row>
    ),
    finished: books(t('Finished'), data.finished, `${all}&progress=finished`),
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
  if (data.length === 0) return <p className="page-message muted">{t('No genres here yet. They come with each book’s details once a scan has found them.')}</p>
  const genres = [...data].sort((a, b) => a.name.localeCompare(b.name, locale()))
  return (
    <nav className="genre-grid" aria-label={t(Words.genres)}>
      {genres.map((g) => (
        <Link key={g.name} to={`/library/${library.id}?view=genre&genre=${encodeURIComponent(g.name)}`} className="genre-tile book-genre-tile">
          <GenreCovers covers={g.covers} />
          <span className="genre-name">{g.name}</span>
          <span className="genre-count">{t('{count, plural, one {# book} other {# books}}', { count: g.count })}</span>
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
            <ChevronLeft /> {t(Words.genres)}
          </Link>
          <h1 className="library-title">{genre}</h1>
        </div>
      </header>
      <AllBooks key={`${library.id}-${genre}`} library={library} view="genre" />
    </div>
  )
}

const sorts: { value: NonNullable<BookFilters['sort']>; label: string }[] = [
  { value: 'title', label: Words.sortAToZ },
  { value: 'author', label: Words.author },
  { value: 'series', label: Words.series },
  { value: 'added', label: Words.recentlyAdded },
  { value: 'released', label: Words.year },
  { value: 'length', label: Words.length },
  { value: 'listened', label: Words.recentlyListened },
]

/**
 * Every book, filtered and sorted by what the address says, so a filter can be linked to and comes back on Back. As
 * the films' grid: a search box, the A–Z rail (sorted by title), and Back returns to the same place.
 */
function AllBooks({ library, view }: { library: Library; view: 'all' | 'genre' }) {
  const [params, setParams] = useSearchParams()
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

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250)
    return () => clearTimeout(timer)
  }, [search])
  const { items, end, total, error, moreError, busy, jumping, jumpFailed, grid, empty, small, grown, loadedCount, onGridClick, loadMore, jump, restart } = useLibraryGrid({
    snapshotKey: `books-${view} ${library.id} ${key}`,
    query,
    pageSize: PAGE_SIZE,
    jumpPageSize: MAX_PAGE,
    itemCount: library.itemCount,
    load: (page, signal) => getLibraryBooks(library.id, { ...filters, search: query || undefined, ...page }, signal),
  })

  function set(name: string, value: string) {
    const next = new URLSearchParams(params)
    next.set('view', view)
    if (value) next.set(name, value)
    else next.delete(name)
    setParams(next, { replace: true })
  }

  const filtered = !!query || Object.entries(filters).some(([name, value]) => name !== 'sort' && !(view === 'genre' && name === 'genre') && value !== undefined)
  const letters = (!filters.sort || filters.sort === 'title') && !query && items.length > 40
  return (
    <>
      <div className="library-tools book-filters">
        <input type="search" placeholder={t('Search {name}', { name: library.name })} aria-label={t('Search {name}', { name: library.name })} value={search} onChange={(e) => setSearch(e.target.value)} />
        <Choice label={t('Sort by')} value={params.get('sort') ?? 'title'} onChange={(v) => set('sort', v === 'title' ? '' : v)}>
          {sorts.map((s) => (
            <option key={s.value} value={s.value}>
              {t(s.label)}
            </option>
          ))}
        </Choice>
      </div>
      {((filters.genre && view === 'all') || filters.author || progress) && (
        <p className="chips">
          {progress && progressLabels[progress] && (
            <button className="chip" aria-pressed="true" onClick={() => set('progress', '')} aria-label={t('{filter}: clear', { filter: t(progressLabels[progress]) })}>
              {t(progressLabels[progress])} ×
            </button>
          )}
          {filters.genre && view === 'all' && (
            <button className="chip" aria-pressed="true" onClick={() => set('genre', '')} aria-label={t('Genre {genre}: clear', { genre: filters.genre })}>
              {filters.genre} ×
            </button>
          )}
          {filters.author && (
            <button className="chip" aria-pressed="true" onClick={() => set('author', '')} aria-label={authorName ? t('Author {name}: clear', { name: authorName }) : t('Author: clear')}>
              {authorName ?? t(Words.author)} ×
            </button>
          )}
        </p>
      )}
      {grown && !small && (
        <p className="library-new" role="status">
          {library.itemCount > (loadedCount ?? 0) ? t('New books have arrived.') : t('Some books have gone.')}{' '}
          <button className="button quiet small" onClick={restart}>
            {t('Refresh')}
          </button>
        </p>
      )}
      {error && <PageError error={error} onRetry={restart} />}
      {total !== undefined && (
        <p className="muted book-count">{filtered ? t('{count} matching', { count: total }) : t('{count, plural, one {# book} other {# books}}', { count: total })}</p>
      )}
      {total === 0 && <p className="page-message muted">{query ? t('Nothing in {name} matches “{query}”.', { name: library.name, query }) : filtered ? t('No books match.') : t('No books yet.')}</p>}
      <div className="library-body">
        {letters && <AlphaPicker jump={(l) => void jump(l)} jumping={jumping} failed={jumpFailed} empty={empty} />}
        <div className="poster-grid square" aria-busy={total === undefined} ref={grid} onClick={onGridClick}>
          {items.map((item) => (
            <div key={item.id} data-id={item.id}>
              <PosterCard item={item} />
            </div>
          ))}
        </div>
        {letters && <AlphaRail jump={(l) => void jump(l)} jumping={jumping} failed={jumpFailed} empty={empty} />}
      </div>
      <ShowMore loaded={end} total={total} pageSize={PAGE_SIZE} busy={busy} error={moreError} onMore={() => void loadMore()} />
    </>
  )
}

const progressLabels: Record<string, string> = { notstarted: tk('Not started'), inprogress: tk('In progress'), finished: tk('Finished') }

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
  return { items, total: first.data?.total, error: first.error, reload: first.reload, more }
}

function SeriesList({ library }: { library: Library }) {
  const paged = usePaged((skip, signal) => getBookSeriesList(library.id, { skip, take: PAGE_SIZE }, signal), [library.id])
  return (
    <PagedGrid paged={paged} empty={t('No series here yet.')} className="poster-grid square book-series-grid">
      {paged.items?.map((s) => (
        <BookSeriesCard key={s.id} series={s} />
      ))}
    </PagedGrid>
  )
}

function People({ library, role }: { library: Library; role: 'author' | 'narrator' }) {
  const paged = usePaged((skip, signal) => getBookPeople(library.id, { role, skip, take: PAGE_SIZE }, signal), [library.id, role])
  return (
    <PagedGrid paged={paged} empty={role === 'author' ? t('No authors here yet.') : t('No narrators here yet.')} className="poster-grid people-grid">
      {paged.items?.map((p) => (
        <BookPersonCard key={p.id} person={p} />
      ))}
    </PagedGrid>
  )
}

function PagedGrid<T>({ paged, empty, className, children }: { paged: ReturnType<typeof usePaged<T>>; empty: string; className: string; children: ReactNode }) {
  return (
    <>
      {paged.error && <PageError error={paged.error} onRetry={paged.reload} />}
      {paged.total === 0 && <p className="page-message muted">{empty}</p>}
      <div className={className} aria-busy={!paged.items}>
        {children}
      </div>
      {paged.items && paged.total !== undefined && paged.items.length < paged.total && (
        <div className="load-more">
          <AsyncButton className="button quiet" onClick={paged.more}>
            {t('Show more')}
          </AsyncButton>
        </div>
      )}
    </>
  )
}
