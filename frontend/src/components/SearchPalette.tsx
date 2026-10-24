import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { imageUrl, search, type SearchResults } from '../api/client'
import { episodeCode, year } from '../lib/format'
import { itemLink } from '../lib/links'
import { Search } from './icons'

interface Result {
  key: string
  group: string
  title: string
  detail: string | null
  image: string | undefined
  href: string
}

function flatten(results: SearchResults): Result[] {
  return [
    ...results.titles.map((t) => ({
      key: t.id,
      group: 'Titles',
      title: t.title,
      detail: [t.kind === 'Series' ? 'Series' : 'Film', year(t)].filter(Boolean).join(', '),
      image: imageUrl(t.artwork.poster, 92),
      href: itemLink(t),
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

/** ⌘K search across titles, people, episodes and genres, operable entirely from the keyboard. */
export function SearchPalette({ onClose }: { onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<Result[]>([])
  const [active, setActive] = useState(0)
  const [loading, setLoading] = useState(false)
  const navigate = useNavigate()
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

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') onClose()
    else if (e.key === 'ArrowDown') {
      e.preventDefault()
      setActive((a) => Math.min(results.length - 1, a + 1))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((a) => Math.max(0, a - 1))
    } else if (e.key === 'Enter' && results[active]) {
      navigate(results[active].href)
      onClose()
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
            placeholder="Films, shows, episodes, people, genres"
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
                return (
                  <button
                    key={r.key}
                    id={`result-${r.key}`}
                    role="option"
                    aria-selected={index === active}
                    className={index === active ? 'palette-result active' : 'palette-result'}
                    onMouseEnter={() => setActive(index)}
                    onClick={() => {
                      navigate(r.href)
                      onClose()
                    }}
                  >
                    <span className={`palette-thumb palette-thumb-${r.group.toLowerCase()}`}>{r.image && <img src={r.image} alt="" />}</span>
                    <span className="palette-text">
                      <span>{r.title}</span>
                      {r.detail && <span className="muted">{r.detail}</span>}
                    </span>
                  </button>
                )
              })}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
