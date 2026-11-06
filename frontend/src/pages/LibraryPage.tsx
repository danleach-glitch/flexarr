import { lazy, Suspense, useEffect, useLayoutEffect, useMemo, useRef, useState, type MouseEvent, type ReactNode } from 'react'
import { PageError } from '../components/PageStatus'
import { Link, Navigate, useNavigationType, useParams, useSearchParams } from 'react-router'
import { forgetPage, getGenreTracks, getLibraryAlbumGenres, getLibraryAlbums, getLibraryBrowse, getLibraryGenres, getLibraryItems, getMusicRequestStatus, getRequestStatusCached, imageUrl, keepPage, keptPage, type HomeRow, type Library } from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { PosterCard } from '../components/Cards'
import { GenreCovers } from '../components/GenreCovers'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { ScanProgress } from '../components/ScanProgress'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { ChevronLeft } from '../components/icons'
import { icon } from '../components/icons.generated'
import { libraryGenreLink } from '../lib/links'
import { onDemand } from '../lib/onDemand'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'
import { LibraryRequested } from './LibraryRequested'
import { AlphaPicker, AlphaRail, ShowMore, useLibraryGrid } from './libraryGrid'
import { t, Words } from '../i18n'

const PAGE_SIZE = 200

const sorts = [
  { value: '', label: Words.sortAToZ },
  { value: 'added', label: Words.recentlyAdded },
  { value: 'year', label: Words.year },
  { value: 'rating', label: Words.rating },
] as const
/** What a music library's albums sort by: no ratings there. */
const albumSorts = sorts.filter((s) => s.value !== 'rating')

// A book library's shelves and a music library's playlists load when one is opened: most first pages need neither.
const BooksLibrary = lazy(() => onDemand(() => import('./BooksLibrary').then((m) => ({ default: m.BooksLibrary }))))
const PlaylistsPage = lazy(() => onDemand(() => import('./PlaylistsPage').then((m) => ({ default: m.PlaylistsPage }))))

/** Says Loading… where the page goes, as the library does before it's known, while a page's code arrives. */
function WhileLoading({ children }: { children: ReactNode }) {
  return <Suspense fallback={<p className="page-message muted">{t('Loading…')}</p>}>{children}</Suspense>
}

/**
 * Ways into a library: Browse (a home page of rows for just this library), All (every title, with sort, genre and
 * letter jumps) and, for films and shows, Genres (a tile for each, opening All filtered to it). Links that sort or
 * filter open All.
 */
export function LibraryPage() {
  const { id = '' } = useParams()
  const [params] = useSearchParams()
  const { libraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)
  if (!library) return <p className="page-message muted">{libraries.length ? t('This library no longer exists.') : t('Loading…')}</p>
  // Podcasts have pages of their own: what you follow, and Apple's directory to find more.
  if (library.type === 'Podcasts') return <Navigate to="/podcasts" replace />
  // Books have shelves, series, authors and narrators of their own.
  if (library.type === 'Audiobooks')
    return (
      <WhileLoading>
        <BooksLibrary library={library} />
      </WhileLoading>
    )

  // A genre's own page, as books have: its titles (a music library's albums) under its name, with a way back to Genres.
  const genrePage = params.get('view') === 'genre' && params.get('genre') && hasGenres(library)
  if (genrePage) return <LibraryAll key={`${id} genre ${params.get('genre')}`} albums={library.type === 'Music'} genrePage />
  // A music library's albums.
  if (library.type === 'Music' && params.get('view') === 'albums') return <LibraryAll key={`${id} albums`} albums />
  // What Lidarr (music) or Seerr (films and shows) is waiting for: only offered when they can be requested.
  if (hasRequested(library) && params.get('view') === 'requested')
    return <LibraryRequested key={`${id} requested`} library={library} viewSwitch={<ViewSwitch library={library} view="requested" />} />
  // The profile's playlists, under the library's views.
  if (library.type === 'Music' && params.get('view') === 'playlists')
    return (
      <WhileLoading>
        <PlaylistsPage
          key={`${id} playlists`}
          heading={
            <div className="library-heading">
              <h1 className="library-title">{library.name}</h1>
              <ViewSwitch library={library} view="playlists" />
            </div>
          }
        />
      </WhileLoading>
    )
  const all = params.get('view') === 'all' || params.has('sort') || params.has('genre')
  if (all) return <LibraryAll key={id} />
  if (params.get('view') === 'genres' && hasGenres(library)) return <LibraryGenres key={id} library={library} />
  return <LibraryBrowse key={id} library={library} />
}

