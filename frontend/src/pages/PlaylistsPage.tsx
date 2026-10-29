import { useState, type CSSProperties, type DragEvent, type FormEvent, type MouseEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import {
  ApiError,
  createPlaylist,
  deletePlaylist,
  getPlaylist,
  getPlaylists,
  imageUrl,
  movePlaylistEntry,
  removeFromPlaylist,
  renamePlaylist,
  setPlaylistCover,
  type ItemSummary,
  type PlaylistTrack,
} from '../api/client'
import { AsyncButton, IconButton } from '../components/AsyncButton'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ChevronDown, ChevronUp, Close, Grip, More, Pencil, Play, Trash } from '../components/icons'
import { Menu, type MenuPoint } from '../components/ItemMenu'
import { PlaylistCard, PlaylistCover } from '../components/PlaylistCover'
import { useAudioPlayer, useMusicPage } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { formatClock, formatDuration } from '../lib/format'
import { MoreButton, queueActions, shuffled } from './MusicPages'

/** Every playlist of the profile's, most recently changed first, and a way to start another. */
export function PlaylistsPage() {
  const playlists = useApi(getPlaylists, [])
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const navigate = useNavigate()
  useMusicPage()

  async function create(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setSaving(true)
    try {
      const created = await createPlaylist(name.trim())
      navigate(`/playlist/${created.id}`)
    } catch (err) {
      setError(err instanceof ApiError && err.messages[0] ? err.messages[0] : String(err))
      setSaving(false)
    }
  }

  return (
    <div className="library-page playlists-page">
      <header className="library-header">
        <div>
          <h1>Playlists</h1>
          <p className="muted">Your own mixes. Add tracks, albums or whole artists from their ⋯ menu.</p>
        </div>
        {creating ? (
          <form className="field-row playlist-new" onSubmit={create} aria-label="New playlist">
            <label className="grow">
              <span className="visually-hidden">Playlist name</span>
              <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} placeholder="Name your playlist" autoFocus />
            </label>
            <AsyncButton className="button" type="submit" disabled={!name.trim()} busy={saving}>
              Create
            </AsyncButton>
            <button className="button quiet" type="button" onClick={() => setCreating(false)}>
              Cancel
            </button>
          </form>
        ) : (
          <button className="button" onClick={() => setCreating(true)}>
            New playlist
          </button>
        )}
      </header>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {playlists.error && <p className="error">{playlists.error.message}</p>}
      {playlists.data?.length === 0 && <p className="muted collection-empty">No playlists yet. Make one, or add a track to a new one from its ⋯ menu.</p>}
      <div className="poster-grid square">
        {playlists.data?.map((p) => (
          <PlaylistCard key={p.id} playlist={p} />
        ))}
      </div>
    </div>
  )
}

/**
 * One playlist: its cover, name and length, Play and Shuffle, and the tracks in order. Tracks can be dragged into a new
 * order (or moved with the arrow buttons from the keyboard) and removed; the playlist can be renamed or deleted.
 */
