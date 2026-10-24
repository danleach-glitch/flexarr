import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router'
import { getLibraryItems, scanLibrary, type ItemSummary } from '../api/client'
import { PosterCard } from '../components/PosterCard'
import { ScanProgress } from '../components/ScanProgress'
import { useLibraries } from '../hooks/librariesContext'
import { libraryTypeLabel } from '../lib/format'

const PAGE_SIZE = 120

export function LibraryPage() {
  const { id = '' } = useParams()
  const { libraries, reload: reloadLibraries } = useLibraries()
  const library = libraries.find((l) => l.id === id)

  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<ItemSummary[]>([])
  const [total, setTotal] = useState<number>()
  const [error, setError] = useState<string>()
  const loadedFor = useRef('')

  // Debounce typing into the search box.
  useEffect(() => {
    const timer = setTimeout(() => setQuery(search.trim()), 250)
    return () => clearTimeout(timer)
  }, [search])

  // Reload the first page when the library, the search, or the library's item count changes (e.g. during a scan).
  const itemCount = library?.itemCount
  useEffect(() => {
    const controller = new AbortController()
    getLibraryItems(id, { search: query, take: Math.max(PAGE_SIZE, loadedFor.current === `${id}|${query}` ? items.length : 0) }, controller.signal)
      .then((page) => {
        loadedFor.current = `${id}|${query}`
        setItems(page.items)
        setTotal(page.total)
        setError(undefined)
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- items.length only sizes the refresh
  }, [id, query, itemCount])

  async function loadMore() {
    const page = await getLibraryItems(id, { search: query, skip: items.length, take: PAGE_SIZE })
    setItems((current) => [...current, ...page.items])
    setTotal(page.total)
  }

  async function scan() {
    await scanLibrary(id)
    setTimeout(reloadLibraries, 2500)
  }

  if (!library) {
    return <p className="muted">{libraries.length ? 'Library not found.' : 'Loading…'}</p>
  }

  return (
    <>
      <header className="page-header">
        <div>
          <h1>{library.name}</h1>
          <p className="muted">
            {libraryTypeLabel[library.type]} · {total ?? library.itemCount} {query ? 'matches' : 'items'}
          </p>
        </div>
        <div className="page-actions">
          <input
            className="search"
            type="search"
            placeholder={`Search ${library.name}`}
            aria-label="Search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <button className="button secondary" onClick={scan} disabled={!!library.scan}>
            Scan
          </button>
        </div>
      </header>

      {library.scan && <ScanProgress scan={library.scan} />}
      {error && <p className="error">{error}</p>}

      {items.length === 0 && total === 0 && (
        <p className="muted">{query ? 'Nothing matches that search.' : library.scan ? 'Items appear here as they are found.' : 'This library is empty.'}</p>
      )}

      <div className="poster-grid">
        {items.map((item) => (
          <PosterCard key={item.id} item={item} />
        ))}
      </div>

      {total !== undefined && items.length < total && (
        <div className="load-more">
          <button className="button secondary" onClick={loadMore}>
            Show more ({(total - items.length).toLocaleString()} left)
          </button>
        </div>
      )}
    </>
  )
}
