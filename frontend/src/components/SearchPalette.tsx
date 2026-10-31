import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import {
  clearRecentSearches,
  forgetRecentItem,
  forgetSearch,
  getRecentSearches,
  imageUrl,
  recordSearch,
  search,
  type ItemSummary,
  type RecentSearches,
  type SearchResults,
} from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { authorsLine, isBook } from '../lib/books'
import { episodeCode, year } from '../lib/format'
import { itemLink } from '../lib/links'
import { Close, History, Play, Search } from './icons'

interface Result {
  key: string
  group: string
  title: string
  detail: string | null
  image: string | undefined
  href: string
  /** Which thumbnail shape it gets: poster, still, round, square or none. */
  thumb: string
  /** What's remembered as opened when it's picked: the item, or nothing for a person or genre. */
  itemId: string | null
  /** A track: it can play from here, as well as open its album. */
  track?: ItemSummary
  /** A recent search: picking it searches again. */
  recentQuery?: string
  /** A recently opened item, which can be forgotten. */
  recentItem?: boolean
}

function titleDetail(t: ItemSummary): string {
  if (t.kind === 'Artist') return t.childCount === 1 ? '1 album' : `${t.childCount} albums`
  if (t.kind === 'Audiobook') return authorsLine(t) ?? 'Audiobook'
  return [t.kind === 'Series' ? 'Series' : 'Film', year(t)].filter(Boolean).join(', ')
}

function flatten(results: SearchResults): Result[] {
  return [
    ...results.titles.map((t) => ({
      key: t.id,
      group: 'Titles',
      title: t.title,
      detail: titleDetail(t),
      image: imageUrl(t.artwork.poster, 92),
      href: itemLink(t),
      thumb: 'titles',
      itemId: t.id,
    })),
    ...results.albums.map((a) => ({
      key: a.id,
      group: 'Albums',
      title: a.title,
      detail: [a.seriesTitle, a.year].filter(Boolean).join(', '),
      image: imageUrl(a.artwork.poster, 92),
      href: `/item/${a.id}`,
      thumb: 'albums',
      itemId: a.id,
    })),
    // A track opens its album (it has no page of its own), with the artist and album named beneath it.
    ...results.tracks.map(({ item, album }) => ({
      key: item.id,
      group: 'Tracks',
      title: item.title,
      detail: [item.seriesTitle, album].filter(Boolean).join(', '),
      image: imageUrl(item.artwork.poster, 92),
      href: item.parentId ? `/item/${item.parentId}` : itemLink(item),
      thumb: 'tracks',
      // The album is what opens, so it's what's remembered.
      itemId: item.parentId ?? item.id,
      track: item,
    })),
    // Audiobooks and their series, apart from films and shows (servers before 0.58 leave them out).
    ...(results.books ?? []).map((b) => ({
      key: b.id,
      group: 'Audiobooks',
      title: b.title,
      detail: authorsLine(b),
      image: imageUrl(b.artwork.poster, 92),
      href: itemLink(b),
      thumb: 'albums',
      itemId: b.id,
    })),
    ...(results.bookSeries ?? []).map((series) => ({
      key: `bs-${series.id}`,
      group: 'Book series',
      title: series.name,
      detail: series.bookCount === 1 ? '1 book' : `${series.bookCount} books`,
      image: undefined,
      href: `/book-series/${series.id}`,
      thumb: 'genres',
      itemId: null,
    })),
    ...results.people.map((p) => ({
      key: p.id,
      group: 'People',
      title: p.name,
      detail: p.credits === 1 ? '1 title in your library' : `${p.credits} titles in your library`,
      image: imageUrl(p.image, 92),
      href: `/person/${p.id}`,
      thumb: 'people',
      itemId: null,
    })),
    ...results.episodes.map((e) => ({
      key: e.id,
      group: 'Episodes',
      title: e.title,
      detail: episodeCode(e),
      image: imageUrl(e.artwork.thumb ?? e.artwork.poster, 185),
      href: `/item/${e.id}`,
      thumb: 'episodes',
      itemId: e.id,
    })),
    ...results.genres.map((g) => ({
      key: `g-${g}`,
      group: 'Genres',
      title: g,
      detail: null,
      image: undefined,
      href: `/discover?genre=${encodeURIComponent(g)}`,
      thumb: 'genres',
      itemId: null,
    })),
  ]
}

/** What shows before anything's typed: the profile's recent searches, then what they opened from them. */
function recentResults(recent: RecentSearches | null): Result[] {
  if (!recent) return []
  return [
    ...recent.queries.map((q) => ({ key: `q-${q}`, group: 'Recent searches', title: q, detail: null, image: undefined, href: '', thumb: 'recent', itemId: null, recentQuery: q })),
    ...recent.items.map((item) => {
      const episode = item.kind === 'Episode'
      const book = isBook(item)
      const square = item.kind === 'Album' || item.kind === 'Track' || book
      return {
        key: `i-${item.id}`,
        group: 'Recently opened',
        title: item.title,
        detail: episode
          ? [item.seriesTitle, episodeCode(item)].filter(Boolean).join(', ')
          : square && !book
            ? [item.seriesTitle, item.year].filter(Boolean).join(', ')
            : titleDetail(item),
        image: episode ? imageUrl(item.artwork.thumb ?? item.artwork.poster, 185) : imageUrl(item.artwork.poster, 92),
        href: itemLink(item),
        thumb: episode ? 'episodes' : square ? 'albums' : 'titles',
        itemId: item.id,
        recentItem: true,
      }
    }),
  ]
}

