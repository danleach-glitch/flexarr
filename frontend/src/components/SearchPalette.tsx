import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { imageUrl, search, type ItemSummary, type SearchResults } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { episodeCode, year } from '../lib/format'
import { itemLink } from '../lib/links'
import { Play, Search } from './icons'

interface Result {
  key: string
  group: string
  title: string
  detail: string | null
  image: string | undefined
  href: string
  /** A track: it can play from here, as well as open its album. */
  track?: ItemSummary
}

function titleDetail(t: ItemSummary): string {
  if (t.kind === 'Artist') return t.childCount === 1 ? '1 album' : `${t.childCount} albums`
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
    })),
    ...results.albums.map((a) => ({
      key: a.id,
      group: 'Albums',
      title: a.title,
      detail: [a.seriesTitle, a.year].filter(Boolean).join(', '),
      image: imageUrl(a.artwork.poster, 92),
      href: `/item/${a.id}`,
    })),
    // A track opens its album (it has no page of its own), with the artist and album named beneath it.
    ...results.tracks.map(({ item, album }) => ({
      key: item.id,
      group: 'Tracks',
      title: item.title,
      detail: [item.seriesTitle, album].filter(Boolean).join(', '),
      image: imageUrl(item.artwork.poster, 92),
      href: item.parentId ? `/item/${item.parentId}` : itemLink(item),
      track: item,
    })),
    ...results.people.map((p) => ({
      key: p.id,
      group: 'People',
      title: p.name,
      detail: p.credits === 1 ? '1 title in your library' : `${p.credits} titles in your library`,
      image: imageUrl(p.image, 92),
      href: `/person/${p.id}`,
    })),
    ...results.episodes.map((e) => ({
      key: e.id,
      group: 'Episodes',
      title: e.title,
      detail: episodeCode(e),
      image: imageUrl(e.artwork.thumb ?? e.artwork.poster, 185),
      href: `/item/${e.id}`,
    })),
    ...results.genres.map((g) => ({ key: `g-${g}`, group: 'Genres', title: g, detail: null, image: undefined, href: `/discover?genre=${encodeURIComponent(g)}` })),
  ]
}

/** ⌘K search across titles, albums, tracks, people, episodes and genres, operable entirely from the keyboard. */
export function SearchPalette({ onClose }: { onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<Result[]>([])
  const [active, setActive] = useState(0)
  const [loading, setLoading] = useState(false)
  const navigate = useNavigate()
  const player = useAudioPlayer()
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => input.current?.focus(), [])

  useEffect(() => {
    const q = query.trim()
    if (!q) {
      setResults([])
      return
    }
    const controller = new AbortController()
    const timer = setTimeout(() => {
      setLoading(true)
      search(q, controller.signal)
        .then((r) => {
          setResults(flatten(r))
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

  const groups = useMemo(() => {
    const map = new Map<string, Result[]>()
    for (const r of results) map.set(r.group, [...(map.get(r.group) ?? []), r])
    return [...map.entries()]
  }, [results])

  const open = (r: Result) => {
    navigate(r.href)
    onClose()
  }

  // Plays the track on its own, where you are: the palette closes but the page stays.
  const play = (track: ItemSummary) => {
    player.play([track], 0)
    onClose()
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
      if (e.shiftKey && track) play(track)
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
            placeholder="Films, shows, music, episodes, people, genres"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            aria-label="Search your library"
            aria-activedescendant={results[active] ? `result-${results[active].key}` : undefined}
            aria-controls="search-results"
          />
          <kbd>esc</kbd>
        </label>
        <div className="palette-results" id="search-results" role="listbox">
          {query.trim() && !loading && results.length === 0 && <p className="palette-empty">Nothing in your library matches “{query.trim()}”.</p>}
          {groups.map(([group, items]) => (
            <div key={group} role="group" aria-label={group}>
              <h3 className="palette-group">{group}</h3>
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
                      <span className={`palette-thumb palette-thumb-${r.group.toLowerCase()}`}>{r.image && <img src={r.image} alt="" />}</span>
                      <span className="palette-text">
                        <span>{r.title}</span>
                        {r.detail && <span className="muted">{r.detail}</span>}
                      </span>
                    </button>
                    {track && (
                      <button className="icon-button palette-play" aria-label={`Play ${r.title}`} onClick={() => play(track)}>
                        <Play />
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
