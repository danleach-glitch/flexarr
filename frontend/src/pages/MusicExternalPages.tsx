import { Link, useParams } from 'react-router'
import { getMusicAlbum, getMusicArtist, imageUrl, type MusicExternal } from '../api/client'
import { Artwork } from '../components/Artwork'
import { MusicExternalCard, MusicRequestButton } from '../components/MusicExternalCard'
import { Row } from '../components/Row'
import { useApi } from '../hooks/useApi'
import { formatClock, formatDuration } from '../lib/format'

/** Deezer's kinds of release, shelved as the library's artist page shelves them. */
const shelves: [string, (t: string | null | undefined) => boolean][] = [
  ['Albums', (t) => !t || t === 'album'],
  ['EPs and singles', (t) => t === 'ep' || t === 'single'],
  ['Compilations', (t) => t === 'compile'],
]

/** Releases in a grid per kind, each with Request album. */
export function MusicShelves({ albums, showArtist = false }: { albums: MusicExternal[]; showArtist?: boolean }) {
  return shelves.map(([title, holds]) => {
    const found = albums.filter((a) => holds(a.recordType))
    return (
      found.length > 0 && (
        <section key={title} className="music-section">
          <h2 className="music-section-title">{title}</h2>
          <div className="poster-grid music-albums">
            {found.map((a) => (
              <MusicExternalCard key={`${a.deezerId}-${a.musicBrainzId ?? a.title}`} item={a} showArtist={showArtist} />
            ))}
          </div>
        </section>
      )
    )
  })
}

/** An artist the library may not have: Follow artist, their releases to request one by one, and artists like them. */
export function MusicExternalArtistPage() {
  const { id = '' } = useParams()
  const artist = useApi((signal) => getMusicArtist(id, signal), [id])
  if (artist.error) return <p className="error page-error">{artist.error.message}</p>
  if (!artist.data) return <p className="muted page-loading">Loading…</p>
  const a = artist.data

  return (
    <article className="music-page">
      <div className="music-glow" aria-hidden="true">
        {a.image && <img src={imageUrl(a.image, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover music-cover-round">
          <Artwork src={a.image} title={a.name} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <p className="detail-kicker">
            <Link to="/discover?view=music">Music</Link>
          </p>
          <h1>{a.name}</h1>
          <div className="detail-actions">
            {a.libraryItemId && (
              <Link className="button" to={`/item/${a.libraryItemId}`}>
                In your library
              </Link>
            )}
            <MusicRequestButton key={String(a.followed)} kind="artist" size="large" request={{ kind: 'artist', deezerId: a.deezerId }} availability={a.followed ? 'Requested' : 'None'} />
          </div>
        </div>
      </header>
      <MusicShelves albums={a.albums} />
      {a.similar.length > 0 && (
        <Row title="Fans also like">
          {a.similar.map((s) => (
            <MusicExternalCard key={s.deezerId} item={s} />
          ))}
        </Row>
      )}
    </article>
  )
}

/** An album the library may not have: its tracks, and Request album. */
export function MusicExternalAlbumPage() {
  const { id = '' } = useParams()
  const album = useApi((signal) => getMusicAlbum(id, signal), [id])
  if (album.error) return <p className="error page-error">{album.error.message}</p>
  if (!album.data) return <p className="muted page-loading">Loading…</p>
  const a = album.data
  const discs = new Set(a.tracks.map((t) => t.disc)).size
  const kind = a.recordType === 'ep' ? 'EP' : a.recordType === 'single' ? 'Single' : a.recordType === 'compile' ? 'Compilation' : null

  return (
    <article className="music-page">
      <div className="music-glow" aria-hidden="true">
        {a.image && <img src={imageUrl(a.image, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={a.image} title={a.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{a.title}</h1>
          <p className="facts">
            {a.artistDeezerId ? (
              <Link to={`/discover/music/artist/${a.artistDeezerId}`} className="music-artist-link">
                {a.artist}
              </Link>
            ) : (
              <span>{a.artist}</span>
            )}
            {kind && <span>{kind}</span>}
            {a.year && <span>{a.year}</span>}
            <span>{a.tracks.length === 1 ? '1 track' : `${a.tracks.length} tracks`}</span>
            {a.durationSeconds > 0 && <span>{formatDuration(a.durationSeconds)}</span>}
          </p>
          <div className="detail-actions">
            {a.libraryItemId ? (
              <Link className="button" to={`/item/${a.libraryItemId}`}>
                In your library
              </Link>
            ) : (
              <MusicRequestButton kind="album" size="large" request={{ kind: 'album', deezerId: a.deezerId }} availability={a.availability} />
            )}
          </div>
        </div>
      </header>
      <ol className="tracklist">
        {a.tracks.map((t, i) => (
          <li key={`${t.disc}-${t.number}-${i}`}>
            {discs > 1 && (i === 0 || a.tracks[i - 1].disc !== t.disc) && <h2 className="tracklist-disc">Disc {t.disc}</h2>}
            <div className="track-row">
              <span className="track">
                <span className="track-number">{t.number}</span>
                <span className="track-title">{t.title}</span>
                <span className="track-time">{t.durationSeconds ? formatClock(t.durationSeconds) : ''}</span>
              </span>
            </div>
          </li>
        ))}
      </ol>
    </article>
  )
}
