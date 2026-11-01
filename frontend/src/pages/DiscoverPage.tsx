import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { getExternalDiscover, getGenreItems, getGenres, imageUrl, searchEverything } from '../api/client'
import { ExternalCard } from '../components/ExternalCard'
import { Row } from '../components/Row'
import { PosterCard } from '../components/Cards'
import { useApi } from '../hooks/useApi'

export function DiscoverPage() {
  const [params, setParams] = useSearchParams()
  const genre = params.get('genre')
  const kind = params.get('kind') ?? ''
  const genres = useApi(getGenres, [])
  const external = useApi(getExternalDiscover, [])
  const items = useApi((signal) => (genre ? getGenreItems(genre, kind || undefined, signal) : Promise.resolve(undefined)), [genre, kind])

  // Search goes beyond the library: anything on TMDB, so something new can be found and requested from here.
  const query = params.get('q') ?? ''
  const [typed, setTyped] = useState(query)
  // Debounced: searches once typing pauses (and at two letters or more); a newer search cancels the one in flight.
  useEffect(() => {
    const term = typed.trim().length >= 2 ? typed.trim() : ''
    const timer = setTimeout(() => {
      if (term !== query) setParams(term ? { q: term } : {}, { replace: true })
    }, 350)
    return () => clearTimeout(timer)
  }, [typed, query, setParams])
  const results = useApi((signal) => (query ? searchEverything(query, signal) : Promise.resolve(undefined)), [query])

  if (genre) {
    return (
      <div className="discover-page">
        <header className="library-header">
          <div>
            <p className="detail-kicker">
              <Link to="/discover">Discover</Link>
            </p>
            <h1>{genre}</h1>
            <p className="muted">{items.data ? `${items.data.total.toLocaleString()} titles, best rated first` : ' '}</p>
          </div>
          <div className="genre-chips" role="group" aria-label="Show">
            {[
              ['', 'Everything'],
              ['Movie', 'Movies'],
              ['Series', 'Shows'],
            ].map(([value, label]) => (
              <button key={value} className="chip" aria-pressed={kind === value} onClick={() => setParams(value ? { genre, kind: value } : { genre })}>
                {label}
              </button>
            ))}
          </div>
        </header>
        <div className="poster-grid">
          {items.data?.items.map((item) => (
            <PosterCard key={item.id} item={item} />
          ))}
        </div>
      </div>
    )
  }

  const searchBox = (
    <input
      type="search"
      className="discover-search"
      placeholder="Search for any movie or show"
      aria-label="Search for any movie or show"
      value={typed}
      onChange={(e) => setTyped(e.target.value)}
    />
  )

  if (query) {
    return (
      <div className="discover-page">
        <header className="library-header">
          <h1>Discover</h1>
          {searchBox}
        </header>
        {results.error && <p className="error">{results.error.message}</p>}
        {results.loading && !results.data && <p className="muted">Searching…</p>}
        {results.data && results.data.length === 0 && <p className="muted">Nothing found for “{query}”. Try a different spelling, or the original title.</p>}
        {results.data && results.data.length > 0 && (
          <>
            <p className="muted discover-search-note">
              {results.data.filter((t) => t.libraryItemId).length > 0
                ? `${results.data.filter((t) => t.libraryItemId).length} already in your library. Request anything else to add it.`
                : 'Request anything to add it to your library.'}
            </p>
            {(
              [
                ['tv', 'Shows'],
                ['movie', 'Movies'],
              ] as const
            ).map(([type, label]) => {
              const found = results.data!.filter((t) => t.mediaType === type)
              return (
                found.length > 0 && (
                  <section key={type} className="discover-results">
                    <h2 className="settings-subhead">
                      {label} <span className="health-count">{found.length}</span>
                    </h2>
                    <div className="poster-grid">
                      {found.map((t) => (
                        <ExternalCard key={`${t.mediaType}-${t.tmdbId}`} title={t} />
                      ))}
                    </div>
                  </section>
                )
              )
            })}
          </>
        )}
      </div>
    )
  }

  return (
    <div className="discover-page">
      <header className="library-header">
        <h1>Discover</h1>
        {searchBox}
      </header>
      {external.data && external.data.length > 0 && (
        <div className="discover-external">
          {external.data.map((row) => (
            <Row key={row.id} title={row.title}>
              {row.items.map((t) => (
                <ExternalCard key={`${t.mediaType}-${t.tmdbId}`} title={t} />
              ))}
            </Row>
          ))}
          <h2 className="section-title">Your library by genre</h2>
        </div>
      )}
      <div className="genre-grid">
        {genres.data?.map((g) => (
          <Link key={g.name} to={`/discover?genre=${encodeURIComponent(g.name)}`} className="genre-tile">
            {g.backdrop && <img src={imageUrl(g.backdrop, 780)} alt="" loading="lazy" />}
            <span className="genre-name">{g.name}</span>
            <span className="genre-count">
              {[g.movies && `${g.movies} movies`, g.shows && `${g.shows} shows`].filter(Boolean).join(' and ')}
            </span>
          </Link>
        ))}
      </div>
    </div>
  )
}
