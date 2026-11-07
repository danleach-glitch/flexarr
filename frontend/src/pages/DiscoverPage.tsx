import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { getExternalDiscover, getGenreItems, getGenres, getMusicRequestStatus, imageUrl, searchEverything } from '../api/client'
import { ExternalCard } from '../components/ExternalCard'
import { Row } from '../components/Row'
import { PosterCard } from '../components/Cards'
import { useApi } from '../hooks/useApi'
import { DiscoverSwitch, MusicDiscover, MusicGenrePage, MusicSearchResults } from './DiscoverMusic'
import { t, Words } from '../i18n'
import '../styles/settings.lazy.css'

export function DiscoverPage() {
  const [params, setParams] = useSearchParams()
  const music = params.get('view') === 'music'
  const genre = params.get('genre')
  const kind = params.get('kind') ?? ''
  const musicStatus = useApi(getMusicRequestStatus, [])
  const offersMusic = musicStatus.data?.configured === true
  // Films and shows, unless Music is chosen and offered (an old link to Music, with music requests since turned off, lands here).
  const films = !music || musicStatus.data?.configured === false
  const genres = useApi((signal) => (films ? getGenres(signal) : Promise.resolve(undefined)), [films])
  const external = useApi((signal) => (films ? getExternalDiscover(signal) : Promise.resolve(undefined)), [films])
  const items = useApi((signal) => (genre && films ? getGenreItems(genre, kind || undefined, signal) : Promise.resolve(undefined)), [genre, kind, films])

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
  const results = useApi((signal) => (query && films ? searchEverything(query, signal) : Promise.resolve(undefined)), [query, films])

  // Music requests turned off (or this profile can't have them): Discover is films and shows.
  const showMusic = !films
  if (showMusic && genre) return <MusicGenrePage id={genre} />

  if (genre) {
    return (
      <div className="discover-page">
        <header className="library-header">
          <div>
            <p className="detail-kicker">
              <Link to="/discover">{t(Words.discover)}</Link>
            </p>
            <h1>{genre}</h1>
            <p className="muted">{items.data ? t('{count, plural, other {# titles, best rated first}}', { count: items.data.total }) : ' '}</p>
          </div>
          <div className="genre-chips" role="group" aria-label={t('Show')}>
            {[
              ['', t('Everything')],
              ['Movie', t(Words.movies)],
              ['Series', t(Words.shows)],
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

  const placeholder = music ? t('Search for any artist or album') : t('Search for any movie or show')
  const searchBox = (
    <input type="search" className="discover-search" placeholder={placeholder} aria-label={placeholder} value={typed} onChange={(e) => setTyped(e.target.value)} />
  )
  const switcher = offersMusic && <DiscoverSwitch music={music} />

  const heading = (
    <div className="library-heading">
      <h1>{t(Words.discover)}</h1>
      {switcher}
    </div>
  )

  if (showMusic) {
    return (
      <div className="discover-page">
        <header className="library-header">
          {heading}
          {searchBox}
        </header>
        {musicStatus.data?.reachable === false && <p className="muted discover-note">{t('Lidarr isn’t answering.')}</p>}
        {query ? <MusicSearchResults query={query} /> : <MusicDiscover />}
      </div>
    )
  }

  if (query) {
    return (
      <div className="discover-page">
        <header className="library-header">
          {heading}
          {searchBox}
        </header>
        {results.error && <p className="error">{results.error.message}</p>}
        {results.loading && !results.data && <p className="muted">{t('Searching…')}</p>}
        {results.data && results.data.length === 0 && <p className="muted">{t('Nothing found for “{query}”. Try a different spelling, or the original title.', { query })}</p>}
        {results.data && results.data.length > 0 && (
          <>
            <p className="muted discover-search-note">
              {results.data.filter((title) => title.libraryItemId).length > 0
                ? t('{count, plural, other {# already in your library. Request anything else to add it.}}', { count: results.data.filter((title) => title.libraryItemId).length })
                : t('Request anything to add it to your library.')}
            </p>
            {(
              [
                ['tv', t(Words.shows)],
                ['movie', t(Words.movies)],
              ] as const
            ).map(([type, label]) => {
              const found = results.data!.filter((title) => title.mediaType === type)
              return (
                found.length > 0 && (
                  <section key={type} className="discover-results">
                    <h2 className="settings-subhead">
                      {label} <span className="health-count">{found.length}</span>
                    </h2>
                    <div className="poster-grid">
                      {found.map((title) => (
                        <ExternalCard key={`${title.mediaType}-${title.tmdbId}`} title={title} />
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
        {heading}
        {searchBox}
      </header>
      {external.data && external.data.length > 0 && (
        <div className="discover-external">
          {external.data.map((row) => (
            <Row key={row.id} title={row.title}>
              {row.items.map((title) => (
                <ExternalCard key={`${title.mediaType}-${title.tmdbId}`} title={title} />
              ))}
            </Row>
          ))}
          <h2 className="section-title">{t('Your library by genre')}</h2>
        </div>
      )}
      <div className="genre-grid">
        {genres.data?.map((g) => (
          <Link key={g.name} to={`/discover?genre=${encodeURIComponent(g.name)}`} className="genre-tile">
            {g.backdrop && <img src={imageUrl(g.backdrop, 780)} alt="" loading="lazy" />}
            <span className="genre-name">{g.name}</span>
            <span className="genre-count">
              {g.movies && g.shows
                ? t('{movies, plural, other {# movies}} and {shows, plural, other {# shows}}', { movies: g.movies, shows: g.shows })
                : g.movies
                  ? t('{count, plural, other {# movies}}', { count: g.movies })
                  : g.shows
                    ? t('{count, plural, other {# shows}}', { count: g.shows })
                    : ''}
            </span>
          </Link>
        ))}
      </div>
    </div>
  )
}