export function PlaylistPage() {
  const { id = '' } = useParams()
  const [version, setVersion] = useState(0)
  const { data, error } = useApi((signal) => getPlaylist(id, signal), [id, version])
  const player = useAudioPlayer()
  const navigate = useNavigate()
  useMusicPage()
  // The order as it's being changed: shown at once, the server catching up (a failure reloads what it has).
  const [order, setOrder] = useState<PlaylistTrack[]>()
  const [renaming, setRenaming] = useState(false)
  const [name, setName] = useState('')
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [message, setMessage] = useState<string>()
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  // One track's menu at a time: which, and where it opened.
  const [trackMenu, setTrackMenu] = useState<{ index: number; at: MenuPoint } | null>(null)

  const reload = () => {
    setOrder(undefined)
    setVersion((v) => v + 1)
  }

  if (error) return <p className="page-message error">{error.message.includes('404') ? 'That playlist isn’t here any more.' : error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  const summary = data.summary
  const entries = order ?? data.tracks
  const tracks = entries.map((e) => e.track)
  const total = tracks.reduce((sum, t) => sum + (t.durationSeconds ?? 0), 0)
  const glow = summary.cover ?? summary.covers[0]
  const accent = tracks.find((t) => t.accentColor)?.accentColor

  async function act(work: () => Promise<unknown>) {
    try {
      setMessage(undefined)
      await work()
      reload()
    } catch (e) {
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      reload()
    }
  }

  /** Moves the entry at `from` to `to` in the list shown, and tells the server. */
  function move(from: number, to: number) {
    if (from === to || to < 0 || to >= entries.length) return
    const moved = [...entries]
    const [entry] = moved.splice(from, 1)
    moved.splice(to, 0, entry)
    setOrder(moved)
    void movePlaylistEntry(summary.id, entry.entryId, to).catch((e: unknown) => {
      setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      reload()
    })
  }

  function remove(entry: PlaylistTrack) {
    setOrder(entries.filter((e) => e.entryId !== entry.entryId))
    void act(() => removeFromPlaylist(summary.id, entry.entryId))
  }

  function rename(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setSaving(true)
    void act(async () => {
      await renamePlaylist(summary.id, name.trim())
      setRenaming(false)
    }).finally(() => setSaving(false))
  }

  const dragStart = (at: number, track: ItemSummary) => (e: DragEvent) => {
    setDragging(at)
    e.dataTransfer.effectAllowed = 'move'
    e.dataTransfer.setData('text/plain', track.title)
  }
  const dragOver = (at: number) => (e: DragEvent) => {
    if (dragging === null) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (over !== at) setOver(at)
  }
  const drop = (at: number) => (e: DragEvent) => {
    e.preventDefault()
    if (dragging !== null && dragging !== at) move(dragging, at)
    setDragging(null)
    setOver(null)
  }
  const dragEnd = () => {
    setDragging(null)
    setOver(null)
  }

  const openMenu = (index: number, at: MenuPoint) => (e: MouseEvent) => {
    e.preventDefault()
    setTrackMenu(trackMenu?.index === index ? null : { index, at })
  }

  const menuFor = (index: number) => {
    const entry = entries[index]
    const track = entry.track
    return [
      { label: 'Play', run: () => player.play(tracks, index) },
      ...queueActions(player, () => [track], track),
      'divider' as const,
      ...(track.parentId ? [{ label: 'Use its cover for the playlist', run: () => act(() => setPlaylistCover(summary.id, track.parentId)) }] : []),
      { label: 'Remove from playlist', run: () => remove(entry), danger: true },
    ]
  }

  return (
    <article className="music-page playlist-page" style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {glow && <img src={imageUrl(glow, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <PlaylistCover playlist={summary} width={500} />
        </div>
        <div className="music-header-text">
          <p className="detail-kicker">
            <Link to="/playlists">Playlists</Link>
          </p>
          {renaming ? (
            <form className="field-row playlist-rename" onSubmit={rename} aria-label="Rename playlist">
              <label className="grow">
                <span className="visually-hidden">Playlist name</span>
                <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} autoFocus />
              </label>
              <AsyncButton className="button" type="submit" disabled={!name.trim()} busy={saving}>
                Save
              </AsyncButton>
              <button className="button quiet" type="button" onClick={() => setRenaming(false)}>
                Cancel
              </button>
            </form>
          ) : (
            <h1>{summary.name}</h1>
          )}
          <p className="facts">
            <span>{tracks.length === 1 ? '1 track' : `${tracks.length} tracks`}</span>
            {total > 0 && <span>{formatDuration(total)}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => player.play(tracks, 0, { openNowPlaying: true })} disabled={tracks.length === 0}>
              <Play /> Play
            </button>
            <button className="button quiet" onClick={() => player.play(shuffled(tracks), 0, { openNowPlaying: true })} disabled={tracks.length < 2}>
              Shuffle
            </button>
            <IconButton
              label="Rename"
              icon={<Pencil />}
              className="button quiet icon"
              onClick={() => {
                setName(summary.name)
                setRenaming(true)
              }}
            />
            <IconButton label="Delete" icon={<Trash />} className="button quiet icon danger-icon" onClick={() => setDeleting(true)} />
            <MoreButton
              label={`More for ${summary.name}`}
              actions={[...queueActions(player, () => tracks), ...(summary.cover ? [{ label: 'Back to the mosaic cover', run: () => act(() => setPlaylistCover(summary.id, null)) }] : [])]}
              disabled={tracks.length === 0}
            />
          </div>
          {message && (
            <p className="error" role="alert">
              {message}
            </p>
          )}
        </div>
      </header>

      {entries.length === 0 ? (
        <p className="muted collection-empty">Nothing in it yet. Add tracks, albums or artists from their ⋯ menu: Add to playlist.</p>
      ) : (
        <ol className="tracklist playlist-tracks" aria-label={`Tracks in ${summary.name}`}>
          {entries.map(({ entryId, track }, i) => {
            const isCurrent = player.current?.id === track.id
            return (
              <li
                key={entryId}
                data-entry-id={entryId}
                className={['playlist-entry', dragging === i && 'dragging', over === i && 'drag-over'].filter(Boolean).join(' ')}
                draggable
                onDragStart={dragStart(i, track)}
                onDragOver={dragOver(i)}
                onDragLeave={() => over === i && setOver(null)}
                onDrop={drop(i)}
                onDragEnd={dragEnd}
                onContextMenu={(e) => openMenu(i, { x: e.clientX, y: e.clientY })(e)}
              >
                <span className="queue-grip" aria-hidden="true">
                  <Grip />
                </span>
                <button className={isCurrent ? 'track current' : 'track'} onClick={() => player.play(tracks, i)} aria-current={isCurrent || undefined}>
                  <span className="track-number">{isCurrent && player.playing ? <Play /> : i + 1}</span>
                  <span className="playlist-track-art" aria-hidden="true">
                    {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 92)} alt="" loading="lazy" />}
                  </span>
                  <span className="playlist-track-text">
                    <span className="track-title">{track.title}</span>
                    <span className="muted">{track.seriesTitle}</span>
                  </span>
                  <span className="track-time">{track.durationSeconds ? formatClock(track.durationSeconds) : ''}</span>
                </button>
                <span className="queue-actions">
                  <button className="icon-button" aria-label={`Move ${track.title} up`} disabled={i === 0} onClick={() => move(i, i - 1)}>
                    <ChevronUp />
                  </button>
                  <button className="icon-button" aria-label={`Move ${track.title} down`} disabled={i === entries.length - 1} onClick={() => move(i, i + 1)}>
                    <ChevronDown />
                  </button>
                  <button className="icon-button" aria-label={`Remove ${track.title} from the playlist`} onClick={() => remove(entries[i])}>
                    <Close />
                  </button>
                  <button
                    className="icon-button"
                    aria-label={`More for ${track.title}`}
                    aria-haspopup="menu"
                    aria-expanded={trackMenu?.index === i}
                    onClick={(e) => {
                      const r = e.currentTarget.getBoundingClientRect()
                      openMenu(i, { x: r.right - 220, y: r.bottom + 6 })(e)
                    }}
                  >
                    <More />
                  </button>
                </span>
              </li>
            )
          })}
        </ol>
      )}

      {trackMenu && entries[trackMenu.index] && (
        <Menu label={`More for ${entries[trackMenu.index].track.title}`} at={trackMenu.at} actions={menuFor(trackMenu.index)} onClose={() => setTrackMenu(null)} />
      )}
      {deleting && (
        <ConfirmDialog
          title={`Delete ${summary.name}?`}
          confirmLabel="Delete playlist"
          danger
          onClose={() => setDeleting(false)}
          onConfirm={async () => {
            try {
              await deletePlaylist(summary.id)
              navigate('/playlists', { replace: true })
            } catch (e) {
              setDeleting(false)
              setMessage(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
            }
          }}
        >
          <p>The tracks stay in your library.</p>
        </ConfirmDialog>
      )}
    </article>
  )
}
