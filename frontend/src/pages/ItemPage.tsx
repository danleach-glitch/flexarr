import { useMemo, useState, type CSSProperties } from 'react'
import { openAddToCollection } from '../components/AddToCollection'
import { Link, Navigate, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  analyzeItem,
  getItem,
  getItemPage,
  getSimilar,
  imageUrl,
  setWatched,
  type Credit,
  type ItemDetail,
  type ItemSummary,
  type MediaFile,
} from '../api/client'
import { Artwork } from '../components/Artwork'
import { AsyncButton } from '../components/AsyncButton'
import { PosterCard } from '../components/Cards'
import { Facts } from '../components/Hero'
import { Check, Film, More, Play, Shuffle } from '../components/icons'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { Row } from '../components/Row'
import { TrailerDialog } from '../components/TrailerDialog'
import { DeleteDialog } from '../components/DeleteDialog'
import { FixMatchDialog } from '../components/FixMatchDialog'
import { SubtitleSearchDialog } from '../components/SubtitleSearchDialog'
import { WatchedToggle } from '../components/WatchedToggle'
import { WatchlistButton } from '../components/WatchlistButton'
import { useSession } from '../hooks/sessionContext'
import { announceWatched } from '../hooks/watched'
import { useApi } from '../hooks/useApi'
import { AlbumPage, ArtistPage } from './MusicPages'
import {
  episodeHeading,
  formatBitrate,
  formatChannels,
  formatClock,
  formatDuration,
  formatSize,
  progressOf,
  pictureLabel,
} from '../lib/format'

export function ItemPage() {
  const { id = '' } = useParams()
  const [version, setVersion] = useState(0)
  const [params] = useSearchParams()
  // A series comes with its Up Next and the season to show (picking another season later fetches just that one).
  const { data, error } = useApi((signal) => getItemPage(id, params.get('season'), signal), [id, version])

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="detail-hero detail-loading" aria-busy="true" />

  // Seasons live on their series' page, tracks on their album's.
  if (data.item.kind === 'Season' && data.item.parentId) {
    return <Navigate to={`/item/${data.item.parentId}?season=${data.item.seasonNumber}`} replace />
  }
  if (data.item.kind === 'Track' && data.item.parentId) {
    return <Navigate to={`/item/${data.item.parentId}`} replace />
  }
  if (data.item.kind === 'Album') return <AlbumPage detail={data} />
  if (data.item.kind === 'Artist') return <ArtistPage detail={data} />

  return <Detail key={id} detail={data} onChange={() => setVersion((v) => v + 1)} />
}

