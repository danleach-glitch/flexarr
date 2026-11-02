import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type MouseEvent } from 'react'
import { useNavigationType } from 'react-router'
import { forgetPage, freshly, getItem, keepPage, keptPage, wasDeleted, type ItemSummary, type Paged } from '../api/client'
import { useWatchedChanges, watchedSince } from '../hooks/watched'

export const LETTERS = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ']
const JUMP_PAGE = 500
const JUMP_PARALLEL = 4

/** A title's letter on the A–Z rail, ignoring a leading "The", "A" or "An" as the sort does. */
export function letterOf(item: ItemSummary) {
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

/**
 * A library's grid of titles, a page at a time: searching (`query`), Show more, jumping to a letter, refreshing when
 * a scan changes the library, and picking up where it was on Back. `snapshotKey` names the grid (its library, sort
 * and filters, not the search): a grid with the same key is restored on Back. `itemCount` is the library's size now.
 */
export function useLibraryGrid({ snapshotKey, query, pageSize, itemCount, load }: {
  snapshotKey: string
  query: string
  pageSize: number
  itemCount: number | undefined
  load: (page: { skip?: number; take: number }, signal?: AbortSignal) => Promise<Paged<ItemSummary>>
}) {
  const navigation = useNavigationType()
  const [restored] = useState(() => (navigation === 'POP' ? restorable(keptPage<GridSnapshot>(snapshotKey)) : undefined))

  const [items, setItems] = useState<ItemSummary[]>(restored?.items ?? [])
  const [total, setTotal] = useState<number | undefined>(restored?.total)
  // The library's size when the grid loaded, to notice titles a scan adds since.
  const [loadedCount, setLoadedCount] = useState<number | undefined>(restored?.itemCount)
  // The grid (sort, filter and search) the titles were loaded for: until a new one's first page lands, they're the old one's.
  const gridKey = `${snapshotKey} ${query}`
  const [loadedFor, setLoadedFor] = useState(restored ? gridKey : undefined)
  const [error, setError] = useState<Error>()
  const [busy, setBusy] = useState(false)
  // The letter being jumped to while the titles up to it load.
  const [jumping, setJumping] = useState<string>()
  const [version, setVersion] = useState(0)
  // Bumped whenever the grid starts over, so pages still arriving for the old one are dropped.
  const generation = useRef(0)
  const grid = useRef<HTMLDivElement>(null)
  const loader = useRef(load)
  useEffect(() => {
    loader.current = load
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
        setTotal(page.total)
        setLoadedCount(countAtLoad)
        setLoadedFor(loadingFor)
        setError(undefined)
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
      const page = await loader.current({ skip: items.length, take: pageSize })
      if (run !== generation.current) return
      setItems((current) => append(current, [page.items]))
      setTotal(page.total)
    } catch (e) {
      if (run === generation.current) setError(e instanceof Error ? e : new Error(String(e)))
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
        const pages = await Promise.all(skips.map((skip) => loader.current({ skip, take: JUMP_PAGE })))
        if (run !== generation.current) return
        const next = append(loaded, pages.map((p) => p.items))
        if (next.length === loaded.length) break
        loaded = next
        setItems(loaded)
      }
    } catch (e) {
      if (run === generation.current) setError(e instanceof Error ? e : new Error(String(e)))
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

  const empty = useMemo(() => emptyLetters(items, total), [items, total])
  /** Remembers which card was clicked to leave the grid (Safari doesn't focus a link you click). */
  const onGridClick = (e: MouseEvent) => {
    const card = e.target instanceof Element ? e.target.closest('a')?.closest<HTMLElement>('[data-id]') : undefined
    if (card) opened.current = card.dataset.id
  }

  return {
    items, total, error, busy, jumping, grid, empty, small, grown, loadedCount, onGridClick, loadMore, jump,
    restart: () => setVersion((v) => v + 1),
  }
}

/** The A–Z picker for phones (no room for the rail), kept in reach while scrolling. */
export function AlphaPicker({ jump, jumping, empty }: { jump: (letter: string) => void; jumping: string | undefined; empty: Set<string> }) {
  return (
    <label className="alpha-picker">
      <span className="visually-hidden">Jump to letter</span>
      <select
        value=""
        onChange={(e) => {
          if (e.target.value) jump(e.target.value)
        }}
      >
        <option value="">{jumping ? 'Loading…' : 'Jump to…'}</option>
        {LETTERS.map((l) => (
          <option key={l} value={l} disabled={empty.has(l)}>
            {l}
          </option>
        ))}
      </select>
    </label>
  )
}

/** The A–Z rail beside a grid, and what it says to screen readers while a jump loads. */
export function AlphaRail({ jump, jumping, empty }: { jump: (letter: string) => void; jumping: string | undefined; empty: Set<string> }) {
  return (
    <>
      <nav className="alpha-rail" aria-label="Jump to letter" aria-busy={jumping ? true : undefined}>
        {LETTERS.map((l) => (
          <button key={l} className={jumping === l ? 'jumping' : undefined} onClick={() => jump(l)} disabled={empty.has(l)} aria-label={l === '#' ? 'Numbers and symbols' : undefined}>
            {l}
            {jumping === l && (
              <span className="alpha-loading" aria-hidden="true">
                Loading…
              </span>
            )}
          </button>
        ))}
      </nav>
      <p className="visually-hidden" role="status">
        {jumping ? `Loading titles up to ${jumping === '#' ? 'numbers and symbols' : jumping}…` : ''}
      </p>
    </>
  )
}
