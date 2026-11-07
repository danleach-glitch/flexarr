import { memo, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent, type FormEvent, type MouseEvent, type ReactNode } from 'react'
import { PageError, PageLoading } from '../components/PageStatus'
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
import { ChevronDown, ChevronUp, Close, Grip, More, Pencil, Play } from '../components/icons'
import { icon } from '../components/icons.generated'
import { Menu, type MenuPoint } from '../components/ItemMenu'
import { PlaylistCard, PlaylistCover } from '../components/PlaylistCover'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { formatClock, formatDuration } from '../lib/format'
import { MoreButton, queueActions } from './MusicPages'
import { shuffled } from '../lib/shuffle'
import { t, Words } from '../i18n'
import '../styles/settings.lazy.css'

/**
 * Every playlist of the profile's, most recently changed first, and a way to start another. Inside a music library it
 * sits under the library's name and views (`heading`); on its own (the sidebar's Playlists) it has a title of its own.
 */
export function PlaylistsPage({ heading }: { heading?: ReactNode }) {
  const playlists = useApi(getPlaylists, [])
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const navigate = useNavigate()

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

  const empty = playlists.data?.length === 0
  const newPlaylist = creating ? (
    <form className="field-row playlist-new" onSubmit={create} aria-label={t('New playlist')}>
      <label className="grow">
        <span className="visually-hidden">{t('Playlist name')}</span>
        <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} placeholder={t('Name your playlist')} autoFocus />
      </label>
      <AsyncButton className="button" type="submit" disabled={!name.trim()} busy={saving}>
        {t('Create')}
      </AsyncButton>
      <button className="button quiet" type="button" onClick={() => setCreating(false)}>
        {t(Words.cancel)}
      </button>
    </form>
  ) : (
    <button className="button" onClick={() => setCreating(true)}>
      {t('New playlist')}
    </button>
  )

  return (
    <div className="library-page playlists-page">
      <header className="library-header">
        {heading ?? (
          <div>
            <h1>{t('Playlists')}</h1>
            <p className="muted">{t('Your own mixes. Add tracks, albums or whole artists from their ⋯ menu.')}</p>
          </div>
        )}
        {/* With none yet, the button waits in the middle of the page instead. */}
        {!empty && newPlaylist}
      </header>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {playlists.error && <p className="error">{playlists.error.message}</p>}
      {empty ? (
        <div className="playlists-empty">
          <p className="muted">{t('No playlists yet. Make one, or add a track to a new one from its ⋯ menu.')}</p>
          {newPlaylist}
        </div>
      ) : (
        <div className="poster-grid square">
          {playlists.data?.map((p) => (
            <PlaylistCard key={p.id} playlist={p} />
          ))}
        </div>
      )}
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
  // The order as it's being changed: shown at once, the server catching up (a failure reloads what it has).
  const [order, setOrder] = useState<PlaylistTrack[]>()
  const [renaming, setRenaming] = useState(false)
  const [name, setName] = useState('')
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [message, setMessage] = useState<string>()
  // One track's menu at a time: which, and where it opened.
  const [trackMenu, setTrackMenu] = useState<{ index: number; at: MenuPoint } | null>(null)

  const reload = () => {
    setOrder(undefined)
    setVersion((v) => v + 1)
  }

  if (error) return <PageError error={error} message={error.message.includes('404') ? t('That playlist isn’t here any more.') : undefined} onRetry={reload} />
  if (!data) return <PageLoading />

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

  const openMenu = (index: number, at: MenuPoint, e: MouseEvent) => {
    e.preventDefault()
    setTrackMenu(trackMenu?.index === index ? null : { index, at })
  }

  const menuFor = (index: number) => {
    const entry = entries[index]
    const track = entry.track
    return [
      { label: t(Words.play), run: () => player.play(tracks, index) },
      ...queueActions(player, () => [track], track),
      'divider' as const,
      ...(track.parentId ? [{ label: t('Use its cover for the playlist'), run: () => act(() => setPlaylistCover(summary.id, track.parentId)) }] : []),
      { label: t('Remove from playlist'), run: () => remove(entry), danger: true },
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
            <Link to="/playlists">{t('Playlists')}</Link>
          </p>
          {renaming ? (
            <form className="field-row playlist-rename" onSubmit={rename} aria-label={t('Rename playlist')}>
              <label className="grow">
                <span className="visually-hidden">{t('Playlist name')}</span>
                <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} autoFocus />
              </label>
              <AsyncButton className="button" type="submit" disabled={!name.trim()} busy={saving}>
                {t('Save')}
              </AsyncButton>
              <button className="button quiet" type="button" onClick={() => setRenaming(false)}>
                {t(Words.cancel)}
              </button>
            </form>
          ) : (
            <h1>{summary.name}</h1>
          )}
          <p className="facts">
            <span>{t('{count, plural, one {# track} other {# tracks}}', { count: tracks.length })}</span>
            {total > 0 && <span>{formatDuration(total)}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => player.play(tracks, 0, { openNowPlaying: true })} disabled={tracks.length === 0}>
              <icon.play /> {t(Words.play)}
            </button>
            <button className="button quiet" onClick={() => player.play(shuffled(tracks), 0, { openNowPlaying: true })} disabled={tracks.length < 2}>
              {t(Words.shuffle)}
            </button>
            <IconButton
              label={t('Rename')}
              icon={<Pencil />}
              className="button quiet icon"
              onClick={() => {
                setName(summary.name)
                setRenaming(true)
              }}
            />
            <IconButton label={t(Words.delete)} icon={<icon.delete />} className="button quiet icon danger-icon" onClick={() => setDeleting(true)} />
            <MoreButton
              label={t('More for {title}', { title: summary.name })}
              actions={[...queueActions(player, () => tracks), ...(summary.cover ? [{ label: t('Back to the mosaic cover'), run: () => act(() => setPlaylistCover(summary.id, null)) }] : [])]}
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
        <p className="muted collection-empty">{t('Nothing in it yet. Add tracks, albums or artists from their ⋯ menu: Add to playlist.')}</p>
      ) : (
        <PlaylistEntries
          entries={entries}
          label={t('Tracks in {name}', { name: summary.name })}
          currentId={player.current?.id}
          playing={player.playing}
          menuIndex={trackMenu?.index}
          actions={{ play: (at) => player.play(tracks, at), move, remove: (at) => remove(entries[at]), openMenu }}
        />
      )}

      {trackMenu && entries[trackMenu.index] && (
        <Menu label={t('More for {title}', { title: entries[trackMenu.index].track.title })} at={trackMenu.at} actions={menuFor(trackMenu.index)} onClose={() => setTrackMenu(null)} />
      )}
      {deleting && (
        <ConfirmDialog
          title={t('Delete {name}?', { name: summary.name })}
          confirmLabel={t('Delete playlist')}
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
          <p>{t('The tracks stay in your library.')}</p>
        </ConfirmDialog>
      )}
    </article>
  )
}

