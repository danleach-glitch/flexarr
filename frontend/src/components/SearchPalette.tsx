import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import {
  clearRecentSearches,
  forgetRecentItem,
  forgetSearch,
  getMusicRequestStatus,
  getRecentSearches,
  getRequestStatus,
  imageUrl,
  recordSearch,
  search,
  searchEverything,
  searchMusic,
  type ExternalTitle,
  type ItemSummary,
  type MusicExternal,
  type RecentSearches,
  type SearchResults,
} from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useModal } from '../hooks/useModal'
import { authorsLine, isBook } from '../lib/books'
import { episodeCode, year } from '../lib/format'
import { itemLink, libraryGenreLink } from '../lib/links'
import { musicLink } from '../lib/musicLinks'
import { Close, History, Search } from './icons'
import { icon } from './icons.generated'
import { t, Words } from '../i18n'

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

function titleDetail(item: ItemSummary): string {
  if (item.kind === 'Artist') return t('{count, plural, one {# album} other {# albums}}', { count: item.childCount })
  if (item.kind === 'Audiobook') return authorsLine(item) ?? t('Audiobook')
  return [item.kind === 'Series' ? t(Words.series) : t('Movie'), year(item)].filter(Boolean).join(', ')
}

function flatten(results: SearchResults): Result[] {
  return [
    ...results.titles.map((title) => ({
      key: title.id,
      group: t('Titles'),
      title: title.title,
      detail: titleDetail(title),
      image: imageUrl(title.artwork.poster, 92),
      href: itemLink(title),
      thumb: 'titles',
      itemId: title.id,
    })),
    ...results.albums.map((a) => ({
      key: a.id,
      group: t(Words.albums),
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
      group: t('Tracks'),
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
      group: t(Words.audiobooks),
      title: b.title,
      detail: authorsLine(b),
      image: imageUrl(b.artwork.poster, 92),
      href: itemLink(b),
      thumb: 'albums',
      itemId: b.id,
    })),
    ...(results.bookSeries ?? []).map((series) => ({
      key: `bs-${series.id}`,
      group: t('Book series'),
      title: series.name,
      detail: t('{count, plural, one {# book} other {# books}}', { count: series.bookCount }),
      image: undefined,
      href: `/book-series/${series.id}`,
      thumb: 'genres',
      itemId: null,
    })),
    ...results.people.map((p) => ({
      key: p.id,
      group: t('People'),
      title: p.name,
      detail: t('{count, plural, one {# title in your library} other {# titles in your library}}', { count: p.credits }),
      image: imageUrl(p.image, 92),
      href: `/person/${p.id}`,
      thumb: 'people',
      itemId: null,
    })),
    ...results.episodes.map((e) => ({
      key: e.id,
      group: t('Episodes'),
      title: e.title,
      detail: episodeCode(e),
      image: imageUrl(e.artwork.thumb ?? e.artwork.poster, 185),
      href: `/item/${e.id}`,
      thumb: 'episodes',
      itemId: e.id,
    })),
    ...genreResults(results),
  ]
}

/** A genre opens its library's own genre: a library of each kind, the fullest first. Older servers only send the name. */
function genreResults(results: SearchResults): Result[] {
  const places = results.genreLibraries ?? []
  return results.genres.flatMap((g): Result[] => {
    const here = places.filter((p) => p.name === g)
    if (here.length === 0) {
      return [{ key: `g-${g}`, group: t(Words.genres), title: g, detail: null, image: undefined, href: `/discover?genre=${encodeURIComponent(g)}`, thumb: 'genres', itemId: null }]
    }
    return here.map((p) => ({
      key: `g-${g}-${p.libraryId}`,
      group: t(Words.genres),
      title: g,
      detail: p.library,
      image: undefined,
      href: libraryGenreLink(p.libraryId, p.type, g),
      thumb: 'genres',
      itemId: null,
    }))
  })
}


/** Films and shows (Seerr's) and artists and albums (Deezer's) that the library doesn't have, opening their own pages. */
function outsideResults(titles: ExternalTitle[], music: { artists: MusicExternal[]; albums: MusicExternal[] }): Result[] {
  const wanted = <T extends { libraryItemId: string | null; availability: ExternalTitle['availability'] }>(x: T) => x.libraryItemId == null && x.availability !== 'Available'
  const requested = (x: { availability: ExternalTitle['availability'] }) => (x.availability === 'Requested' ? t(Words.requested) : null)
  const outside = t(Words.notInYourLibrary)
  return [
    ...titles.filter(wanted).slice(0, 6).map((title) => ({
      key: `x-${title.mediaType}-${title.tmdbId}`,
      group: outside,
      title: title.title,
      detail: [title.mediaType === 'tv' ? t(Words.series) : t('Movie'), title.year, requested(title)].filter(Boolean).join(', '),
      image: title.poster ? imageUrl(title.poster, 92) : undefined,
      href: `/discover/${title.mediaType}/${title.tmdbId}`,
      thumb: 'titles',
      itemId: null,
    })),
    ...[...music.artists.slice(0, 3), ...music.albums.slice(0, 3)].filter(wanted).flatMap((m): Result[] => {
      const href = musicLink(m)
      if (!href) return []
      return [
        {
          key: `x-${m.kind}-${m.deezerId}`,
          group: outside,
          title: m.title,
          detail: [m.kind === 'artist' ? t('Artist') : m.artist, m.kind === 'album' ? m.year : null, requested(m)].filter(Boolean).join(', '),
          image: m.image ? imageUrl(m.image, 92) : undefined,
          href,
          thumb: m.kind === 'artist' ? 'people' : 'albums',
          itemId: null,
        },
      ]
    }),
  ]
}

