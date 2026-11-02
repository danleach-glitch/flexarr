import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError, getMusicRequested, imageUrl, unrequestAlbum, type Library, type MusicRequested } from '../api/client'
import { AsyncButton } from '../components/AsyncButton'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { PageError, PageLoading } from '../components/PageStatus'
import { useApi } from '../hooks/useApi'
import { requestedState } from '../lib/requestedState'

/**
 * A music library's Requested view: every album Lidarr is waiting for, household-wide (asked for, monitored without
 * files, or not out yet), with how far along each is and Unrequest.
 */
export function LibraryRequested({ library, viewSwitch }: { library: Library; viewSwitch: ReactNode }) {
  const { data, error, reload } = useApi(getMusicRequested, [])
  const [gone, setGone] = useState<Set<string>>(new Set())
  const shown = data?.filter((r) => !gone.has(r.musicBrainzId))

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
      ) : !shown ? (
        <PageLoading label="Loading requested albums" />
      ) : shown.length === 0 ? (
        <p className="page-message muted">Nothing on its way.</p>
      ) : (
        <div className="poster-grid music-albums">
          {shown.map((r) => (
            <RequestedCard key={r.musicBrainzId} item={r} onGone={() => setGone((g) => new Set(g).add(r.musicBrainzId))} />
          ))}
        </div>
      )}
    </div>
  )
}

function RequestedCard({ item, onGone }: { item: MusicRequested; onGone: () => void }) {
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string>()

  async function unrequest() {
    setError(undefined)
    try {
      await unrequestAlbum({ musicBrainzId: item.musicBrainzId })
      setAsking(false)
      onGone()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'That didn’t work. Try again.')
    }
  }

  return (
    <div className="card external-card music-external-card music-external-album requested-card">
      <div className="card-frame">
        <div className="artwork artwork-square">
          {item.image ? <img src={imageUrl(item.image, 342)} alt="" loading="lazy" /> : <span className="artwork-fallback">{item.title}</span>}
        </div>
      </div>
      <span className="card-title">{item.title}</span>
      <span className="card-subtitle">
        {item.artistLibraryItemId ? <Link to={`/item/${item.artistLibraryItemId}`}>{item.artist}</Link> : item.artist}
      </span>
      <span className={`requested-state requested-${item.state}`}>{requestedState(item)}</span>
      {item.state === 'downloading' && item.progress != null && (
        <progress className="requested-progress" value={item.progress} max={1} aria-label={`${item.title} downloading`} />
      )}
      <div className="external-card-actions">
        <AsyncButton className="button small quiet" onClick={() => setAsking(true)}>
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