/** What a playlist's track can have done to it, by its place in the list. */
interface EntryActions {
  play(at: number): void
  move(from: number, to: number): void
  remove(at: number): void
  openMenu(at: number, point: MenuPoint, e: MouseEvent): void
}

/** What a row is given to work with: the playlist's actions, and dragging it to a new place. */
interface RowActions extends EntryActions {
  dragStart(at: number, track: ItemSummary, e: DragEvent): void
  dragOver(at: number, e: DragEvent): void
  dragLeave(at: number): void
  drop(at: number, e: DragEvent): void
  dragEnd(): void
}

/**
 * A playlist's tracks, in order, to play, drag into a new order, move with the arrow buttons or remove. Each row is
 * drawn again only when something of its own changes: a drag step redraws the two rows it passes between, and the
 * player moving on the two tracks it moves between, however long the playlist.
 */
function PlaylistEntries({
  entries,
  label,
  currentId,
  playing,
  menuIndex,
  actions,
}: {
  entries: PlaylistTrack[]
  label: string
  currentId: string | undefined
  playing: boolean
  menuIndex: number | undefined
  actions: EntryActions
}) {
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  // The actions as of the last render: the rows get one unchanging set (`rowActions`) that calls these.
  const latest = useRef(actions)
  const drag = useRef({ dragging, over })
  useLayoutEffect(() => {
    latest.current = actions
    drag.current = { dragging, over }
  })
  const rowActions = useMemo<RowActions>(() => {
    const end = () => {
      setDragging(null)
      setOver(null)
    }
    return {
      play: (at) => latest.current.play(at),
      move: (from, to) => latest.current.move(from, to),
      remove: (at) => latest.current.remove(at),
      openMenu: (at, point, e) => latest.current.openMenu(at, point, e),
      dragStart: (at, track, e) => {
        setDragging(at)
        drag.current.dragging = at
        e.dataTransfer.effectAllowed = 'move'
        e.dataTransfer.setData('text/plain', track.title)
      },
      dragOver: (at, e) => {
        if (drag.current.dragging === null) return
        e.preventDefault()
        e.dataTransfer.dropEffect = 'move'
        setOver(at)
      },
      dragLeave: (at) => setOver((o) => (o === at ? null : o)),
      drop: (at, e) => {
        e.preventDefault()
        const from = drag.current.dragging
        if (from !== null && from !== at) latest.current.move(from, at)
        end()
      },
      dragEnd: end,
    }
  }, [])

  return (
    <ol className="tracklist playlist-tracks" aria-label={label}>
      {entries.map(({ entryId, track }, i) => {
        const isCurrent = currentId === track.id
        return (
          <PlaylistEntryRow
            key={entryId}
            entryId={entryId}
            track={track}
            index={i}
            last={i === entries.length - 1}
            isCurrent={isCurrent}
            playing={isCurrent && playing}
            dragging={dragging === i}
            over={over === i}
            menuOpen={menuIndex === i}
            actions={rowActions}
          />
        )
      })}
    </ol>
  )
}