function Detail({ detail, onChange }: { detail: ItemDetail; onChange: () => void }) {
  const { item, ancestors } = detail
  const series = ancestors[0]
  const navigate = useNavigate()
  const [trailer, setTrailer] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [matching, setMatching] = useState(false)
  const [findingSubtitles, setFindingSubtitles] = useState(false)
  const [subtitlesSaved, setSubtitlesSaved] = useState<string>()
  const [moreAt, setMoreAt] = useState<MenuPoint | null>(null)
  const { me } = useSession()
  // The main profile, locked or not: deleting asks for the PIN when it's locked (see UnlockPrompt).
  const mainProfile = me?.isAdmin === true || me?.canUnlock === true
  const canDelete = mainProfile && (item.kind === 'Movie' || item.kind === 'Series' || item.kind === 'Episode')
  const canFixMatch = mainProfile && (item.kind === 'Movie' || item.kind === 'Series')
  const nextUp = detail.nextUp ?? undefined
  const similar = useApi((signal) => (item.kind === 'Movie' || item.kind === 'Series' ? getSimilar(item.id, signal) : Promise.resolve([])), [item.id])

  const accent = item.accentColor ?? series?.accentColor
  const backdrop = item.artwork.backdrop ?? series?.artwork.backdrop
  const logo = item.kind === 'Episode' ? series?.artwork.logo : item.artwork.logo
  const progress = progressOf(item)
  // Play, the trailer and watched stay as buttons; everything else waits behind the ellipsis so the row doesn't wrap.
  const more: MenuAction[] = []
  if (progress !== null) more.push({ label: 'Play from the start', run: () => navigate(`/play/${item.id}?t=0`) })
  // Part way through: forget the place without marking it watched, which also takes it out of Continue Watching.
  if (progress !== null && !item.userData.watched) more.push({ label: 'Mark as unwatched', run: () => markUnwatched() })
  if (item.kind === 'Movie' || item.kind === 'Series') more.push({ label: 'Add to collection…', run: () => openAddToCollection(item) })
  if (detail.canSearchSubtitles) more.push({ label: 'Search for subtitles…', run: () => setFindingSubtitles(true) })
  if (canFixMatch) more.push({ label: 'Fix match…', run: () => setMatching(true) })
  if (canDelete) {
    if (more.length) more.push('divider')
    more.push({ label: 'Delete…', run: () => setDeleting(true), danger: true })
  }
  const best = detail.files[0]

  async function markUnwatched() {
    await setWatched(item.id, false)
    announceWatched(item)
    onChange()
  }

  async function toggleWatched() {
    await setWatched(item.id, !item.userData.watched)
    announceWatched(item)
    onChange()
  }

  const playTarget = item.kind === 'Series' ? nextUp : item
  const playLabel = (() => {
    if (item.kind === 'Series') {
      if (!nextUp) return 'Play'
      if (item.userData.watched) return `Watch again from ${episodeHeading(nextUp)}`
      const resume = nextUp.userData.positionSeconds > 0
      return `${resume ? 'Resume' : 'Play'} ${episodeHeading(nextUp)}`
    }
    return progress ? `Resume from ${formatClock(item.userData.positionSeconds)}` : 'Play'
  })()

  return (
    <article className="detail" style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}>
      <section className="detail-hero">
        <div className="detail-art" aria-hidden="true">
          {backdrop && <img src={imageUrl(backdrop, 1920)} alt="" />}
        </div>
        <div className="detail-hero-content">
          {item.kind === 'Episode' && series && (
            <p className="detail-kicker">
              <Link to={`/item/${series.id}?season=${item.seasonNumber}`}>{series.title}</Link>
              <span className="muted">{episodeHeading(item)}</span>
            </p>
          )}
          {logo && item.kind !== 'Episode' ? (
            <h1 className="detail-logo">
              <img src={imageUrl(logo, 780)} alt={item.title} />
            </h1>
          ) : (
            <h1 className="detail-title">{item.title}</h1>
          )}
          <Facts
            item={item}
            extra={[
              item.resolution,
              best ? pictureLabel(best) : null,
              item.kind === 'Episode' && item.releaseDate ? new Date(item.releaseDate).toLocaleDateString(undefined, { dateStyle: 'medium' }) : null,
            ]}
          />
          {detail.overview && <p className="detail-overview">{detail.overview}</p>}
          <div className="detail-actions">
            {playTarget && (
              <button className="button" onClick={() => navigate(`/play/${playTarget.id}`)}>
                <Play /> {playLabel}
              </button>
            )}
            {item.kind === 'Series' && item.childCount > 0 && (
              <button className="button quiet" onClick={() => navigate(`/play/${item.id}?shuffle=1`)}>
                <Shuffle /> Shuffle
              </button>
            )}
            {detail.trailerYouTubeId && (
              <button className="button quiet" onClick={() => setTrailer(true)}>
                <Film /> Trailer
              </button>
            )}
            <AsyncButton className="button quiet" aria-pressed={item.userData.watched} onClick={toggleWatched}>
              <Check /> {item.userData.watched ? 'Watched' : 'Mark as watched'}
            </AsyncButton>
            {detail.onWatchlist != null && <WatchlistButton titleKey={{ libraryItemId: item.id }} title={item.title} on={detail.onWatchlist} />}
            {more.length > 0 && (
              <button
                className="button quiet icon"
                aria-label="More actions"
                aria-haspopup="menu"
                aria-expanded={moreAt !== null}
                onClick={(e) => {
                  const r = e.currentTarget.getBoundingClientRect()
                  setMoreAt(moreAt ? null : { x: r.left, y: r.bottom + 6 })
                }}
              >
                <More />
              </button>
            )}
            {moreAt && <Menu label={`More for ${item.title}`} at={moreAt} actions={more} onClose={() => setMoreAt(null)} />}
          </div>
          {subtitlesSaved && (
            <p className="muted" role="status">
              {subtitlesSaved} subtitles saved. Choose them from the subtitles menu while it plays.
            </p>
          )}
          {progress !== null && (
            <span className="detail-progress" aria-label={`${Math.round(progress * 100)}% watched`}>
              <span style={{ width: `${progress * 100}%` }} />
            </span>
          )}
          {detail.genres.length > 0 && (
            <p className="detail-genres">
              {detail.genres.map((g) => (
                <Link key={g} className="chip" to={`/discover?genre=${encodeURIComponent(g)}`}>
                  {g}
                </Link>
              ))}
            </p>
          )}
        </div>
      </section>

      <div className="detail-body">
        {item.kind === 'Series' && <Seasons series={detail} onChange={onChange} />}
        {detail.credits.length > 0 && <Cast credits={detail.credits} studio={detail.studio} />}
        {similar.data && similar.data.length > 0 && (
          <Row title="More like this">
            {similar.data.map((s) => (
              <PosterCard key={s.id} item={s} />
            ))}
          </Row>
        )}
        {detail.files.length > 0 && <FileInfo files={detail.files} itemId={item.id} kind={item.kind} />}
      </div>

      {deleting && (
        <DeleteDialog
          item={item}
          onClose={() => setDeleting(false)}
          // Back to what it belonged to: an episode's show, or the library.
          onDeleted={() => navigate(item.kind === 'Episode' && series ? `/item/${series.id}?season=${item.seasonNumber}` : `/library/${item.libraryId}`, { replace: true })}
        />
      )}
      {matching && <FixMatchDialog item={item} onFixed={onChange} onClose={() => setMatching(false)} />}
      {findingSubtitles && (
        <SubtitleSearchDialog
          itemId={item.id}
          title={item.title}
          onDownloaded={(saved) => {
            setFindingSubtitles(false)
            setSubtitlesSaved(saved.label)
          }}
          onClose={() => setFindingSubtitles(false)}
        />
      )}
      {trailer && detail.trailerYouTubeId && <TrailerDialog videoId={detail.trailerYouTubeId} title={item.title} onClose={() => setTrailer(false)} />}
    </article>
  )
}

