import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import {
  getMusicRequested,
  getMusicRequestStatusNow,
  getRequestStatus,
  getTitlesRequested,
  imageUrl,
  retryAlbum,
  unrequestAlbum,
  unrequestTitle,
  type Library,
  type MusicRequested,
  type TitleRequested,
} from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PageError, PageLoading } from '../components/PageStatus'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { requestedState } from '../lib/requestedState'
import { undoFailure } from '../lib/musicUndo'
import { errorText } from './settings/useSettings'
import { t, Words } from '../i18n'

/** How many cards show at first; Show more adds as many again. */
const PAGE_SIZE = 200
/** How often the view asks again while something is downloading or importing. */
const REFRESH_MS = 30_000

/** One card of the view, whichever library it's in. */
interface Row {
  key: string
  title: string
  subtitle: ReactNode
  image: string | null
  /** Where the artwork goes, if anywhere. */
  link?: string
  /** What the artwork's link says. */
  linkLabel: string
  state: MusicRequested['state']
  progress: number | null
  releaseDate: string | null
  undo: () => Promise<void>
  /** Starts over on one that couldn't be imported (albums only). */
  retry?: () => Promise<void>
}

/**
 * A library's Requested view. Music: every album Lidarr is waiting for with this library's artists (asked for,
 * monitored without files, or not out yet). Films and shows: every one asked for through Seerr that isn't in yet and
 * is going to this library. Each with how far along it is and Unrequest. When Lidarr or Seerr doesn't answer, it says
 * so rather than that nothing is coming.
 */