const hasRequested = (library: Library) => library.type === 'Music' || library.type === 'Movies' || library.type === 'TvShows'

const hasGenres = (library: Library) => library.type === 'Movies' || library.type === 'TvShows' || library.type === 'Music'

function ViewSwitch({ library, view }: { library: Library; view: 'browse' | 'all' | 'albums' | 'genres' | 'playlists' | 'requested' }) {
  const current = (v: typeof view) => (view === v ? 'page' : undefined)
  // Requested is there when the library's kind can be asked for (music through Lidarr, films and shows through Seerr).
  // The status is cached, so every view asking is cheap.
  const music = library.type === 'Music'
  const requestable = hasRequested(library)
  const requests = useApi(
    (signal) => (music ? getMusicRequestStatus(signal) : requestable ? getRequestStatusCached(signal) : Promise.resolve(undefined)),
    [music, requestable],
  )
  return (
    <nav className="view-switch" aria-label={t('{name} views', { name: library.name })}>
      <Link to={`/library/${library.id}`} aria-current={current('browse')}>
        {t(Words.browse)}
      </Link>
      <Link to={`/library/${library.id}?view=all`} aria-current={current('all')}>
        {{ Movies: t('All movies'), TvShows: t('All shows'), Music: t(Words.artists), Podcasts: t('All podcasts'), HomeVideos: t('All folders'), Audiobooks: t('All books') }[library.type]}
      </Link>
      {library.type === 'Music' && (
        <Link to={`/library/${library.id}?view=albums`} aria-current={current('albums')}>
          {t(Words.albums)}
        </Link>
      )}
      {hasGenres(library) && (
        <Link to={`/library/${library.id}?view=genres`} aria-current={current('genres')}>
          {t(Words.genres)}
        </Link>
      )}
      {music && (
        <Link to={`/library/${library.id}?view=playlists`} aria-current={current('playlists')}>
          {t('Playlists')}
        </Link>
      )}
      {/* Last, so the others don't move as it arrives: only once the status says requests work (a child's is refused, so
          it never flickers in for them), or while it's the view open. */}
      {requestable && (requests.data?.configured || view === 'requested') && (
        <Link to={`/library/${library.id}?view=requested`} aria-current={current('requested')}>
          {t(Words.requested)}
        </Link>
      )}
    </nav>
  )
}