/** One track of a playlist (see PlaylistEntries). */
const PlaylistEntryRow = memo(function PlaylistEntryRow({
  entryId,
  track,
  index: i,
  last,
  isCurrent,
  playing,
  dragging,
  over,
  menuOpen,
  actions,
}: {
  entryId: number
  track: ItemSummary
  index: number
  last: boolean
  isCurrent: boolean
  playing: boolean
  dragging: boolean
  over: boolean
  menuOpen: boolean
  actions: RowActions
}) {
  return (
    <li
      data-entry-id={entryId}
      className={['playlist-entry', dragging && 'dragging', over && 'drag-over'].filter(Boolean).join(' ')}
      draggable
      onDragStart={(e) => actions.dragStart(i, track, e)}
      onDragOver={(e) => actions.dragOver(i, e)}
      onDragLeave={() => actions.dragLeave(i)}
      onDrop={(e) => actions.drop(i, e)}
      onDragEnd={actions.dragEnd}
      onContextMenu={(e) => actions.openMenu(i, { x: e.clientX, y: e.clientY }, e)}
    >
      <span className="queue-grip" aria-hidden="true">
        <Grip />
      </span>
      <button className={isCurrent ? 'track current' : 'track'} onClick={() => actions.play(i)} aria-current={isCurrent || undefined}>
        <span className="track-number">{playing ? <Play /> : i + 1}</span>
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
        <button className="icon-button" aria-label={t('Move {title} up', { title: track.title })} disabled={i === 0} onClick={() => actions.move(i, i - 1)}>
          <ChevronUp />
        </button>
        <button className="icon-button" aria-label={t('Move {title} down', { title: track.title })} disabled={last} onClick={() => actions.move(i, i + 1)}>
          <ChevronDown />
        </button>
        <button className="icon-button" aria-label={t('Remove {title} from the playlist', { title: track.title })} onClick={() => actions.remove(i)}>
          <Close />
        </button>
        <button
          className="icon-button"
          aria-label={t('More for {title}', { title: track.title })}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          onClick={(e) => {
            const r = e.currentTarget.getBoundingClientRect()
            actions.openMenu(i, { x: r.right - 220, y: r.bottom + 6 }, e)
          }}
        >
          <More />
        </button>
      </span>
    </li>
  )
})
