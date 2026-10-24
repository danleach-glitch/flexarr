import { Link, useSearchParams } from 'react-router'
import { getExternalDiscover, getGenreItems, getGenres, imageUrl } from '../api/client'
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
              ['Movie', 'Films'],
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

  return (
    <div className="discover-page">
      <header className="library-header">
        <h1>Discover</h1>
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
              {[g.movies && `${g.movies} films`, g.shows && `${g.shows} shows`].filter(Boolean).join(' and ')}
            </span>
          </Link>
        ))}
      </div>
    </div>
  )
}
