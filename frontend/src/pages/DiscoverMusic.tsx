import { Link } from 'react-router'
import { getMusicDiscover, getMusicGenre, imageUrl, searchMusic } from '../api/client'
import { MusicExternalCard } from '../components/MusicExternalCard'
import { PageError, PageLoading } from '../components/PageStatus'
import { Row } from '../components/Row'
import { useApi } from '../hooks/useApi'
import { t, Words } from '../i18n'

/** Discover's two halves: films and shows (through Seerr), and music (through Lidarr). Only shown when music requests are on. */
export function DiscoverSwitch({ music }: { music: boolean }) {
  return (
    <nav className="view-switch" aria-label={t(Words.discover)}>
      <Link to="/discover" aria-current={music ? undefined : 'page'}>
        {t(Words.moviesAndShows)}
      </Link>
      <Link to="/discover?view=music" aria-current={music ? 'page' : undefined}>
        {t(Words.music)}
      </Link>
    </nav>
  )
}

/** Discover › Music: rows from Deezer matched against the library, then genre tiles. */
export function MusicDiscover() {
  const discover = useApi(getMusicDiscover, [])
  if (discover.error) return <PageError error={discover.error} message={t('Deezer didn’t answer.')} onRetry={discover.reload} />
  if (!discover.data) return <PageLoading />
  const { rows, genres } = discover.data
  if (rows.length === 0 && genres.length === 0) return <p className="muted discover-note">{t('Nothing to suggest yet.')}</p>
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
          {genres.length > 0 && <h2 className="section-title">{t(Words.genres)}</h2>}
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
      <p className="muted discover-credit">{t('Data from Deezer')}</p>
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
            <Link to="/discover?view=music">{t(Words.music)}</Link>
          </p>
          <h1>{genre.data?.name ?? t('Genre')}</h1>
        </div>
      </header>
      {genre.error ? (
        <PageError error={genre.error} message={t('Deezer didn’t answer.')} onRetry={genre.reload} />
      ) : !genre.data ? (
        <PageLoading />
      ) : genre.data.artists.length === 0 ? (
        <p className="muted discover-note">{t('No artists here yet.')}</p>
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
  if (results.error) return <PageError error={results.error} message={t('Deezer didn’t answer.')} onRetry={results.reload} />
  if (!results.data) return <p className="muted">{t('Searching…')}</p>
  const { artists, albums } = results.data
  if (artists.length === 0 && albums.length === 0) return <p className="muted">{t('Nothing found for “{query}”.', { query })}</p>
  return (
    <>
      {(
        [
          ['artists', t(Words.artists), artists],
          ['albums', t(Words.albums), albums],
        ] as const
      ).map(
        ([key, label, found]) =>
          found.length > 0 && (
            <section key={key} className="discover-results">
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