/** A tile for each of the library's genres, with a backdrop from one of its titles; one opens the genre's own page. */
function LibraryGenres({ library }: { library: Library }) {
  const music = library.type === 'Music'
  const { data, error, reload } = useApi((signal) => (music ? getLibraryAlbumGenres : getLibraryGenres)(library.id, signal), [library.id])
  return (
    <div className="library-page">
      <header className="library-header">
        <div className="library-heading">
          <h1 className="library-title">{library.name}</h1>
          <ViewSwitch library={library} view="genres" />
        </div>
      </header>
      {error ? (
        <PageError error={error} onRetry={reload} />
      ) : !data ? (
        <div className="genre-grid" aria-busy="true" />
      ) : data.length === 0 ? (
        <p className="page-message muted">{t('No genres here yet. They come with each title’s details once a scan has found them.')}</p>
      ) : (
        <div className="genre-grid">
          {data.map((g) => (
            <Link key={g.name} to={libraryGenreLink(library.id, library.type, g.name)} className={!g.backdrop && g.covers?.length ? 'genre-tile book-genre-tile' : 'genre-tile'}>
              {g.backdrop ? <img src={imageUrl(g.backdrop, 780)} alt="" loading="lazy" /> : <GenreCovers covers={g.covers} />}
              <span className="genre-name">{g.name}</span>
              <span className="genre-count">
                {library.type === 'Movies'
                  ? t('{count, plural, one {# movie} other {# movies}}', { count: g.count })
                  : music
                    ? t('{count, plural, one {# album} other {# albums}}', { count: g.count })
                    : t('{count, plural, one {# show} other {# shows}}', { count: g.count })}
              </span>
            </Link>
          ))}
        </div>
      )}
    </div>
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

/** Where a card on a page of rows is: its row's heading and its link (the same title can sit in several rows). */
interface CardPlace {
  row: string | undefined
  href: string
}

/** The card `target` is part of, inside `container`, by its keyboard-reachable link. */
function placeOf(target: EventTarget | Element | null, container: HTMLElement | null): CardPlace | undefined {
  const card = target instanceof Element ? target.closest('.card') : null
  if (!card || !container?.contains(card)) return undefined
  const href = card.querySelector('a:not([tabindex="-1"])')?.getAttribute('href')
  return href ? { row: card.closest('.row')?.querySelector('h2')?.textContent ?? undefined, href } : undefined
}

/**
 * Going back to a page of rows puts keyboard focus on the card that was opened (or had focus when you left), so Tab
 * carries on from there. Spread the result on the rows' container.
 */
function useFocusReturn(key: string, ready: boolean) {
  const container = useRef<HTMLDivElement>(null)
  const navigation = useNavigationType()
  const [target] = useState(() => (navigation === 'POP' ? keptPage<CardPlace>(key) : undefined))
  const clicked = useRef<CardPlace>(undefined)
  const done = useRef(false)

  useEffect(() => {
    if (!ready || !target || done.current) return
    done.current = true
    // After the page's own scroll to the top on arrival.
    requestAnimationFrame(() => {
      const links = (scope: Element | null | undefined) =>
        Array.from(scope?.querySelectorAll<HTMLElement>('a:not([tabindex="-1"])') ?? []).filter((a) => a.getAttribute('href') === target.href)
      const rows = Array.from(container.current?.querySelectorAll('.row') ?? [])
      const row = rows.find((r) => r.querySelector('h2')?.textContent === target.row)
      const link = links(row)[0] ?? links(container.current)[0]
      if (!link) return
      link.closest('.card')?.scrollIntoView({ block: 'center', inline: 'nearest' })
      link.focus({ preventScroll: true })
    })
  }, [ready, target])

  // On the way out, remember the card with focus, else the one clicked to leave. A layout effect, so the rows are
  // still there; run again once they've arrived, to have hold of them.
  useLayoutEffect(() => {
    const element = container.current
    return () => {
      const place = placeOf(document.activeElement, element) ?? clicked.current
      if (place) keepPage(key, place)
      else forgetPage(key)
    }
  }, [key, ready])

  const onClick = (e: MouseEvent) => {
    if (e.target instanceof Element && e.target.closest('a')) clicked.current = placeOf(e.target, container.current)
  }
  return { ref: container, onClick }
}

function LibraryBrowse({ library }: { library: Library }) {
  // A scan changes the count every few seconds; these rows are heavy, so refresh them at most every 30 seconds while
  // it runs, and once more when it's done.
  const itemCount = useThrottled(library.itemCount, library.scan ? 30_000 : 0)
  const { data: loaded, error, reload } = useApi((signal) => getLibraryBrowse(library.id, signal), [library.id, itemCount])
  useWatchedChanges(reload)
  // A music library's Browse ends with Your artists, as in the apps; See all opens Artists when there are more.
  const music = library.type === 'Music'
  const artists = useApi((signal) => (music ? getLibraryItems(library.id, { take: ARTISTS_IN_ROW }, signal) : Promise.resolve(undefined)), [library.id, music, itemCount])
  const data = useMemo(() => {
    const page = artists.data
    if (!loaded || !page || page.items.length === 0) return loaded
    const row: HomeRow = {
      id: 'your-artists',
      title: t(Words.yourArtists),
      style: 'poster',
      items: page.items,
      link: page.total > page.items.length ? `/library/${library.id}?view=all` : null,
    }
    return { ...loaded, rows: [...loaded.rows, row] }
  }, [loaded, artists.data, library.id])
  const focusReturn = useFocusReturn(`library-browse ${library.id}`, !!data && data.rows.length > 0)
  const heading = (
    <>
      <h1 className="library-title">{library.name}</h1>
      <ViewSwitch library={library} view="browse" />
    </>
  )

  if (error) return <PageError error={error} onRetry={reload} />
  if (!data) return <div className="hero hero-loading" aria-busy="true" />
  // Every row switched off in Home & browse (or none with anything in it), but the library has titles: show them all.
  if (data.rows.length === 0 && library.itemCount > 0 && !library.scan) return <LibraryAll key={library.id} />
  if (data.rows.length === 0) {
    return (
      <div className="library-page">
        <header className="library-header">
          <div className="library-heading">{heading}</div>
        </header>
        <p className="page-message muted">{library.scan ? t('Titles appear here as the scan finds them.') : t('Nothing here yet.')}</p>
      </div>
    )
  }

  // No hero (a music library on an older server, say): the heading stands on its own above the rows.
  if (data.featured.length === 0) {
    return (
      <>
        <div className="library-page">
          <header className="library-header">
            <div className="library-heading">{heading}</div>
          </header>
        </div>
        <div className="rows no-hero" {...focusReturn}>
          <HomeRows rows={data.rows} upcoming={data.upcoming} />
        </div>
      </>
    )
  }

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} resumes={data.resumes} top={heading} />
      <div className="rows" {...focusReturn}>
        <HomeRows rows={data.rows} upcoming={data.upcoming} />
      </div>
    </>
  )
}