function Seasons({ series, onChange }: { series: ItemDetail; onChange: () => void }) {
  const [params, setParams] = useSearchParams()
  // Specials go last, as on most services.
  const seasons = useMemo(
    () => [...series.children].sort((a, b) => (a.seasonNumber === 0 ? 1 : 0) - (b.seasonNumber === 0 ? 1 : 0) || (a.seasonNumber ?? 0) - (b.seasonNumber ?? 0)),
    [series.children],
  )
  const requested = params.get('season')
  const firstUnwatched = seasons.find((s) => !s.userData.watched && s.seasonNumber !== 0)
  const selected = seasons.find((s) => String(s.seasonNumber) === requested) ?? firstUnwatched ?? seasons[0]
  // The season the series came with, unless another is picked. Changes reload the series, and its season with it.
  const embedded = series.season && series.season.item.id === selected?.id ? series.season : undefined
  const season = useApi(
    (signal) => (embedded ? Promise.resolve(embedded) : selected ? getItem(selected.id, signal) : Promise.resolve(undefined)),
    [selected?.id, embedded],
  )

  if (!selected) return null

  return (
    <section className="seasons">
      <div className="season-tabs" role="tablist" aria-label="Seasons">
        {seasons.map((s) => (
          <button
            key={s.id}
            role="tab"
            aria-selected={s.id === selected.id}
            className={s.id === selected.id ? 'season-tab active' : 'season-tab'}
            onClick={() => setParams({ season: String(s.seasonNumber) }, { replace: true })}
          >
            {s.homeVideo ? s.title : s.seasonNumber === 0 ? 'Specials' : `Season ${s.seasonNumber}`}
            {s.userData.watched && <Check aria-label="watched" />}
          </button>
        ))}
      </div>
      <div className="season-toolbar">
        <span className="muted">{season.data ? `${season.data.children.length} episodes` : ''}</span>
        <AsyncButton
          className="text-button"
          onClick={async () => {
            await setWatched(selected.id, !selected.userData.watched)
            announceWatched(selected)
            onChange()
          }}
        >
          {selected.userData.watched ? 'Mark season unwatched' : 'Mark season watched'}
        </AsyncButton>
      </div>
      <ol className="episode-list">
        {season.data?.children.map((episode) => (
          <Episode
            key={episode.id}
            episode={episode}
            overview={season.data?.childOverviews[episode.id] ?? null}
            onChange={onChange}
          />
        ))}
      </ol>
    </section>
  )
}

