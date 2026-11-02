import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { getExternalDiscover, getGenreItems, getGenres, getMusicRequestStatus, imageUrl, searchEverything } from '../api/client'
import { ExternalCard } from '../components/ExternalCard'
import { Row } from '../components/Row'
import { PosterCard } from '../components/Cards'
import { useApi } from '../hooks/useApi'
import { DiscoverSwitch, MusicDiscover, MusicGenrePage, MusicSearchResults } from './DiscoverMusic'

export function DiscoverPage() {
  const [params, setParams] = useSearchParams()
  const music = params.get('view') === 'music'
  const genre = params.get('genre')
  const kind = params.get('kind') ?? ''
  const musicStatus = useApi(getMusicRequestStatus, [])
  const offersMusic = musicStatus.data?.configured === true
  const genres = useApi((signal) => (music ? Promise.resolve(undefined) : getGenres(signal)), [music])
  const external = useApi((signal) => (music ? Promise.resolve(undefined) : getExternalDiscover(signal)), [music])
  const items = useApi((signal) => (genre && !music ? getGenreItems(genre, kind || undefined, signal) : Promise.resolve(undefined)), [genre, kind, music])

  // Search goes beyond the library: anything on TMDB, so something new can be found and requested from here.
  const query = params.get('q') ?? ''
  const [typed, setTyped] = useState(query)
  // Debounced: searches once typing pauses (and at two letters or more); a newer search cancels the one in flight.
  useEffect(() => {
    const term = typed.trim().length >= 2 ? typed.trim() : ''
    const timer = setTimeout(() => {
      if (term !== query) setParams({ ...(music ? { view: 'music' } : {}), ...(term ? { q: term } : {}) }, { replace: true })
    }, 350)
    return () => clearTimeout(timer)
  }, [typed, query, setParams, music])
  const results = useApi((signal) => (query && !music ? searchEverything(query, signal) : Promise.resolve(undefined)), [query, music])

  if (music && genre) return <MusicGenrePage id={genre} />

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

  const placeholder = music ? 'Search for any artist or album' : 'Search for any movie or show'
  const searchBox = (
    <input type="search" className="discover-search" placeholder={placeholder} aria-label={placeholder} value={typed} onChange={(e) => setTyped(e.target.value)} />
  )
  const switcher = offersMusic && <DiscoverSwitch music={music} />

  if (music) {
    return (
      <div className="discover-page">
        <header className="library-header">
          <h1>Discover</h1>
          {searchBox}
        </header>
        {switcher}
        {query ? <MusicSearchResults query={query} /> : <MusicDiscover />}
      </div>
    )
  }

  if (query) {
    return (
      <div className="discover-page">
        <header className="library-header">
          <h1>Discover</h1>
          {searchBox}
        </header>
        {switcher}
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
      {switcher}
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