/** Play and Shuffle for a music genre: up to 200 of its tracks. Its own component, so only it follows the player. */
function GenrePlay({ library, genre, disabled }: { library: string; genre: string; disabled: boolean }) {
  const player = useAudioPlayer()
  const play = async (shuffle: boolean) => {
    const tracks = await getGenreTracks(library, genre, shuffle)
    if (tracks.length > 0) player.play(tracks, 0, { openNowPlaying: true })
  }
  return (
    <div className="detail-actions">
      <AsyncButton className="button" onClick={() => play(false)} disabled={disabled}>
        <icon.play /> {t(Words.play)}
      </AsyncButton>
      <AsyncButton className="button quiet" onClick={() => play(true)} disabled={disabled}>
        <icon.shuffle /> {t(Words.shuffle)}
      </AsyncButton>
    </div>
  )
}

/** "1,204 movies", "12 Jazz albums", "3 matching": how many titles the grid below holds. */
function countLine(library: Library, total: number | undefined, { albums, genrePage, genre, query }: { albums: boolean; genrePage: boolean; genre: string; query: string }) {
  if (albums) {
    const count = total ?? 0
    if (query) return t('{count} matching', { count })
    if (genre && !genrePage) return t('{count, plural, one {# {genre} album} other {# {genre} albums}}', { count, genre })
    return t('{count, plural, one {# album} other {# albums}}', { count })
  }
  if (genrePage) {
    const count = total ?? 0
    if (query) return t('{count} matching', { count })
    return library.type === 'TvShows' ? t('{count, plural, one {# show} other {# shows}}', { count }) : t('{count, plural, one {# movie} other {# movies}}', { count })
  }
  const count = total ?? library.itemCount
  if (query || genre) return t('{count} matching', { count })
  switch (library.type) {
    case 'Movies':
      return t('{count, plural, one {# movie} other {# movies}}', { count })
    case 'TvShows':
      return t('{count, plural, one {# show} other {# shows}}', { count })
    case 'Music':
      return t('{count, plural, one {# artist} other {# artists}}', { count })
    case 'Podcasts':
      return t('{count, plural, one {# podcast} other {# podcasts}}', { count })
    case 'HomeVideos':
      return t('{count, plural, one {# folder} other {# folders}}', { count })
    case 'Audiobooks':
      return t('{count, plural, one {# book} other {# books}}', { count })
    default:
      return t('{count, plural, one {# title} other {# titles}}', { count })
  }
}

/** How many artists Your artists shows before its See all. */
const ARTISTS_IN_ROW = 20

/**
 * Every title in a library, or (`albums`) every album in a music library, with sort, genre and letter jumps. As a
 * genre's own page (`genrePage`), the genre's alone under its name, with a way back to Genres instead of the views.
 */