function Episode({ episode, overview, onChange }: { episode: ItemSummary; overview: string | null; onChange: () => void }) {
  const progress = progressOf(episode)

  return (
    <li className={episode.userData.watched ? 'episode watched' : 'episode'}>
      <div className="episode-still-wrap">
      <Link to={`/play/${episode.id}`} className="episode-still" aria-label={`Play ${episode.title}`}>
        <Artwork src={episode.artwork.thumb} fallback={episode.artwork.backdrop} title={episode.title} width={500} shape="wide" />
        <span className="episode-play" aria-hidden="true">
          <Play />
        </span>
        {progress !== null && (
          <span className="progress">
            <span style={{ width: `${progress * 100}%` }} />
          </span>
        )}
      </Link>
      <WatchedToggle key={`${episode.id}-${episode.userData.watched}`} item={episode} onChanged={onChange} />
      </div>
      <div className="episode-text">
        <h3>
          <span className="episode-number">{episode.airDate ? '' : episode.episodeNumberEnd ? `${episode.episodeNumber}–${episode.episodeNumberEnd}` : episode.episodeNumber}</span>
          <Link to={`/item/${episode.id}`}>{episode.title}</Link>
        </h3>
        <p className="episode-facts muted">
          {[formatDuration(episode.durationSeconds ?? (episode.runtimeMinutes ?? 0) * 60), episode.releaseDate && new Date(episode.releaseDate).toLocaleDateString(undefined, { dateStyle: 'medium' })]
            .filter(Boolean)
            .map((f) => (
              <span key={String(f)}>{f}</span>
            ))}
        </p>
        {overview && <p className="episode-overview">{overview}</p>}
      </div>
    </li>
  )
}

function Cast({ credits, studio }: { credits: Credit[]; studio: string | null }) {
  const cast = credits.filter((c) => c.role === 'Actor')
  const crew = credits.filter((c) => c.role !== 'Actor')
  const roles = { Director: 'Directed by', Writer: 'Written by', Creator: 'Created by', Producer: 'Produced by' } as const

  return (
    <section className="cast">
      {cast.length > 0 && (
        <Row title="Cast" style="people">
          {cast.map((c) => (
            <Link key={`${c.personId}-${c.character}`} to={`/person/${c.personId}`} className="card person-card">
              <div className="card-frame">
                <Artwork src={c.image} title={c.name} width={185} shape="portrait" />
              </div>
              <span className="card-title">{c.name}</span>
              {c.character && <span className="card-subtitle">{c.character}</span>}
            </Link>
          ))}
        </Row>
      )}
      {(crew.length > 0 || studio) && (
        <dl className="crew">
          {Object.entries(roles).map(([role, label]) => {
            const people = crew.filter((c) => c.role === role)
            return people.length ? (
              <div key={role}>
                <dt>{label}</dt>
                <dd>
                  {people.map((p, i) => (
                    <span key={p.personId}>
                      {i > 0 && ', '}
                      <Link to={`/person/${p.personId}`}>{p.name}</Link>
                    </span>
                  ))}
                </dd>
              </div>
            ) : null
          })}
          {studio && (
            <div>
              <dt>From</dt>
              <dd>{studio}</dd>
            </div>
          )}
        </dl>
      )}
    </section>
  )
}

function FileInfo({ files, itemId, kind }: { files: MediaFile[]; itemId: string; kind: string }) {
  const [queued, setQueued] = useState(false)
  return (
    <details className="file-info">
      <summary>{files.length > 1 ? `${files.length} versions on disk` : 'File and stream details'}</summary>
      {files.map((file) => (
        <div key={file.id} className="file">
          <code>{file.path}</code>
          {file.probeError && <p className="error">This file couldn't be read: {file.probeError}</p>}
          <dl>
            <dt>Video</dt>
            <dd>{[file.videoCodec?.toUpperCase(), file.width && `${file.width}×${file.height}`, pictureLabel(file)].filter(Boolean).join(', ') || 'None'}</dd>
            <dt>Audio</dt>
            <dd>{[file.audioCodec?.toUpperCase(), formatChannels(file.audioChannels)].filter(Boolean).join(', ') || 'None'}</dd>
            <dt>File</dt>
            <dd>{[file.container?.toUpperCase(), formatSize(file.size), formatBitrate(file.bitrate)].filter(Boolean).join(', ')}</dd>
            <dt>Subtitles</dt>
            <dd>
              {file.streams
                .filter((s) => s.type === 'Subtitle')
                .map((s) => s.language ?? 'unknown')
                .join(', ') || 'None embedded'}
            </dd>
          </dl>
        </div>
      ))}
      {kind === 'Episode' && (
        <AsyncButton
          className="text-button"
          disabled={queued}
          onClick={() => analyzeItem(itemId).then(() => setQueued(true))}
        >
          {queued ? 'Queued: intros and credits for this season will be re-detected shortly' : 'Re-detect intros and credits for this season'}
        </AsyncButton>
      )}
    </details>
  )
}

