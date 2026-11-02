import { Link } from 'react-router'
import { getMusicDiscover, getMusicGenre, imageUrl, searchMusic } from '../api/client'
import { MusicExternalCard } from '../components/MusicExternalCard'
import { PageError, PageLoading } from '../components/PageStatus'
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
  if (discover.error) return <PageError error={discover.error} message="Deezer didn’t answer." onRetry={discover.reload} />
  if (!discover.data) return <PageLoading />
  const { rows, genres } = discover.data
  if (rows.length === 0 && genres.length === 0) return <p className="muted discover-note">Nothing to suggest yet.</p>
  return (
    <>
      {rows.length > 0 && (
        <div className="discover-external">
          {rows.map((row) => (
            <Row key={row.id} title={row.title}>
              {row.items.map((item, i) => (
                <MusicExternalCard key={`${i}-${item.kind}-${item.deezerId}`} item={item} />
              ))}
            </Row>
          ))}
          {genres.length > 0 && <h2 className="section-title">Genres</h2>}
        </div>
      )}
      <div className="genre-grid">
        {genres.map((g) => (
          <Link key={g.id} to={`/discover?view=music&genre=${g.id}`} className="genre-tile">
            {g.image && <img src={imageUrl(g.image, 780)} alt="" loading="lazy" />}
            <span className="genre-name">{g.name}</span>
          </Link>
        ))}
      </div>
      <p className="muted discover-credit">Data from Deezer</p>
    </>
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
          <h1>{genre.data?.name ?? 'Genre'}</h1>
        </div>
      </header>
      {genre.error ? (
        <PageError error={genre.error} message="Deezer didn’t answer." onRetry={genre.reload} />
      ) : !genre.data ? (
        <PageLoading />
      ) : genre.data.artists.length === 0 ? (
        <p className="muted discover-note">No artists here yet.</p>
      ) : (
        <div className="poster-grid">
          {genre.data.artists.map((a, i) => (
            <MusicExternalCard key={`${i}-${a.deezerId}`} item={a} />
          ))}
        </div>
      )}
    </div>
  )
}

/** Artists and albums anywhere (Deezer's), marked when you have them. */
export function MusicSearchResults({ query }: { query: string }) {
  const results = useApi((signal) => searchMusic(query, signal), [query])
  if (results.error) return <PageError error={results.error} message="Deezer didn’t answer." onRetry={results.reload} />
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
                {found.map((item, i) => (
                  <MusicExternalCard key={`${i}-${item.kind}-${item.deezerId}`} item={item} />
                ))}
              </div>
            </section>
          ),
      )}
    </>
  )
}