/**
 * ⌘K search across titles, albums, tracks, people, episodes and genres, operable entirely from the keyboard. Before
 * anything's typed it offers the profile's recent searches, shared with their other devices.
 */
export function SearchPalette({ onClose }: { onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<Result[]>([])
  const [recent, setRecent] = useState<RecentSearches | null>(null)
  const [active, setActive] = useState(0)
  const [loading, setLoading] = useState(false)
  const navigate = useNavigate()
  const player = useAudioPlayer()
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => input.current?.focus(), [])

  // Servers before 0.56 don't keep recent searches: then there are none to offer.
  useEffect(() => {
    const controller = new AbortController()
    getRecentSearches(controller.signal)
      .then(setRecent)
      .catch(() => undefined)
    return () => controller.abort()
  }, [])

  useEffect(() => {
    const q = query.trim()
    // Nothing typed shows the recent searches instead, whatever was found before.
    if (!q) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      setLoading(true)
      search(q, controller.signal)
        .then((r) => {
          setFound(flatten(r))
          setActive(0)
        })
        .catch(() => undefined)
        .finally(() => setLoading(false))
    }, 120)
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [query])

  const results = useMemo(() => (query.trim() ? found : recentResults(recent)), [query, found, recent])

  const groups = useMemo(() => {
    const map = new Map<string, Result[]>()
    for (const r of results) map.set(r.group, [...(map.get(r.group) ?? []), r])
    return [...map.entries()]
  }, [results])

  // Remembered only once something's picked, so half-typed words never show up on the profile's other devices.
  const remember = (itemId: string | null) => {
    const q = query.trim()
    if (q || itemId) recordSearch(q || null, itemId).catch(() => undefined)
  }

  const open = (r: Result) => {
    if (r.recentQuery !== undefined) {
      setQuery(r.recentQuery)
      setActive(0)
      input.current?.focus()
      return
    }
    remember(r.itemId)
    navigate(r.href)
    onClose()
  }

  // Plays the track on its own, where you are: the palette closes but the page stays.
  const play = (r: Result, track: ItemSummary) => {
    remember(r.itemId)
    player.play([track], 0)
    onClose()
  }

  const forget = (r: Result) => {
    if (!recent) return
    if (r.recentQuery !== undefined) {
      forgetSearch(r.recentQuery).catch(() => undefined)
      setRecent({ ...recent, queries: recent.queries.filter((q) => q !== r.recentQuery) })
    } else if (r.itemId) {
      forgetRecentItem(r.itemId).catch(() => undefined)
      setRecent({ ...recent, items: recent.items.filter((i) => i.id !== r.itemId) })
    }
    input.current?.focus()
  }

  const clear = () => {
    clearRecentSearches().catch(() => undefined)
    setRecent({ queries: [], items: [] })
    input.current?.focus()
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') onClose()
    else if (e.key === 'ArrowDown') {
      e.preventDefault()
      setActive((a) => Math.min(results.length - 1, a + 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((a) => Math.max(0, a - 1))
    } else if (e.key === 'Enter' && results[active]) {
      // Shift+Enter plays a track rather than opening its album.
      const { track } = results[active]
      if (e.shiftKey && track) play(results[active], track)
      else open(results[active])
    }
  }

  return (
    <div className="palette-backdrop" onMouseDown={onClose}>
      <div className="palette" role="dialog" aria-modal="true" aria-label="Search" onMouseDown={(e) => e.stopPropagation()} onKeyDown={onKeyDown}>
        <label className="palette-input">
          <Search />
          <input
            ref={input}
            type="search"
            placeholder="Films, shows, music, books, people, genres"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value)
              if (!e.target.value.trim()) setActive(0)
            }}
            aria-label="Search your library"
            aria-activedescendant={results[active] ? `result-${results[active].key}` : undefined}
            aria-controls="search-results"
          />
          <kbd>esc</kbd>
        </label>
        <div className="palette-results" id="search-results" role="listbox">
          {query.trim() && !loading && results.length === 0 && <p className="palette-empty">Nothing in your library matches “{query.trim()}”.</p>}
          {groups.map(([group, items], g) => (
            <div key={group} role="group" aria-label={group}>
              {!query.trim() && g === 0 ? (
                <div className="palette-group-head">
                  <h3 className="palette-group">{group}</h3>
                  <button className="palette-clear" onClick={clear}>
                    Clear
                  </button>
                </div>
              ) : (
                <h3 className="palette-group">{group}</h3>
              )}
              {items.map((r) => {
                const index = results.indexOf(r)
                const { track } = r
                return (
                  <div key={r.key} className="palette-row">
                    <button
                      id={`result-${r.key}`}
                      role="option"
                      aria-selected={index === active}
                      className={index === active ? 'palette-result active' : 'palette-result'}
                      onMouseEnter={() => setActive(index)}
                      onClick={() => open(r)}
                    >
                      <span className={`palette-thumb palette-thumb-${r.thumb}`}>
                        {r.recentQuery !== undefined ? <History /> : r.image && <img src={r.image} alt="" />}
                      </span>
                      <span className="palette-text">
                        <span>{r.title}</span>
                        {r.detail && <span className="muted">{r.detail}</span>}
                      </span>
                    </button>
                    {track && (
                      <button className="icon-button palette-play" aria-label={`Play ${r.title}`} onClick={() => play(r, track)}>
                        <Play />
                      </button>
                    )}
                    {(r.recentQuery !== undefined || r.recentItem) && (
                      <button className="icon-button palette-play" aria-label={`Remove ${r.title} from recent searches`} onClick={() => forget(r)}>
                        <Close />
                      </button>
                    )}
                  </div>
                )
              })}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