function LibraryAll({ albums = false, genrePage = false }: { albums?: boolean; genrePage?: boolean }) {
  const { id = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const sort = params.get('sort') ?? ''
  const genre = params.get('genre') ?? ''
  const { libraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)
  // Movies and shows filter by genre here; music has its Genres view for that, and a genre's page is one genre.
  const pills = library?.type !== 'Music' && !genrePage
  const genres = useApi((signal) => (pills ? getLibraryGenres(id, signal) : Promise.resolve(undefined)), [id, pills])
  const getPage = albums ? getLibraryAlbums : getLibraryItems

  // Coming back (Back from a title) picks up where you were, unless something changed meanwhile.
  const snapshotKey = `library-${genrePage ? 'genre' : albums ? 'albums' : 'all'} ${id} ${sort} ${genre}`
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250)
    return () => clearTimeout(timer)
  }, [search])
  const { items, total, error, moreError, busy, jumping, jumpFailed, grid, empty, small, grown, loadedCount, onGridClick, loadMore, jump, restart } = useLibraryGrid({
    snapshotKey,
    query,
    pageSize: PAGE_SIZE,
    itemCount: library?.itemCount,
    load: (page, signal) => getPage(id, { search: query, sort, genre, ...page }, signal),
  })

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const letters = !sort && !query && items.length > 40

  if (!library) return null

  return (
    <div className="library-page">
      <header className="library-header">
        <div>
          {genrePage ? (
            <div className="library-heading">
              <Link className="genre-back" to={`/library/${library.id}?view=genres`}>
                <ChevronLeft /> {t(Words.genres)}
              </Link>
              <h1 className="library-title">{genre}</h1>
            </div>
          ) : (
            <div className="library-heading">
              <h1>{library.name}</h1>
              <ViewSwitch library={library} view={albums ? 'albums' : 'all'} />
            </div>
          )}
          <p className="muted">
            {total === undefined && (albums || genrePage) ? '\u00a0' : countLine(library, total, { albums, genrePage, genre, query })}
          </p>
          {albums && genre && (
            // A genre plays as a station: up to 200 of its tracks, in order or shuffled.
            <GenrePlay library={id} genre={genre} disabled={total === 0} />
          )}
        </div>
        <div className="library-tools">
          <input type="search" placeholder={t('Search {name}', { name: library.name })} aria-label={t('Search {name}', { name: library.name })} value={search} onChange={(e) => setSearch(e.target.value)} />
          <select aria-label={t('Sort by')} value={sort} onChange={(e) => setParam('sort', e.target.value)}>
            {(albums ? albumSorts : sorts).map((s) => (
              <option key={s.value} value={s.value}>
                {t(s.label)}
              </option>
            ))}
          </select>
        </div>
      </header>

      {genres.data && genres.data.length > 0 && (
        <div className="genre-chips" role="group" aria-label={t('Filter by genre')}>
          <button className="chip" aria-pressed={!genre} onClick={() => setParam('genre', '')}>
            {t('All')}
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
          {library.itemCount > (loadedCount ?? 0) ? t('New titles have arrived.') : t('Some titles have gone.')}{' '}
          <button className="button quiet small" onClick={restart}>
            {t('Refresh')}
          </button>
        </p>
      )}
      {error && <PageError error={error} onRetry={restart} />}
      {items.length === 0 && total === 0 && (
        <p className="page-message muted">
          {query
            ? t('Nothing in {name} matches “{query}”.', { name: library.name, query })
            : library.scan
              ? t('Titles appear here as the scan finds them.')
              : t('Nothing here yet.')}
        </p>
      )}

      <div className="library-body">
        {letters && <AlphaPicker jump={(l) => void jump(l)} jumping={jumping} failed={jumpFailed} empty={empty} />}
        <div
          className={library.type === 'Music' || library.type === 'Audiobooks' ? 'poster-grid square' : 'poster-grid'}
          ref={grid}
          onClick={onGridClick}
        >
          {items.map((item) => (
            <div key={item.id} data-id={item.id}>
              <PosterCard item={item} />
            </div>
          ))}
        </div>
        {letters && <AlphaRail jump={(l) => void jump(l)} jumping={jumping} failed={jumpFailed} empty={empty} />}
      </div>

      <ShowMore loaded={items.length} total={total} pageSize={PAGE_SIZE} busy={busy} error={moreError} onMore={() => void loadMore()} />
    </div>
  )
}
