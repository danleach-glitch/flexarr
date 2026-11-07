import { lazy, Suspense, useEffect, useMemo, useState, type CSSProperties } from 'react'
import { PageError } from '../components/PageStatus'
import { openAddToCollection } from '../components/AddToCollection'
import { Link, Navigate, useLocation, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  analyzeItem,
  getItem,
  backdropSources,
  getItemPage,
  getSimilar,
  getShowMissing,
  type ShowMissing,
  imageUrl,
  setKidsOverride,
  setWatched,
  type Credit,
  type ItemDetail,
  type ItemSummary,
  type KidsOverride,
  type MediaFile,
} from '../api/client'
import { Artwork } from '../components/Artwork'
import { AsyncButton } from '../components/AsyncButton'
import { PosterCard } from '../components/Cards'
import { Facts } from '../components/Hero'
import { icon } from '../components/icons.generated'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { Row } from '../components/Row'
import { TrailerDialog } from '../components/TrailerDialog'
import { DeleteDialog } from '../components/DeleteDialog'
import { DetailsEditor, FixMatchDialog } from '../components/onDemand'
import { SubtitleSearchDialog } from '../components/SubtitleSearchDialog'
import { WatchedToggle } from '../components/WatchedToggle'
import { Overview } from '../components/Overview'
import { WatchlistButton } from '../components/WatchlistButton'
import { ShowGaps } from '../components/ShowGaps'
import { useAgeLimits, useSession } from '../hooks/sessionContext'
import { announceWatched } from '../hooks/watched'
import { useApi } from '../hooks/useApi'
import { endingFrom, finalReportSent, moveOn, rememberScroll, takeScroll, type PlayState } from '../hooks/playbackEnding'
import { getReady, handOver, leave, PAGE_DELAY_MS } from '../lib/preparedPlayback'
import { onDemand } from '../lib/onDemand'
import { detectProfile } from '../lib/profile'
import { prepareVideo } from '../lib/copiedVideo'
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
import { locale, t, Words } from '../i18n'

// Only someone with audiobooks opens a book's page, so it isn't part of the first page everyone loads.
const BookPage = lazy(() => onDemand(() => import('./BookPage').then((m) => ({ default: m.BookPage }))))

export function ItemPage() {
  const { id = '' } = useParams()
  const [version, setVersion] = useState(0)
  const [params] = useSearchParams()
  const location = useLocation()
  // Back from watching an episode Play was pressed on here: on to the next one (or the one it stopped part way
  // through), straight away, without loading this one first.
  const ending = endingFrom(location.key)
  const movedTo = ending && moveOn(ending, id)
  // A series comes with its Up Next and the season to show (picking another season later fetches just that one).
  // Loading as the player closes, it waits a moment for the player's last report, so Up Next has what was just watched.
  const { data, error, reload } = useApi(
    async (signal) => {
      if (movedTo) return new Promise<never>(() => undefined)
      await finalReportSent()
      return getItemPage(id, params.get('season'), signal)
    },
    [id, version, movedTo],
  )

  if (movedTo) return <Navigate to={`/item/${movedTo}`} replace state={{ movedOn: true }} />

  if (error) return <PageError error={error} onRetry={reload} />
  if (!data) return <div className="detail-hero detail-loading" aria-busy="true" />

  // Seasons live on their series' page, tracks on their album's.
  if (data.item.kind === 'Season' && data.item.parentId) {
    return <Navigate to={`/item/${data.item.parentId}?season=${data.item.seasonNumber}`} replace />
  }
  if (data.item.kind === 'Track' && data.item.parentId) {
    return <Navigate to={`/item/${data.item.parentId}`} replace />
  }
  if (data.item.kind === 'Album') return <AlbumPage detail={data} onChange={() => setVersion((v) => v + 1)} />
  if (data.item.kind === 'Artist') return <ArtistPage detail={data} onChange={() => setVersion((v) => v + 1)} />
  // A book's page loads when a book is first opened; until it has, the page waits as it does for the book itself.
  if (data.item.kind === 'Audiobook')
    return (
      <Suspense fallback={<div className="detail-hero detail-loading" aria-busy="true" />}>
        <BookPage key={id} detail={data} onChange={() => setVersion((v) => v + 1)} />
      </Suspense>
    )

  return <Detail key={id} detail={data} onChange={() => setVersion((v) => v + 1)} />
}