export function LibraryRequested({ library, viewSwitch }: { library: Library; viewSwitch: ReactNode }) {
  const music = library.type === 'Music'
  const service = music ? 'Lidarr' : 'Seerr'
  const { data, error, reload } = useApi(
    async (signal) => {
      const rows = music ? (await getMusicRequested(library.id, signal)).map(albumRow) : (await getTitlesRequested(library.id, signal)).map(titleRow)
      // Asked after the list, which marks a Lidarr or Seerr that didn't answer it.
      const status = await (music ? getMusicRequestStatusNow : getRequestStatus)(signal).catch(() => undefined)
      return { rows, answering: status?.reachable !== false }
    },
    [library.id, music],
  )
  const [gone, setGone] = useState<Set<string>>(new Set())
  const [shown, setShown] = useState(PAGE_SIZE)
  const grid = useRef<HTMLDivElement>(null)
  const rows = data?.rows.filter((r) => !gone.has(r.key))
  const answering = data?.answering !== false
  // Downloading 40% moves on: asked again now and then while anything is on its way in, so it doesn't look stuck.
  usePolling(reload, data?.rows.some((r) => r.state === 'downloading' || r.state === 'importing' || r.state === 'couldNotImport') ? REFRESH_MS : null)

  // The card that opened the dialog is gone: the keyboard moves on to the next one's Unrequest (or the grid).
  function remove(key: string, index: number) {
    setGone((g) => new Set(g).add(key))
    requestAnimationFrame(() => {
      const buttons = grid.current?.querySelectorAll<HTMLButtonElement>('.requested-card .requested-unrequest')
      const next = buttons?.[Math.min(index, buttons.length - 1)]
      if (next) next.focus()
      else grid.current?.focus()
    })
  }

  return (
    <div className="library-page">
      <header className="library-header">
        <div className="library-heading">
          <h1 className="library-title">{library.name}</h1>
          {viewSwitch}
        </div>
      </header>
      {error && !data ? (
        <PageError error={error} onRetry={reload} />
      ) : !rows ? (
        <PageLoading label={music ? t('Loading requested albums') : library.type === 'Movies' ? t('Loading requested movies') : t('Loading requested shows')} />
      ) : (
        <>
          {!answering && (
            <p className="page-message muted" role="status">
              {t('{service} isn’t answering.', { service })}
            </p>
          )}
          {rows.length === 0 ? (
            answering && <p className="page-message muted">{t('Nothing on its way.')}</p>
          ) : (
            <div className={music ? 'poster-grid music-albums' : 'poster-grid'} ref={grid} tabIndex={-1}>
              {rows.slice(0, shown).map((r, i) => (
                <RequestedCard key={r.key} row={r} square={music} offline={!answering} service={service} onGone={() => remove(r.key, i)} onRetried={reload} />
              ))}
            </div>
          )}
          {rows.length > shown && (
            <div className="load-more">
              <button className="button quiet" onClick={() => setShown((n) => n + PAGE_SIZE)}>
                {t('Show more')}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

/** The album isn't anywhere yet: its artwork opens its artist, when they're in the library. */
function albumRow(item: MusicRequested): Row {
  const link = item.artistLibraryItemId ? `/item/${item.artistLibraryItemId}` : undefined
  return {
    key: item.musicBrainzId,
    title: item.title,
    subtitle: link ? <Link to={link}>{item.artist}</Link> : item.artist,
    image: item.image,
    link,
    linkLabel: t('{title}, by {artist}', { title: item.title, artist: item.artist }),
    state: item.state,
    progress: item.progress,
    releaseDate: item.releaseDate,
    undo: () => unrequestAlbum({ musicBrainzId: item.musicBrainzId }),
    retry: () => retryAlbum({ musicBrainzId: item.musicBrainzId }),
  }
}

/** A film or show opens its page: the library's, for a show that's partly in, else the one from outside. */
function titleRow(item: TitleRequested): Row {
  const seasons = item.seasons?.length
    ? item.seasons.length === 1
      ? t('Season {number}', { number: item.seasons[0] })
      : t('Seasons {list} and {last}', { list: item.seasons.slice(0, -1).join(', '), last: item.seasons[item.seasons.length - 1] })
    : null
  return {
    key: `${item.mediaType}:${item.tmdbId}`,
    title: item.title,
    subtitle: seasons ?? item.year ?? '',
    image: item.poster,
    link: item.libraryItemId ? `/item/${item.libraryItemId}` : `/discover/${item.mediaType}/${item.tmdbId}`,
    linkLabel: item.title,
    state: item.state,
    progress: item.progress,
    releaseDate: item.releaseDate,
    // Just the seasons asked for: a show that's partly in keeps the rest.
    undo: () => unrequestTitle(item.mediaType, item.tmdbId, item.seasons ?? undefined),
  }
}

function RequestedCard({ row, square, offline, service, onGone, onRetried }: { row: Row; square: boolean; offline: boolean; service: string; onGone: () => void; onRetried: () => void }) {
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string>()
  // A cover that won't load shows the title, not the browser's broken-image mark.
  const [brokenImage, setBrokenImage] = useState<string | null>(null)

  async function unrequest() {
    setError(undefined)
    try {
      await row.undo()
      setAsking(false)
      onGone()
    } catch (e) {
      setError(undoFailure(e))
    }
  }

  // Lidarr's queue keeps saying it for a while after a search starts: searching, until the next few looks have had their say.
  const [retried, setRetried] = useState(false)
  const [retryError, setRetryError] = useState<string>()
  useEffect(() => {
    if (!retried) return
    const settle = setTimeout(() => setRetried(false), REFRESH_MS)
    return () => clearTimeout(settle)
  }, [retried])
  const state = retried && row.state === 'couldNotImport' ? 'searching' : row.state
  const stuck = state === 'couldNotImport'

  async function retry() {
    setRetryError(undefined)
    try {
      await row.retry?.()
      setRetried(true)
      onRetried()
    } catch (e) {
      setRetryError(errorText(e))
    }
  }

  const art = (
    <div className="card-frame">
      <div className={square ? 'artwork artwork-square' : 'artwork artwork-poster'}>
        {row.image && row.image !== brokenImage ? <img src={imageUrl(row.image, 342)} alt="" loading="lazy" onError={() => setBrokenImage(row.image)} /> : <span className="artwork-fallback">{row.title}</span>}
      </div>
    </div>
  )

  return (
    <div className={square ? 'card external-card music-external-card music-external-album requested-card' : 'card external-card requested-card'}>
      {row.link ? (
        <Link to={row.link} aria-label={row.linkLabel} tabIndex={-1}>
          {art}
        </Link>
      ) : (
        art
      )}
      <span className="card-title">{square || !row.link ? row.title : <Link to={row.link}>{row.title}</Link>}</span>
      <span className="card-subtitle">{row.subtitle}</span>
      <span className={`requested-state requested-${state}`}>
        {requestedState({ ...row, state })}
        {stuck && (
          <>
            {' '}
            <a href="https://flexarr.io/guide/requests#couldn-t-import" target="_blank" rel="noreferrer" aria-label={t('Why couldn’t {title} be imported?', { title: row.title })}>
              {t('Why?')}
            </a>
          </>
        )}
      </span>
      {row.state === 'downloading' && row.progress != null && (
        <progress className="requested-progress" value={row.progress} max={1} aria-label={t('{title} downloading', { title: row.title })} />
      )}
      <div className="external-card-actions">
        {stuck && row.retry && (
          <AsyncButton className="button small" aria-label={t('Try again: {title}', { title: row.title })} onClick={retry} disabled={offline} title={offline ? t('{service} isn’t answering.', { service }) : undefined}>
            {t('Try again')}
          </AsyncButton>
        )}
        <AsyncButton className="button small quiet requested-unrequest" aria-label={t('Unrequest {title}', { title: row.title })} onClick={() => setAsking(true)} disabled={offline} title={offline ? t('{service} isn’t answering.', { service }) : undefined}>
          {t(Words.unrequest)}
        </AsyncButton>
      </div>
      {retryError && (
        <p className="error requested-error" role="alert">
          {retryError}
        </p>
      )}
      {asking && (
        <ConfirmDialog
          title={t('Unrequest {name}?', { name: row.title })}
          confirmLabel={t(Words.unrequest)}
          cancelLabel={t('Keep it')}
          danger
          error={error}
          onConfirm={unrequest}
          onClose={() => {
            setAsking(false)
            setError(undefined)
          }}
        />
      )}
    </div>
  )
}
