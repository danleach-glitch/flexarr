import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type MouseEvent } from 'react'
import { Link, Navigate, useNavigationType, useParams, useSearchParams } from 'react-router'
import { forgetPage, freshly, getItem, getLibraryBrowse, getLibraryGenres, getLibraryItems, keepPage, keptPage, scanLibrary, wasDeleted, type ItemSummary, type Library } from '../api/client'
import { PosterCard } from '../components/Cards'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { ScanProgress } from '../components/ScanProgress'
import { useMusicPage } from '../hooks/audioPlayerContext'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges, watchedSince } from '../hooks/watched'

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
  // A music library keeps the now-playing bar on screen, ready for a pick.
  useMusicPage(library?.type === 'Music')
  if (!library) return <p className="page-message muted">{libraries.length ? 'This library no longer exists.' : 'Loading…'}</p>
  // Podcasts have pages of their own: what you follow, and Apple's directory to find more.
  if (library.type === 'Podcasts') return <Navigate to="/podcasts" replace />

  const all = params.get('view') === 'all' || params.has('sort') || params.has('genre')
  return all ? <LibraryAll key={id} /> : <LibraryBrowse key={id} library={library} />
}

function ViewSwitch({ library, all }: { library: Library; all: boolean }) {
  return (
    <nav className="view-switch" aria-label={`${library.name} views`}>
      <Link to={`/library/${library.id}`} aria-current={all ? undefined : 'page'}>
        Browse
      </Link>
      <Link to={`/library/${library.id}?view=all`} aria-current={all ? 'page' : undefined}>
        {{ Movies: 'All films', TvShows: 'All shows', Music: 'All artists', Podcasts: 'All podcasts' }[library.type]}
      </Link>
      {library.type === 'Music' && <Link to="/playlists">Playlists</Link>}
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
  const { data, error, reload } = useApi((signal) => getLibraryBrowse(library.id, signal), [library.id, itemCount])
  useWatchedChanges(reload)
  const focusReturn = useFocusReturn(`library-browse ${library.id}`, !!data && data.rows.length > 0)
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

  // A music library has no hero (covers are square); its heading stands on its own above the rows.
  if (data.featured.length === 0) {
    return (
      <>
        <div className="library-page">
          <header className="library-header">
            <div className="library-heading">{heading}</div>
          </header>
        </div>
        <div className="rows no-hero" {...focusReturn}>
          <HomeRows rows={data.rows} />
        </div>
      </>
    )
  }

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} top={heading} />
      <div className="rows" {...focusReturn}>
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

/**
 * What going back to a grid restores: the titles it had loaded, where it was scrolled to and the card that was opened
 * (for keyboard focus). The place is the first card on screen and how far it sat from the top (only cards near the
 * screen are laid out, so a pixel offset lands elsewhere once the grid is rebuilt), with scrollY in case that title
 * has gone. Only a grid that finished loading for its own sort and filter is kept.
 */
interface GridSnapshot {
  items: ItemSummary[]
  total: number
  itemCount: number | undefined
  scrollY: number
  anchor: { id: string; top: number } | undefined
  opened: string | undefined
  savedAt: number
}

/** A kept grid to go back to, unless it never finished loading or shows a title deleted since (start afresh then). */
function restorable(snapshot: GridSnapshot | undefined) {
  if (!snapshot || snapshot.total === undefined || snapshot.items.some((i) => wasDeleted(i.id))) return undefined
  return snapshot
}

/** The first card at least partly below the top of the screen (cards run top to bottom, so a binary search). */
function firstOnScreen(grid: HTMLElement | null) {
  const cards = grid ? (Array.from(grid.children) as HTMLElement[]) : []
  let low = 0
  let high = cards.length
  while (low < high) {
    const mid = (low + high) >> 1
    if (cards[mid].getBoundingClientRect().bottom > 0) high = mid
    else low = mid + 1
  }
  const card = cards[low]
  return card?.dataset.id ? { id: card.dataset.id, top: card.getBoundingClientRect().top } : undefined
}

/** The id of the grid card that holds keyboard focus, if one does. */
function focusedCard(grid: HTMLElement | null) {
  const focused = document.activeElement
  return focused && grid?.contains(focused) ? (focused.closest<HTMLElement>('[data-id]')?.dataset.id ?? undefined) : undefined
}

/** Whether an element is on screen or within half a screen of it. */
function nearScreen(element: Element) {
  const { top, bottom } = element.getBoundingClientRect()
  const margin = window.innerHeight / 2
  return bottom > -margin && top < window.innerHeight + margin
}

/**
 * Letters with nothing to jump to: none among the titles loaded, and either every title is loaded or (sorted A–Z) the
 * grid has already loaded past the letter. "#" also covers accented titles, which sort after Z, so it's only known once
 * everything is loaded. There's no list of letters from the server; this needs nothing more than the grid has.
 */
function emptyLetters(items: ItemSummary[], total: number | undefined) {
  const found = new Set(items.map(letterOf))
  const complete = total !== undefined && items.length >= total
  const last = items.length ? letterOf(items[items.length - 1]) : '#'
  return new Set(LETTERS.filter((l) => !found.has(l) && (complete || (l !== '#' && last !== '#' && last > l))))
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
  const [restored] = useState(() => (navigation === 'POP' ? restorable(keptPage<GridSnapshot>(snapshotKey)) : undefined))

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<ItemSummary[]>(restored?.items ?? [])
  const [total, setTotal] = useState<number | undefined>(restored?.total)
  // The library's size when the grid loaded, to notice titles a scan adds since.
  const [loadedCount, setLoadedCount] = useState<number | undefined>(restored?.itemCount)
  // The grid (sort, filter and search) the titles were loaded for: until a new one's first page lands, they're the old one's.
  const gridKey = `${snapshotKey} ${query}`
  const [loadedFor, setLoadedFor] = useState(restored ? gridKey : undefined)
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  // The letter being jumped to while the titles up to it load.
  const [jumping, setJumping] = useState<string>()
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
  // A restored grid follows from the count it was loaded with, so titles that came or went while away show up.
  const [followed, setFollowed] = useState(restored ? restored.itemCount : library?.itemCount)
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
    const loadingFor = gridKey
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
        setLoadedFor(loadingFor)
        setError(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- depsKey stands for the inputs; the count is read as it is
  }, [depsKey])

  // Marking something watched updates just the titles it touches, in place.
  const refresh = useCallback(
    (ids: string[]) => {
      const run = generation.current
      for (const changed of ids.filter((i) => items.some((item) => item.id === i))) {
        getItem(changed)
          .then((detail) => run === generation.current && setItems((current) => current.map((item) => (item.id === changed ? detail.item : item))))
          .catch(() => undefined)
      }
    },
    [items],
  )
  useWatchedChanges(refresh)

  // A restored grid goes back to where it was, once (searching or sorting later starts a new grid), and catches up
  // with whatever was marked watched while it was away.
  const restoredOnce = useRef(false)
  useEffect(() => {
    if (!restored || restoredOnce.current) return
    restoredOnce.current = true
    requestAnimationFrame(() => {
      const card = restored.anchor && grid.current?.querySelector<HTMLElement>(`[data-id="${restored.anchor.id}"]`)
      if (card && restored.anchor) {
        card.scrollIntoView({ block: 'start' })
        window.scrollTo(0, window.scrollY + card.getBoundingClientRect().top - restored.anchor.top)
      } else {
        window.scrollTo(0, restored.scrollY)
      }
      // Keyboard focus goes back to the card that was opened, so Tab carries on from there, unless the place restored
      // is somewhere else (the next Tab would scroll away to it).
      const opened = restored.opened ? grid.current?.querySelector<HTMLElement>(`[data-id="${restored.opened}"] a:not([tabindex="-1"])`) : undefined
      if (opened && nearScreen(opened)) opened.focus({ preventScroll: true })
    })
    const changed = watchedSince(restored.savedAt)
    if (changed.length) refresh(changed)
  }, [restored, refresh])

  // Remember the grid on the way out (not a search's results, nor one that hadn't loaded yet for its sort and filter:
  // that one starts afresh). A layout effect, so the cards are still on screen to measure. The card opened is the one
  // with focus then, else the one clicked to leave (Safari doesn't focus a link you click): only from this visit.
  const opened = useRef<string>(undefined)
  const latest = useRef({ items, total, itemCount: loadedCount, query, snapshotKey, current: loadedFor === gridKey })
  useEffect(() => {
    latest.current = { items, total, itemCount: loadedCount, query, snapshotKey, current: loadedFor === gridKey }
  })
  useLayoutEffect(() => {
    const element = grid.current
    return () => {
      const { query, snapshotKey, current, total, ...loaded } = latest.current
      if (query) return
      if (!current || total === undefined) return forgetPage(snapshotKey)
      const snapshot: GridSnapshot = { ...loaded, total, scrollY: window.scrollY, anchor: firstOnScreen(element), opened: focusedCard(element) ?? opened.current, savedAt: Date.now() }
      keepPage(snapshotKey, snapshot)
    }
  }, [])

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
        setJumping(letter)
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
      setJumping(undefined)
    }
    const target = loaded.find((i) => letterOf(i) === letter)
    if (target) {
      // Straight there rather than smoothly: cards skipped on the way only have estimated heights, and a smooth scroll
      // aims at where the target was before the cards it passes are laid out. Once there, cards drawn around it can
      // still nudge it, so settle once more.
      requestAnimationFrame(() => {
        const card = grid.current?.querySelector<HTMLElement>(`[data-id="${target.id}"]`)
        card?.scrollIntoView({ block: 'center' })
        // The card's keyboard-reachable link (the poster link is hidden from keyboard and screen readers).
        card?.querySelector<HTMLElement>('a:not([tabindex="-1"])')?.focus({ preventScroll: true })
        requestAnimationFrame(() => card?.scrollIntoView({ block: 'center' }))
      })
    }
  }

  function setParam(key: string, value: string) {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const empty = useMemo(() => emptyLetters(items, total), [items, total])
  const letters = !sort && !query && items.length > 40

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
            {(total ?? library.itemCount).toLocaleString()} {query || genre ? 'matching' : { Movies: 'films', TvShows: 'shows', Music: 'artists', Podcasts: 'podcasts' }[library.type]}
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
        {letters && (
          // Phones have no room for the rail: a compact picker instead, kept in reach while scrolling.
          <label className="alpha-picker">
            <span className="visually-hidden">Jump to letter</span>
            <select value="" onChange={(e) => {
                if (e.target.value) void jump(e.target.value)
              }}>
              <option value="">{jumping ? 'Loading…' : 'Jump to…'}</option>
              {LETTERS.map((l) => (
                <option key={l} value={l} disabled={empty.has(l)}>
                  {l}
                </option>
              ))}
            </select>
          </label>
        )}
        <div
          className={library.type === 'Music' ? 'poster-grid square' : 'poster-grid'}
          ref={grid}
          onClick={(e) => {
            const card = e.target instanceof Element ? e.target.closest('a')?.closest<HTMLElement>('[data-id]') : undefined
            if (card) opened.current = card.dataset.id
          }}
        >
          {items.map((item) => (
            <div key={item.id} data-id={item.id}>
              <PosterCard item={item} />
            </div>
          ))}
        </div>
        {letters && (
          <nav className="alpha-rail" aria-label="Jump to letter" aria-busy={jumping ? true : undefined}>
            {LETTERS.map((l) => (
              <button
                key={l}
                className={jumping === l ? 'jumping' : undefined}
                onClick={() => jump(l)}
                disabled={empty.has(l)}
                aria-label={l === '#' ? 'Numbers and symbols' : undefined}
              >
                {l}
                {jumping === l && (
                  <span className="alpha-loading" aria-hidden="true">
                    Loading…
                  </span>
                )}
              </button>
            ))}
          </nav>
        )}
        {letters && (
          <p className="visually-hidden" role="status">
            {jumping ? `Loading titles up to ${jumping === '#' ? 'numbers and symbols' : jumping}…` : ''}
          </p>
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
