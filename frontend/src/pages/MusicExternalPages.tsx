import { Link, useParams } from 'react-router'
import { getMusicAlbum, getMusicArtist, imageUrl, type MusicExternal } from '../api/client'
import { Artwork } from '../components/Artwork'
import { MusicExternalCard, MusicRequestButton } from '../components/MusicExternalCard'
import { PageError, PageLoading } from '../components/PageStatus'
import { Row } from '../components/Row'
import { useApi } from '../hooks/useApi'
import { formatClock, formatDuration } from '../lib/format'
import { t, tk, Words } from '../i18n'

/** Deezer's kinds of release, shelved as the library's artist page shelves them. */
const shelves: [string, (t: string | null | undefined) => boolean][] = [
  [Words.albums, (type) => !type || type === 'album'],
  [tk('EPs and singles'), (type) => type === 'ep' || type === 'single'],
  [tk('Compilations'), (type) => type === 'compile'],
]

/** Releases in a grid per kind, each with Request album. */
export function MusicShelves({ albums, showArtist = false, unanswered = false }: { albums: MusicExternal[]; showArtist?: boolean; unanswered?: boolean }) {
  return shelves.map(([title, holds]) => {
    const found = albums.filter((a) => holds(a.recordType))
    return (
      found.length > 0 && (
        <section key={title} className="music-section">
          <h2 className="music-section-title">{t(title)}</h2>
          <div className="poster-grid music-albums">
            {found.map((a, i) => (
              <MusicExternalCard key={`${i}-${a.deezerId}-${a.musicBrainzId ?? a.title}`} item={a} showArtist={showArtist} unanswered={unanswered} />
            ))}
          </div>
        </section>
      )
    )
  })
}

/** An artist the library may not have: Follow, their releases to request one by one, and artists like them. */
export function MusicExternalArtistPage() {
  const { id = '' } = useParams()
  const artist = useApi((signal) => getMusicArtist(id, signal), [id])
  // Deezer's answer can take a moment the first time: the shared spinner meanwhile.
  if (artist.error) return <PageError error={artist.error} onRetry={artist.reload} />
  if (!artist.data) return <PageLoading label={t('Loading artist')} />
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
            <Link to="/discover?view=music">{t(Words.music)}</Link>
          </p>
          <h1>{a.name}</h1>
          <div className="detail-actions">
            {a.libraryItemId && (
              <Link className="button" to={`/item/${a.libraryItemId}`}>
                {t(Words.inYourLibrary)}
              </Link>
            )}
            <MusicRequestButton key={String(a.followed)} kind="artist" size="large" request={{ kind: 'artist', deezerId: a.deezerId }} availability={a.followed ? 'Requested' : 'None'} title={a.name} unanswered={a.lidarrAnswered === false} />
          </div>
        </div>
      </header>
      <MusicShelves albums={a.albums} unanswered={a.lidarrAnswered === false} />
      {a.similar.length > 0 && (
        <Row title={t(Words.fansAlsoLike)}>
          {a.similar.map((s, i) => (
            <MusicExternalCard key={`${i}-${s.deezerId}`} item={s} unanswered={a.lidarrAnswered === false} />
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
  if (album.error) return <PageError error={album.error} onRetry={album.reload} />
  if (!album.data) return <PageLoading label={t('Loading album')} />
  const a = album.data
  const discs = new Set(a.tracks.map((t) => t.disc)).size
  const kind = a.recordType === 'ep' ? t('EP') : a.recordType === 'single' ? t('Single') : a.recordType === 'compile' ? t('Compilation') : null

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
          <p className="detail-kicker">
            <Link to="/discover?view=music">{t(Words.music)}</Link>
          </p>
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
            <span>{t('{count, plural, one {# track} other {# tracks}}', { count: a.tracks.length })}</span>
            {a.durationSeconds > 0 && <span>{formatDuration(a.durationSeconds)}</span>}
          </p>
          <div className="detail-actions">
            {a.libraryItemId ? (
              <Link className="button" to={`/item/${a.libraryItemId}`}>
                {t(Words.inYourLibrary)}
              </Link>
            ) : (
              <MusicRequestButton kind="album" size="large" request={{ kind: 'album', deezerId: a.deezerId }} availability={a.availability} title={a.title} unanswered={a.lidarrAnswered === false} />
            )}
          </div>
        </div>
      </header>
      <ol className="tracklist">
        {a.tracks.map((track, i) => (
          <li key={`${track.disc}-${track.number}-${i}`}>
            {discs > 1 && (i === 0 || a.tracks[i - 1].disc !== track.disc) && <h2 className="tracklist-disc">{t('Disc {number}', { number: track.disc })}</h2>}
            <div className="track-row">
              <span className="track">
                <span className="track-number">{track.number}</span>
                <span className="track-title">{track.title}</span>
                <span className="track-time">{track.durationSeconds ? formatClock(track.durationSeconds) : ''}</span>
              </span>
            </div>
          </li>
        ))}
      </ol>
    </article>
  )
}
