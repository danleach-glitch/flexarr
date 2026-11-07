import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent } from 'react'
import { useNavigationType } from 'react-router'
import { forgetPage, freshly, getItem, keepPage, keptPage, wasDeleted, type ItemSummary, type LetterRun, type Paged } from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { useWatchedChanges, watchedSince } from '../hooks/watched'
import { t } from '../i18n'

export const LETTERS = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ']
/** A letter jump's page: the most the films, shows and music lists give at once. A list that gives fewer says so (`jumpPageSize`). */
const JUMP_PAGE = 500
const JUMP_PARALLEL = 4

/** A title's letter on the A–Z rail, ignoring a leading "The", "A" or "An" as the sort does. */
export function letterOf(item: ItemSummary) {
  const first = item.title.replace(/^(the|a|an)\s+/i, '').charAt(0).toUpperCase()
  return /[A-Z]/.test(first) ? first : '#'
}

/**
 * What going back to a grid restores: the titles it had loaded (from `start` in the list, after a letter jump), where
 * it was scrolled to and the card that was opened (for keyboard focus). The place is the first card on screen and how far it sat from the top (only cards near the
 * screen are laid out, so a pixel offset lands elsewhere once the grid is rebuilt), with scrollY in case that title
 * has gone. Only a grid that finished loading for its own sort and filter is kept.
 */
