import { useState } from 'react'
import { PageError } from '../components/PageStatus'
import { Link, Navigate, useParams } from 'react-router'
import { getExternalTitle, imageUrl, type ExternalDetail } from '../api/client'
import { Artwork } from '../components/Artwork'
import { ExternalCard } from '../components/ExternalCard'
import { Film } from '../components/icons'
import { RequestButton } from '../components/RequestButton'
import { WatchlistButton } from '../components/WatchlistButton'
import { Row } from '../components/Row'
import { TrailerDialog } from '../components/TrailerDialog'
import { useApi } from '../hooks/useApi'
import { formatDuration } from '../lib/format'
import { Overview } from '../components/Overview'
import { ScoreChips } from '../components/ScoreChips'
import { hasScores, scoresOf } from '../lib/scores'

/** A film or show from Discover that isn't in the library: what it is, how it's rated, its trailer, and a request. */
export function ExternalTitlePage() {
  const { mediaType = '', tmdbId = '' } = useParams()
  const { data, error, reload } = useApi((signal) => getExternalTitle(mediaType, tmdbId, signal), [mediaType, tmdbId])

  if (error) return <PageError error={error} message={error.message.includes('404') ? 'Flexarr can’t find that title.' : undefined} onRetry={reload} />
  if (!data) return <div className="detail-hero hero-loading" aria-busy="true" />
  // Something you have opens as itself.
  if (data.libraryItemId) return <Navigate to={`/item/${data.libraryItemId}`} replace />
  return <ExternalTitle key={`${mediaType}-${tmdbId}`} title={data} />
}

function ExternalTitle({ title }: { title: ExternalDetail }) {
  const [trailer, setTrailer] = useState(false)
  // Adding it to the watchlist can request it too (when this profile requests what it adds).
  const [requested, setRequested] = useState(false)
  const facts = [
    title.year?.toString(),
    title.mediaType === 'tv' ? (title.seasons ? (title.seasons === 1 ? '1 season' : `${title.seasons} seasons`) : null) : title.runtimeMinutes ? formatDuration(title.runtimeMinutes * 60) : null,
    title.mediaType === 'tv' ? 'Series' : 'Movie',
    title.status && !['Released', 'Ended'].includes(title.status) ? title.status : null,
  ].filter(Boolean)

  return (
    <article className="detail">
      <section className="detail-hero">
        <div className="detail-art" aria-hidden="true">
          {title.backdrop && <img src={imageUrl(title.backdrop, 1920)} alt="" />}
        </div>
        <div className="detail-hero-content">
          <h1 className="detail-title">{title.title}</h1>
          {title.tagline && <p className="detail-tagline">{title.tagline}</p>}
          <p className="facts">
            {title.certification && <span className="tag">{title.certification}</span>}
            {facts.map((f) => (
              <span key={f}>{f}</span>
            ))}
            <Scores title={title} />
          </p>
          {title.overview && <Overview text={title.overview} />}
          <div className="detail-actions">
            <RequestButton key={requested ? 'requested' : 'asked'} title={requested ? { ...title, availability: 'Requested' } : title} size="large" />
            {title.onWatchlist != null && (
              <WatchlistButton
                titleKey={{ mediaType: title.mediaType, tmdbId: title.tmdbId }}
                title={title.title}
                on={title.onWatchlist}
                onRequested={() => setRequested(true)}
              />
            )}
            {title.trailerYouTubeId && (
              <button className="button quiet" onClick={() => setTrailer(true)}>
                <Film /> Trailer
              </button>
            )}
          </div>
          {title.genres.length > 0 && (
            <p className="detail-genres">
              {title.genres.map((g) => (
                <Link key={g} className="chip" to={`/discover?genre=${encodeURIComponent(g)}`}>
                  {g}
                </Link>
              ))}
            </p>
          )}
        </div>
      </section>

      <div className="detail-body">
        {title.cast.length > 0 && (
          <section className="cast">
            <Row title="Cast" style="people">
              {title.cast.map((c) => (
                <div key={`${c.name}-${c.character}`} className="card person-card">
                  <div className="card-frame">
                    <Artwork src={c.image} title={c.name} width={185} shape="portrait" />
                  </div>
                  <span className="card-title">{c.name}</span>
                  {c.character && <span className="card-subtitle">{c.character}</span>}
                </div>
              ))}
            </Row>
            {(title.madeBy.length > 0 || title.studio) && (
              <dl className="crew">
                {title.madeBy.length > 0 && (
                  <div>
                    <dt>{title.mediaType === 'tv' ? 'Created by' : 'Directed by'}</dt>
                    <dd>{title.madeBy.join(', ')}</dd>
                  </div>
                )}
                {title.studio && (
                  <div>
                    <dt>{title.mediaType === 'tv' ? 'Network' : 'Studio'}</dt>
                    <dd>{title.studio}</dd>
                  </div>
                )}
              </dl>
            )}
          </section>
        )}
        {title.recommendations.length > 0 && (
          <Row title="If you like this">
            {title.recommendations.map((r) => (
              <ExternalCard key={`${r.mediaType}-${r.tmdbId}`} title={r} />
            ))}
          </Row>
        )}
      </div>

      {trailer && title.trailerYouTubeId && <TrailerDialog videoId={title.trailerYouTubeId} title={title.title} onClose={() => setTrailer(false)} />}
    </article>
  )
}

/** IMDb and Rotten Tomatoes beside their marks, as on a library title's page; else TMDB's score. */
function Scores({ title }: { title: ExternalDetail }) {
  const scores = scoresOf({
    imdbRating: title.imdbRating,
    rottenTomatoes: title.criticScore !== null ? { score: title.criticScore, state: title.rottenTomatoesState ?? null } : null,
  })
  if (hasScores(scores)) return <ScoreChips scores={scores} />
  if (title.tmdbRating === null) return null
  return (
    <span className="score">
      <span className="visually-hidden">Rated </span>
      {title.tmdbRating.toFixed(1)}
      <span className="muted">/10</span>
    </span>
  )
}