function Detail({ detail, onChange }: { detail: ItemDetail; onChange: () => void }) {
  const { item, ancestors } = detail
  const series = ancestors[0]
  const navigate = useNavigate()
  const location = useLocation()
  // Given to the player with Play, so this page can move on once it closes (see endingFrom), and is back where it was.
  const playState: PlayState = { from: location.key }
  const play = (path: string) => {
    rememberScroll(location.key)
    navigate(path, { state: playState })
  }
  useEffect(() => {
    const y = takeScroll(location.key)
    // After the shell's scroll to the top for a new page.
    if (y !== undefined) requestAnimationFrame(() => window.scrollTo(0, y))
  }, [location.key])
  const movedOn = (location.state as { movedOn?: boolean } | null)?.movedOn === true
  const [trailer, setTrailer] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [matching, setMatching] = useState(false)
  const [editingDetails, setEditingDetails] = useState(false)
  const [findingSubtitles, setFindingSubtitles] = useState(false)
  const [subtitlesSaved, setSubtitlesSaved] = useState<string>()
  const [moreAt, setMoreAt] = useState<MenuPoint | null>(null)
  const { me, profiles } = useSession()
  // A child's page leaves out watch state and trailers (a YouTube page away from anything rated).
  const { kids } = useAgeLimits()
  // The main profile, locked or not: deleting and fixing a match don't take its PIN (only the server's settings do).
  const mainProfile = me?.isAdmin === true || me?.canUnlock === true
  const canDelete = mainProfile && (item.kind === 'Movie' || item.kind === 'Series' || item.kind === 'Episode' || item.kind === 'Audiobook')
  const canFixMatch = mainProfile && (item.kind === 'Movie' || item.kind === 'Series')
  const nextUp = detail.nextUp ?? undefined
  const similar = useApi((signal) => (item.kind === 'Movie' || item.kind === 'Series' ? getSimilar(item.id, signal) : Promise.resolve([])), [item.id])
  // After the page, so a slow Sonarr never holds it up.
  const gaps = useApi((signal) => (item.kind === 'Series' && !kids ? getShowMissing(item.id, signal).catch(() => null) : Promise.resolve(null)), [item.id, kids])

  const accent = item.accentColor ?? series?.accentColor
  const backdrop = item.artwork.backdrop ?? series?.artwork.backdrop
  const logo = item.kind === 'Episode' ? series?.artwork.logo : item.artwork.logo
  const progress = progressOf(item)
  // The order every client shares (docs/design.md › Platforms): Play, Start over, Shuffle, Trailer, Watched, Watchlist,
  // then the ellipsis for everything else, so the row doesn't wrap.
  const more: MenuAction[] = []
  // A child's profile has no collections and doesn't find subtitles.
  if ((item.kind === 'Movie' || item.kind === 'Series') && !kids) more.push({ label: t('Add to collection…'), run: () => openAddToCollection(item) })
  if (detail.canSearchSubtitles && !kids) more.push({ label: t('Search for subtitles…'), run: () => setFindingSubtitles(true) })
  if (canFixMatch) more.push({ label: t('Edit…'), run: () => setEditingDetails(true) }, { label: t('Fix match…'), run: () => setMatching(true) })
  // A parent's say over the title for profiles with an age limit, once any has one; its episodes follow it.
  const override = detail.kidsOverride
  if (mainProfile && override != null && series === undefined && profiles?.some((p) => p.maxRatingAge != null)) {
    const kids = (to: KidsOverride) => async () => {
      await setKidsOverride(item.id, to)
      onChange()
    }
    if (override === 'None') more.push({ label: t('Allow for kids'), run: kids('Allow') }, { label: t('Block for kids'), run: kids('Block') })
    else more.push({ label: override === 'Allow' ? t('Stop allowing for kids') : t('Stop blocking for kids'), run: kids('None') })
  }
  if (canDelete) {
    if (more.length) more.push('divider')
    more.push({ label: t('Delete…'), run: () => setDeleting(true), danger: true })
  }
  const best = detail.files[0]

  async function toggleWatched() {
    await setWatched(item.id, !item.userData.watched)
    announceWatched(item)
    onChange()
  }

  const playTarget = item.kind === 'Series' ? nextUp : item
  // Looked at for a moment: what Play would start is got ready, so it opens at once (docs/fast-start.md).
  const warmId = playTarget && (playTarget.kind === 'Movie' || playTarget.kind === 'Episode') ? playTarget.id : undefined
  useEffect(() => {
    if (!warmId) return
    const timer = window.setTimeout(() => getReady({ itemId: warmId, profile: detectProfile() }, prepareVideo), PAGE_DELAY_MS)
    return () => {
      window.clearTimeout(timer)
      leave(warmId)
    }
  }, [warmId])
  const playLabel = (() => {
    if (item.kind === 'Series') {
      if (!nextUp) return t(Words.play)
      const episode = episodeHeading(nextUp)
      if (item.userData.watched) return t('Watch again from {episode}', { episode })
      return nextUp.userData.positionSeconds > 0 ? t(Words.resumeEpisode, { episode }) : t('Play {episode}', { episode })
    }
    return progress ? t(Words.resumeFrom, { time: formatClock(item.userData.positionSeconds) }) : t(Words.play)
  })()

  return (
    <article className="detail" style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}>
      <section className="detail-hero">
        <div className="detail-art" aria-hidden="true">
          {backdrop && <img {...backdropSources(backdrop)} alt="" />}
        </div>
        <div className="detail-hero-content">
          {/* Moved on from the episode just watched: said, since the page changed under the reader. */}
          {movedOn && (
            <p className="visually-hidden" role="status">
              {t('Now showing {episode}, {title}', { episode: episodeHeading(item), title: item.title })}
            </p>
          )}
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
              item.kind === 'Episode' && item.releaseDate ? new Date(item.releaseDate).toLocaleDateString(locale(), { dateStyle: 'medium' }) : null,
            ]}
          />
          {detail.overview && <Overview text={detail.overview} />}
          <div className="detail-actions">
            {playTarget && (
              <button
                className="button"
                onClick={() => {
                  handOver(playTarget.id)
                  play(`/play/${playTarget.id}`)
                }}
              >
                <icon.play /> {playLabel}
              </button>
            )}
            {progress !== null && (
              <button className="button quiet" onClick={() => play(`/play/${item.id}?t=0`)}>
                <icon.startOver /> {t(Words.startOver)}
              </button>
            )}
            {item.kind === 'Series' && item.childCount > 0 && (
              <button className="button quiet" onClick={() => navigate(`/play/${item.id}?shuffle=1`)}>
                <icon.shuffle /> {t(Words.shuffle)}
              </button>
            )}
            {detail.trailerYouTubeId && !kids && (
              <button className="button quiet" onClick={() => setTrailer(true)}>
                <icon.trailer /> {t(Words.trailer)}
              </button>
            )}
            {!kids && (
              <AsyncButton className="button quiet" aria-pressed={item.userData.watched} onClick={toggleWatched}>
                {item.userData.watched ? <icon.isWatched /> : <icon.watched />} {item.userData.watched ? t('Watched') : t(Words.markAsWatched)}
              </AsyncButton>
            )}
            {detail.onWatchlist != null && !kids && <WatchlistButton titleKey={{ libraryItemId: item.id }} title={item.title} on={detail.onWatchlist} />}
            {more.length > 0 && (
              <button
                className="button quiet icon"
                aria-label={t('More actions')}
                aria-haspopup="menu"
                aria-expanded={moreAt !== null}
                onClick={(e) => {
                  const r = e.currentTarget.getBoundingClientRect()
                  setMoreAt(moreAt ? null : { x: r.left, y: r.bottom + 6 })
                }}
              >
                <icon.more />
              </button>
            )}
            {moreAt && <Menu label={t('More for {title}', { title: item.title })} at={moreAt} actions={more} onClose={() => setMoreAt(null)} />}
          </div>
          {subtitlesSaved && (
            <p className="muted" role="status">
              {t('{language} subtitles saved. Choose them from the subtitles menu while it plays.', { language: subtitlesSaved })}
            </p>
          )}
          {progress !== null && (
            <span className="detail-progress" aria-label={t('{percent}% watched', { percent: Math.round(progress * 100) })}>
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
        {item.kind === 'Series' && <Seasons series={detail} counts={gaps.data?.counts} onChange={onChange} />}
        {item.kind === 'Series' && gaps.data && <ShowGaps
            showId={item.id}
            tmdbId={detail.tmdbId ? Number(detail.tmdbId) : null}
            gaps={gaps.data}
            posters={new Map(detail.children.filter((s) => s.seasonNumber != null).map((s) => [s.seasonNumber!, s.artwork.poster]))}
            showPoster={item.artwork.poster}
          />}
        {detail.credits.length > 0 && <Cast credits={detail.credits} studio={detail.studio} />}
        {similar.data && similar.data.length > 0 && (
          <Row title={t(Words.moreLikeThis)}>
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
      {editingDetails && <DetailsEditor detail={detail} onSaved={onChange} onClose={() => setEditingDetails(false)} />}
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

function Seasons({ series, counts, onChange }: { series: ItemDetail; counts?: ShowMissing['counts']; onChange: () => void }) {
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
      <div className="season-tabs" role="tablist" aria-label={t('Seasons')}>
        {seasons.map((s) => (
          <button
            key={s.id}
            role="tab"
            aria-selected={s.id === selected.id}
            className={s.id === selected.id ? 'season-tab active' : 'season-tab'}
            onClick={() => setParams({ season: String(s.seasonNumber) }, { replace: true })}
          >
            {s.homeVideo ? s.title : s.seasonNumber === 0 ? t('Specials') : t('Season {season}', { season: s.seasonNumber })}
            {(() => {
              // Only a season that's short of what's aired says so.
              const count = counts?.find((c) => c.seasonNumber === s.seasonNumber)
              return count && count.have < count.aired ? (
                <span className="season-count" aria-label={t('{have} of {count, plural, one {# episode} other {# episodes}}', { have: count.have, count: count.aired })}>
                  {t('{have} of {aired}', { have: count.have, aired: count.aired })}
                </span>
              ) : null
            })()}
            {s.userData.watched && <icon.watched aria-label={t('watched')} />}
          </button>
        ))}
      </div>
      <div className="season-toolbar">
        <span className="muted">{season.data ? t('{count, plural, one {# episode} other {# episodes}}', { count: season.data.children.length }) : ''}</span>
        <AsyncButton
          className="text-button"
          onClick={async () => {
            await setWatched(selected.id, !selected.userData.watched)
            announceWatched(selected)
            onChange()
          }}
        >
          {selected.userData.watched ? t('Mark season as unwatched') : t(Words.markSeasonAsWatched)}
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
  const location = useLocation()
  const { kids } = useAgeLimits()
  const progress = progressOf(episode)

  return (
    <li className={episode.userData.watched ? 'episode watched' : 'episode'}>
      <div className="episode-still-wrap">
      <Link
        to={`/play/${episode.id}`}
        state={{ from: location.key } satisfies PlayState}
        onClick={() => rememberScroll(location.key)}
        className="episode-still"
        aria-label={t('Play {title}', { title: episode.title })}
      >
        <Artwork src={episode.artwork.thumb} fallback={episode.artwork.backdrop} title={episode.title} width={500} shape="wide" />
        <span className="episode-play" aria-hidden="true">
          <icon.play />
        </span>
        {progress !== null && (
          <span className="progress">
            <span style={{ width: `${progress * 100}%` }} />
          </span>
        )}
      </Link>
      {!kids && <WatchedToggle key={`${episode.id}-${episode.userData.watched}`} item={episode} onChanged={onChange} />}
      </div>
      <div className="episode-text">
        <h3>
          <span className="episode-number">{episode.airDate ? '' : episode.episodeNumberEnd ? `${episode.episodeNumber}–${episode.episodeNumberEnd}` : episode.episodeNumber}</span>
          <Link to={`/item/${episode.id}`}>{episode.title}</Link>
        </h3>
        <p className="episode-facts muted">
          {[formatDuration(episode.durationSeconds ?? (episode.runtimeMinutes ?? 0) * 60), episode.releaseDate && new Date(episode.releaseDate).toLocaleDateString(locale(), { dateStyle: 'medium' })]
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
  const roles = { Director: t('Directed by'), Writer: t('Written by'), Creator: t('Created by'), Producer: t('Produced by') }

  return (
    <section className="cast">
      {cast.length > 0 && (
        <Row title={t('Cast')} style="people">
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
              <dt>{t('From')}</dt>
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
      <summary>{files.length > 1 ? t('{count} versions on disk', { count: files.length }) : t('File and stream details')}</summary>
      {files.map((file) => (
        <div key={file.id} className="file">
          {file.path && <code>{file.path}</code>}
          {file.probeError && <p className="error">{t('This file couldn’t be read: {reason}', { reason: file.probeError })}</p>}
          <dl>
            <dt>{t('Video')}</dt>
            <dd>{[file.videoCodec?.toUpperCase(), file.width && `${file.width}×${file.height}`, pictureLabel(file)].filter(Boolean).join(', ') || t('None')}</dd>
            <dt>{t('Audio')}</dt>
            <dd>{[file.audioCodec?.toUpperCase(), formatChannels(file.audioChannels)].filter(Boolean).join(', ') || t('None')}</dd>
            <dt>{t('File')}</dt>
            <dd>{[file.container?.toUpperCase(), formatSize(file.size), formatBitrate(file.bitrate)].filter(Boolean).join(', ')}</dd>
            <dt>{t(Words.subtitles)}</dt>
            <dd>
              {file.streams
                .filter((s) => s.type === 'Subtitle')
                .map((s) => s.language ?? t('unknown'))
                .join(', ') || t('None embedded')}
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
          {queued ? t('Queued: intros and credits for this season will be re-detected shortly') : t('Re-detect intros and credits for this season')}
        </AsyncButton>
      )}
    </details>
  )
}

