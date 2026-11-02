import { Link } from 'react-router'
import { getMusicDiscover, getMusicGenre, imageUrl, searchMusic } from '../api/client'
import { MusicExternalCard } from '../components/MusicExternalCard'
import { Row } from '../components/Row'
import { useApi } from '../hooks/useApi'

/** Discover's two halves: films and shows (through Seerr), and music (through Lidarr). Only shown when music requests are on. */
export function DiscoverSwitch({ music }: { music: boolean }) {
  return (
    <nav className="view-switch" aria-label="Discover">
      <Link to="/discover" aria-current={music ? undefined : 'page'}>
        Movies and shows
      </Link>
      <Link to="/discover?view=music" aria-current={music ? 'page' : undefined}>
        Music
      </Link>
    </nav>
  )
}

/** Discover › Music: rows from Deezer matched against the library, then genre tiles. */
export function MusicDiscover() {
  const discover = useApi(getMusicDiscover, [])
  if (discover.error) return <p className="error">{discover.error.message}</p>
  if (!discover.data) return <p className="muted">Loading…</p>
  return (
    <div className="discover-external">
      {discover.data.rows.map((row) => (
        <Row key={row.id} title={row.title}>
          {row.items.map((item) => (
            <MusicExternalCard key={`${item.kind}-${item.deezerId}-${item.title}`} item={item} />
          ))}
        </Row>
      ))}
      {discover.data.genres.length > 0 && (
        <>
          <h2 className="section-title">Genres</h2>
          <div className="genre-grid">
            {discover.data.genres.map((g) => (
              <Link key={g.id} to={`/discover?view=music&genre=${g.id}`} className="genre-tile">
                {g.image && <img src={imageUrl(g.image, 780)} alt="" loading="lazy" />}
                <span className="genre-name">{g.name}</span>
              </Link>
            ))}
          </div>
        </>
      )}
      <p className="muted discover-credit">Data from Deezer</p>
    </div>
  )
}

/** A Deezer genre's best-known artists. */
export function MusicGenrePage({ id }: { id: string }) {
  const genre = useApi((signal) => getMusicGenre(id, signal), [id])
  return (
    <div className="discover-page">
      <header className="library-header">
        <div>
          <p className="detail-kicker">
            <Link to="/discover?view=music">Music</Link>
          </p>
          <h1>{genre.data?.name ?? ' '}</h1>
        </div>
      </header>
      {genre.error && <p className="error">{genre.error.message}</p>}
      <div className="poster-grid">
        {genre.data?.artists.map((a) => (
          <MusicExternalCard key={a.deezerId} item={a} />
        ))}
      </div>
    </div>
  )
}

/** Artists and albums anywhere (Deezer's), marked when you have them. */
export function MusicSearchResults({ query }: { query: string }) {
  const results = useApi((signal) => searchMusic(query, signal), [query])
  if (results.error) return <p className="error">{results.error.message}</p>
  if (!results.data) return <p className="muted">Searching…</p>
  const { artists, albums } = results.data
  if (artists.length === 0 && albums.length === 0) return <p className="muted">Nothing found for “{query}”.</p>
  return (
    <>
      {(
        [
          ['Artists', artists],
          ['Albums', albums],
        ] as const
      ).map(
        ([label, found]) =>
          found.length > 0 && (
            <section key={label} className="discover-results">
              <h2 className="settings-subhead">
                {label} <span className="health-count">{found.length}</span>
              </h2>
              <div className="poster-grid">
                {found.map((item) => (
                  <MusicExternalCard key={`${item.kind}-${item.deezerId}`} item={item} />
                ))}
              </div>
            </section>
          ),
      )}
    </>
  )
}
