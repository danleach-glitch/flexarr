import { useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { getMusicRequested, getMusicRequestStatusNow, imageUrl, unrequestAlbum, type Library, type MusicRequested } from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PageError, PageLoading } from '../components/PageStatus'
import { useApi } from '../hooks/useApi'
import { requestedState } from '../lib/requestedState'
import { undoFailure } from '../lib/musicUndo'

/** How many albums show at first; Show more adds as many again. */
const PAGE_SIZE = 200

/**
 * A music library's Requested view: every album Lidarr is waiting for with this library's artists (asked for,
 * monitored without files, or not out yet), with how far along each is and Unrequest. When Lidarr doesn't answer, it
 * says so rather than that nothing is coming.
 */
export function LibraryRequested({ library, viewSwitch }: { library: Library; viewSwitch: ReactNode }) {
  const { data, error, reload } = useApi(
    async (signal) => {
      const albums = await getMusicRequested(library.id, signal)
      // Asked after the list, which marks a Lidarr that didn't answer it.
      const status = await getMusicRequestStatusNow(signal).catch(() => undefined)
      return { albums, answering: status?.reachable !== false }
    },
    [library.id],
  )
  const [gone, setGone] = useState<Set<string>>(new Set())
  const [shown, setShown] = useState(PAGE_SIZE)
  const grid = useRef<HTMLDivElement>(null)
  const albums = data?.albums.filter((r) => !gone.has(r.musicBrainzId))
  const answering = data?.answering !== false

  // The card that opened the dialog is gone: the keyboard moves on to the next one's Unrequest (or the grid).
  function remove(id: string, index: number) {
    setGone((g) => new Set(g).add(id))
    requestAnimationFrame(() => {
      const buttons = grid.current?.querySelectorAll<HTMLButtonElement>('.requested-card .external-card-actions button')
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
      {error ? (
        <PageError error={error} onRetry={reload} />
      ) : !albums ? (
        <PageLoading label="Loading requested albums" />
      ) : (
        <>
          {!answering && (
            <p className="page-message muted" role="status">
              Lidarr isn’t answering.
            </p>
          )}
          {albums.length === 0 ? (
            answering && <p className="page-message muted">Nothing on its way.</p>
          ) : (
            <div className="poster-grid music-albums" ref={grid} tabIndex={-1}>
              {albums.slice(0, shown).map((r, i) => (
                <RequestedCard key={r.musicBrainzId} item={r} offline={!answering} onGone={() => remove(r.musicBrainzId, i)} />
              ))}
            </div>
          )}
          {albums.length > shown && (
            <div className="load-more">
              <button className="button quiet" onClick={() => setShown((n) => n + PAGE_SIZE)}>
                Show more
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function RequestedCard({ item, offline, onGone }: { item: MusicRequested; offline: boolean; onGone: () => void }) {
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string>()
  const artistLink = item.artistLibraryItemId ? `/item/${item.artistLibraryItemId}` : undefined

  async function unrequest() {
    setError(undefined)
    try {
      await unrequestAlbum({ musicBrainzId: item.musicBrainzId })
      setAsking(false)
      onGone()
    } catch (e) {
      setError(undoFailure(e))
    }
  }

  const art = (
    <div className="card-frame">
      <div className="artwork artwork-square">
        {item.image ? <img src={imageUrl(item.image, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{item.title}</span>}
      </div>
    </div>
  )

  return (
    <div className="card external-card music-external-card music-external-album requested-card">
      {/* The album isn't anywhere yet: its artwork opens its artist, when they're in the library. */}
      {artistLink ? (
        <Link to={artistLink} aria-label={`${item.title}, by ${item.artist}`} tabIndex={-1}>
          {art}
        </Link>
      ) : (
        art
      )}
      <span className="card-title">{item.title}</span>
      <span className="card-subtitle">{artistLink ? <Link to={artistLink}>{item.artist}</Link> : item.artist}</span>
      <span className={`requested-state requested-${item.state}`}>{requestedState(item)}</span>
      {item.state === 'downloading' && item.progress != null && (
        <progress className="requested-progress" value={item.progress} max={1} aria-label={`${item.title} downloading`} />
      )}
      <div className="external-card-actions">
        <AsyncButton className="button small quiet" onClick={() => setAsking(true)} disabled={offline} title={offline ? 'Lidarr isn’t answering.' : undefined}>
          Unrequest
        </AsyncButton>
      </div>
      {asking && (
        <ConfirmDialog
          title={`Unrequest ${item.title}?`}
          confirmLabel="Unrequest"
          cancelLabel="Keep it"
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