interface GridSnapshot {
  items: ItemSummary[]
  /** Where `items` start in the list. Absent from snapshots kept before letter jumps could start part way. */
  start?: number
  /** Where each letter starts, if the server said (null: it can't). */
  runs?: LetterRun[] | null
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

/** Puts a page before a grid's titles, skipping those it already has. */
function prepend(loaded: ItemSummary[], page: ItemSummary[]) {
  const seen = new Set(loaded.map((i) => i.id))
  return [...page.filter((i) => !seen.has(i.id)), ...loaded]
}

/**
 * A library's grid of titles, a page at a time: searching (`query`), Show more, jumping to a letter, refreshing when
 * a scan changes the library, and picking up where it was on Back. `snapshotKey` names the grid (its library, sort
 * and filters, not the search): a grid with the same key is restored on Back. `itemCount` is the library's size now.
 * `jumpPageSize` is the most titles `load` gives in one page: a letter jump asks for pages that size, side by side, so
 * asking for more than the server gives would leave the titles between them out.
 *
 * With `letters` (where each letter starts in the list, null when the server can't say), a jump far down the list
 * loads just the page it lands on rather than every title before it: the grid then starts part way (`start`), with
 * Show earlier above it. Without, or from a server that can't say, a jump loads everything up to its letter.
 */
export function useLibraryGrid({ snapshotKey, query, pageSize, jumpPageSize = JUMP_PAGE, itemCount, load, letters }: {
  snapshotKey: string
  query: string
  pageSize: number
  jumpPageSize?: number
  itemCount: number | undefined
  load: (page: { skip?: number; take: number }, signal?: AbortSignal) => Promise<Paged<ItemSummary>>
  letters?: (signal?: AbortSignal) => Promise<LetterRun[] | null>
}) {
  const navigation = useNavigationType()
  const [restored] = useState(() => (navigation === 'POP' ? restorable(keptPage<GridSnapshot>(snapshotKey)) : undefined))

  const [items, setItems] = useState<ItemSummary[]>(restored?.items ?? [])
  // Where `items` start in the list: 0 unless a letter jump went straight to a page further down.
  const [start, setStart] = useState(restored?.start ?? 0)
  // Where each letter starts, once a jump has asked (null: the server can't say).
  const [runs, setRuns] = useState<LetterRun[] | null | undefined>(restored?.runs)
  const [total, setTotal] = useState<number | undefined>(restored?.total)
  // The library's size when the grid loaded, to notice titles a scan adds since.
  const [loadedCount, setLoadedCount] = useState<number | undefined>(restored?.itemCount)
  // The grid (sort, filter and search) the titles were loaded for: until a new one's first page lands, they're the old one's.
  const gridKey = `${snapshotKey} ${query}`
  const [loadedFor, setLoadedFor] = useState(restored ? gridKey : undefined)
  const [error, setError] = useState<Error>()
  // Show more that failed: the titles already loaded stay, and Show more tries again.
  const [moreError, setMoreError] = useState<Error>()
  // The same for Show earlier.
  const [earlierError, setEarlierError] = useState<Error>()
  // A letter whose titles failed to load: the rail (or picker) says so beside it, and tries that letter again.
  const [jumpFailed, setJumpFailed] = useState<string>()
  const [busy, setBusy] = useState(false)
  // The letter being jumped to while the titles up to it load.
  const [jumping, setJumping] = useState<string>()
  const [version, setVersion] = useState(0)
  // Bumped whenever the grid starts over, so pages still arriving for the old one are dropped.
  const generation = useRef(0)
  const grid = useRef<HTMLDivElement>(null)
  const loader = useRef(load)
  const lettersLoader = useRef(letters)
  useEffect(() => {
    loader.current = load
    lettersLoader.current = letters
  })

  // While every title fits on one page, nothing is lost by starting over, so titles a scan finds just appear (the
  // count is followed during render, React's way to adjust state to a prop). Beyond that, starting over would throw
  // away "Show more" pages and the scroll position: offer it instead.
  const small = total !== undefined && total < pageSize && items.length >= total
  const grown = itemCount !== undefined && loadedCount !== undefined && itemCount !== loadedCount
  // A restored grid follows from the count it was loaded with, so titles that came or went while away show up.
  const [followed, setFollowed] = useState(restored ? restored.itemCount : itemCount)
  if (small && itemCount !== undefined && itemCount !== followed) {
    setFollowed(itemCount)
    setVersion((v) => v + 1)
  }

  const depsKey = `${gridKey}|${version}`
  const restoredFor = useRef(restored ? depsKey : null)
  useEffect(() => {
    if (restoredFor.current === depsKey) return
    restoredFor.current = null
    const run = ++generation.current
    const countAtLoad = itemCount
    const loadingFor = gridKey
    const controller = new AbortController()
    const first = () => loader.current({ take: pageSize }, controller.signal)
    // Starting over on purpose (Refresh, or new titles) skips any cached first page.
    const pending = version ? freshly(first) : first()
    pending
      .then((page) => {
        if (run !== generation.current) return
        setItems(page.items)
        setStart(0)
        setRuns(undefined)
        setTotal(page.total)
        setLoadedCount(countAtLoad)
        setLoadedFor(loadingFor)
        setError(undefined)
        setMoreError(undefined)
        setEarlierError(undefined)
        setJumpFailed(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e : new Error(String(e)))
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
  const latest = useRef({ items, start, runs, total, itemCount: loadedCount, query, snapshotKey, current: loadedFor === gridKey })
  useEffect(() => {
    latest.current = { items, start, runs, total, itemCount: loadedCount, query, snapshotKey, current: loadedFor === gridKey }
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
    setMoreError(undefined)
    try {
      const page = await loader.current({ skip: start + items.length, take: pageSize })
      if (run !== generation.current) return
      setItems((current) => append(current, [page.items]))
      setTotal(page.total)
    } catch (e) {
      if (run === generation.current) setMoreError(e instanceof Error ? e : new Error(String(e)))
    } finally {
      setBusy(false)
    }
  }

  // After Show earlier, the first title it brought, for keyboard focus: the button is disabled while it loads (which
  // drops focus) and goes once the list is complete, and Tab should carry on among the titles just shown.
  const earlierShown = useRef<string>(undefined)
  useLayoutEffect(() => {
    const id = earlierShown.current
    if (!id) return
    earlierShown.current = undefined
    const link = grid.current?.querySelector<HTMLElement>(`[data-id="${id}"] a:not([tabindex="-1"])`)
    if (!link) return
    // The page stays where it was (the new titles fill in under the button, where the viewer is looking), moving only
    // if that title is off screen.
    link.focus({ preventScroll: true })
    link.closest('[data-id]')?.scrollIntoView({ block: 'nearest' })
  })

  /** The page before the titles loaded, after a letter jump started the grid part way down. */
  async function loadEarlier() {
    if (busy || start === 0) return
    const run = generation.current
    setBusy(true)
    setEarlierError(undefined)
    try {
      const skip = Math.max(0, start - pageSize)
      const page = await loader.current({ skip, take: start - skip })
      if (run !== generation.current) return
      earlierShown.current = page.items.find((i) => !items.some((loaded) => loaded.id === i.id))?.id
      setItems((current) => prepend(current, page.items))
      setStart(skip)
      setTotal(page.total)
    } catch (e) {
      if (run === generation.current) setEarlierError(e instanceof Error ? e : new Error(String(e)))
    } finally {
      setBusy(false)
    }
  }

  /**
   * With where the letters start: the letter's first title, loading the page it's on unless it's already here. Close
   * after (or before) the titles loaded, the grid grows to it; further, the grid starts again from that page.
   */
  async function jumpTo(letter: string, at: LetterRun, run: number): Promise<ItemSummary | undefined> {
    const end = start + items.length
    if (at.offset >= start && at.offset < end) return items[at.offset - start]
    setJumping(letter)
    let next: ItemSummary[]
    let from: number
    if (at.offset >= end && at.offset + pageSize - end <= jumpPageSize) {
      const page = await loader.current({ skip: end, take: at.offset + pageSize - end })
      if (run !== generation.current) return undefined
      next = append(items, [page.items])
      from = start
      setTotal(page.total)
    } else if (at.offset < start && start - at.offset <= jumpPageSize) {
      const page = await loader.current({ skip: at.offset, take: start - at.offset })
      if (run !== generation.current) return undefined
      next = prepend(items, page.items)
      from = at.offset
      setTotal(page.total)
    } else {
      const page = await loader.current({ skip: at.offset, take: pageSize })
      if (run !== generation.current) return undefined
      next = page.items
      from = at.offset
      setTotal(page.total)
      setMoreError(undefined)
      setEarlierError(undefined)
    }
    setItems(next)
    setStart(from)
    return next[at.offset - from] ?? next[0]
  }

  /**
   * Scrolls to the letter's first title. Where the server says where letters start, only the page it's on is loaded;
   * otherwise everything up to the letter (a few big pages at a time).
   */
  async function jump(letter: string) {
    if (busy) return
    const run = generation.current
    setBusy(true)
    setJumpFailed(undefined)
    let target: ItemSummary | undefined
    try {
      let known = runs
      if (known === undefined && lettersLoader.current) {
        setJumping(letter)
        known = await lettersLoader.current()
        if (run !== generation.current) return
        setRuns(known)
      }
      if (known) {
        const at = known.find((r) => r.letter === letter)
        if (at) target = await jumpTo(letter, at, run)
      } else {
        target = await loadUpTo(letter, run)
      }
    } catch {
      if (run === generation.current) setJumpFailed(letter)
    } finally {
      setBusy(false)
      setJumping(undefined)
    }
    if (target) scrollToCard(target)
  }

  /** Loads everything up to the letter if needed (a few big pages at a time), and gives its first title. */
  async function loadUpTo(letter: string, run: number): Promise<ItemSummary | undefined> {
    // Sorted A–Z, so once the grid ends past the letter there's nothing with it to find.
    const passed = (loaded: ItemSummary[]) => {
      const last = loaded.length ? letterOf(loaded[loaded.length - 1]) : '#'
      return letter !== '#' && last !== '#' && last > letter
    }
    let loaded = items
    while (!loaded.some((i) => letterOf(i) === letter) && !passed(loaded) && total !== undefined && loaded.length < total) {
      setJumping(letter)
      const skips: number[] = []
      for (let skip = loaded.length; skip < total && skips.length < JUMP_PARALLEL; skip += jumpPageSize) skips.push(skip)
      const pages = await Promise.all(skips.map((skip) => loader.current({ skip, take: jumpPageSize })))
      if (run !== generation.current) return undefined
      const next = append(loaded, pages.map((p) => p.items))
      if (next.length === loaded.length) break
      loaded = next
      setItems(loaded)
    }
    return loaded.find((i) => letterOf(i) === letter)
  }

  function scrollToCard(target: ItemSummary) {
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

  // Once the server has said where letters start, the letters it didn't list are the empty ones.
  const empty = useMemo(() => (runs ? new Set(LETTERS.filter((l) => !runs.some((r) => r.letter === l))) : emptyLetters(items, total)), [runs, items, total])
  /** Remembers which card was clicked to leave the grid (Safari doesn't focus a link you click). */
  const onGridClick = (e: MouseEvent) => {
    const card = e.target instanceof Element ? e.target.closest('a')?.closest<HTMLElement>('[data-id]') : undefined
    if (card) opened.current = card.dataset.id
  }

  return {
    items, start, end: start + items.length, total, error, moreError, earlierError, busy, jumping, jumpFailed, grid, empty, small, grown, loadedCount,
    onGridClick, loadMore, loadEarlier, jump, restart: () => setVersion((v) => v + 1),
  }
}

/** Show more under a grid, while there's more; after one that failed, why, and Try again. */
export function ShowMore({ loaded, total, pageSize, busy, error, onMore }: {
  loaded: number
  total: number | undefined
  pageSize: number
  busy: boolean
  error: Error | undefined
  onMore: () => void
}) {
  if (total === undefined || loaded >= total) return null
  return (
    <div className="load-more">
      {error && (
        <p className="error" role="alert">
          {t('More couldn’t be loaded.')}
        </p>
      )}
      <AsyncButton className="button quiet" onClick={onMore} disabled={busy}>
        {error ? t('Try again') : t('Show {count} more', { count: Math.min(pageSize, total - loaded) })}
      </AsyncButton>
    </div>
  )
}

/** Show earlier above a grid that starts part way down its list (after a letter jump); after one that failed, why, and Try again. */
export function ShowEarlier({ start, pageSize, busy, error, onEarlier }: {
  start: number
  pageSize: number
  busy: boolean
  error: Error | undefined
  onEarlier: () => void
}) {
  if (start <= 0) return null
  return (
    <div className="load-more">
      {error && (
        <p className="error" role="alert">
          {t('Earlier titles couldn’t be loaded.')}
        </p>
      )}
      <AsyncButton className="button quiet" onClick={onEarlier} disabled={busy}>
        {error ? t('Try again') : t('Show {count} earlier', { count: Math.min(pageSize, start) })}
      </AsyncButton>
    </div>
  )
}

interface AlphaProps {
  jump: (letter: string) => void
  jumping: string | undefined
  /** The letter whose titles failed to load, if the last jump failed. */
  failed?: string
  empty: Set<string>
}

/** Why a jump to `letter` failed, and Try again for that letter. `short` words it to fit a phone's picker bar. */
function JumpFailed({ letter, jump, short }: { letter: string; jump: (letter: string) => void; short?: boolean }) {
  return (
    <p className="alpha-failed" role="alert">
      <span className="error">
        {short
          ? t('Couldn’t load {letter}.', { letter })
          : letter === '#'
            ? t('Numbers and symbols couldn’t be loaded.')
            : t('{letter} couldn’t be loaded.', { letter })}
      </span>
      <button type="button" className="button quiet small" onClick={() => jump(letter)}>
        {t('Try again')}
      </button>
    </p>
  )
}

/** The A–Z picker for phones (no room for the rail), kept in reach while scrolling. */
export function AlphaPicker({ jump, jumping, failed, empty }: AlphaProps) {
  return (
    <div className="alpha-picker">
      {failed && !jumping && <JumpFailed letter={failed} jump={jump} short />}
      <label>
        <span className="visually-hidden">{t('Jump to letter')}</span>
        <select
          value=""
          onChange={(e) => {
            if (e.target.value) jump(e.target.value)
          }}
        >
          <option value="">{jumping ? t('Loading…') : t('Jump to…')}</option>
          {LETTERS.map((l) => (
            <option key={l} value={l} disabled={empty.has(l)}>
              {l}
            </option>
          ))}
        </select>
      </label>
    </div>
  )
}

/** The A–Z rail beside a grid, and what it says to screen readers while a jump loads. */
export function AlphaRail({ jump, jumping, failed, empty }: AlphaProps) {
  return (
    <>
      <nav className="alpha-rail" aria-label={t('Jump to letter')} aria-busy={jumping ? true : undefined}>
        {LETTERS.map((l) => (
          <button key={l} className={jumping === l ? 'jumping' : undefined} onClick={() => jump(l)} disabled={empty.has(l)} aria-label={l === '#' ? t('Numbers and symbols') : undefined}>
            {l}
            {jumping === l && (
              <span className="alpha-loading" aria-hidden="true">
                {t('Loading…')}
              </span>
            )}
          </button>
        ))}
        {failed && !jumping && (
          <div className="alpha-failed-at" style={{ '--letter': LETTERS.indexOf(failed) } as CSSProperties}>
            <JumpFailed letter={failed} jump={jump} />
          </div>
        )}
      </nav>
      <p className="visually-hidden" role="status">
        {jumping ? (jumping === '#' ? t('Loading titles up to numbers and symbols…') : t('Loading titles up to {letter}…', { letter: jumping })) : ''}
      </p>
    </>
  )
}