/** What shows before anything's typed: the profile's recent searches, then what they opened from them. */
function recentResults(recent: RecentSearches | null): Result[] {
  if (!recent) return []
  return [
    ...recent.queries.map((q) => ({ key: `q-${q}`, group: t('Recent searches'), title: q, detail: null, image: undefined, href: '', thumb: 'recent', itemId: null, recentQuery: q })),
    ...recent.items.map((item) => {
      const episode = item.kind === 'Episode'
      const book = isBook(item)
      const square = item.kind === 'Album' || item.kind === 'Track' || book
      return {
        key: `i-${item.id}`,
        group: t('Recently opened'),
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
/**
 * How long typing must pause before the library is asked. Long enough that a word typed at an ordinary speed is one
 * search rather than one per letter; a single letter matches nearly everything, so it waits until it's plainly the
 * whole search (a film can be called "M" or "X").
 */
function searchDelay(query: string) {
  return query.length < 2 ? 600 : 250
}

export function SearchPalette({ onClose }: { onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<Result[]>([])
  const [recent, setRecent] = useState<RecentSearches | null>(null)
  const [active, setActive] = useState(0)
  const [loading, setLoading] = useState(false)
  // The last query the library was asked for: until it's this one, "nothing matches" would be a guess.
  const [asked, setAsked] = useState('')
  // What's outside the library, for the query it was found for (it arrives after the library's own results).
  const [outside, setOutside] = useState<{ query: string; results: Result[] }>({ query: '', results: [] })
  const services = useRef<Promise<{ titles: boolean; music: boolean }>>(null)
  const navigate = useNavigate()
  const player = useAudioPlayer()
  const input = useRef<HTMLInputElement>(null)
  // Escape closes it, Tab stays inside it, and the focus goes back to whatever opened it.
  const dialog = useModal<HTMLDivElement>(onClose)

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
      setAsked(q)
      search(q, controller.signal)
        .then((r) => {
          setFound(flatten(r))
          setActive(0)
        })
        .catch(() => undefined)
        .finally(() => setLoading(false))
    }, searchDelay(q))
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [query])

  // Seerr and Deezer are slower, so they're asked once typing settles, and only where they're set up.
  useEffect(() => {
    const q = query.trim()
    if (q.length < 2) return
    const controller = new AbortController()
    const timer = setTimeout(async () => {
      services.current ??= Promise.all([
        getRequestStatus().then((s) => s.configured).catch(() => false),
        getMusicRequestStatus().then((s) => s?.configured === true).catch(() => false),
      ]).then(([titles, music]) => ({ titles, music }))
      const can = await services.current
      if (!can.titles && !can.music) return
      const [titles, music] = await Promise.all([
        can.titles ? searchEverything(q, controller.signal).catch(() => []) : [],
        can.music ? searchMusic(q, controller.signal).catch(() => ({ artists: [], albums: [] })) : { artists: [], albums: [] },
      ])
      if (!controller.signal.aborted) setOutside({ query: q, results: outsideResults(titles, music) })
    }, 400)
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [query])

  // The outside results go last, so the selection never jumps when they arrive.
  const results = useMemo(
    () => (query.trim() ? [...found, ...(outside.query === query.trim() ? outside.results : [])] : recentResults(recent)),
    [query, found, outside, recent],
  )

  // Arrowing past the bottom (or top) of the list brings the selection into view.
  const activeKey = results[active]?.key
  useEffect(() => {
    if (activeKey) document.getElementById(`result-${activeKey}`)?.scrollIntoView?.({ block: 'nearest' })
  }, [activeKey])

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

  // From the search field only: Enter on a tabbed-to Play or Remove button is that button's.
  function onKeyDown(e: React.KeyboardEvent) {
    if (e.target !== input.current) return
    if (e.key === 'ArrowDown') {
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
      <div ref={dialog} className="palette" role="dialog" aria-modal="true" aria-label={t(Words.search)} onMouseDown={(e) => e.stopPropagation()} onKeyDown={onKeyDown}>
        <label className="palette-input">
          <Search />
          <input
            ref={input}
            type="search"
            placeholder={t('Movies, shows, music, books, people, genres')}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value)
              if (!e.target.value.trim()) setActive(0)
            }}
            aria-label={t('Search your library')}
            aria-activedescendant={results[active] ? `result-${results[active].key}` : undefined}
            aria-controls="search-results"
          />
          <kbd>{t('esc')}</kbd>
        </label>
        <div className="palette-results" id="search-results" role="listbox">
          {query.trim() && asked === query.trim() && !loading && results.length === 0 && <p className="palette-empty">{t('Nothing in your library matches “{query}”.', { query: query.trim() })}</p>}
          {groups.map(([group, items], g) => (
            <div key={group} role="group" aria-label={group}>
              {!query.trim() && g === 0 ? (
                <div className="palette-group-head">
                  <h3 className="palette-group">{group}</h3>
                  <button className="palette-clear" onClick={clear}>
                    {t('Clear')}
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
                      <button className="icon-button palette-play" aria-label={t('Play {title}', { title: r.title })} onClick={() => play(r, track)}>
                        <icon.play />
                      </button>
                    )}
                    {(r.recentQuery !== undefined || r.recentItem) && (
                      <button className="icon-button palette-play" aria-label={t('Remove {title} from recent searches', { title: r.title })} onClick={() => forget(r)}>
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
