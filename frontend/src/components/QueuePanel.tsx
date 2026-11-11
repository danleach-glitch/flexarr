import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent } from 'react'
import { imageUrl, type ItemSummary } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { ChevronDown, ChevronUp, Close, Grip } from './icons'
import { t, Words } from '../i18n'

/**
 * Keys for the queue's entries that stay with a track when others move or go: its id, and which time it appears in the
 * queue (a track can be queued twice). Not its place, so removing an early track doesn't redraw every row after it.
 */
function queueKeys(queue: readonly ItemSummary[]): string[] {
  const seen = new Map<string, number>()
  return queue.map((track) => {
    const n = seen.get(track.id) ?? 0
    seen.set(track.id, n + 1)
    return `${track.id}#${n}`
  })
}

/**
 * The queue, above the now-playing bar: what's playing and what's to come. Upcoming tracks can be dragged into a new
 * order (or moved with the arrow buttons from the keyboard), removed, or cleared in one go (with Undo). Escape or a
 * click elsewhere closes it.
 */
export function QueuePanel({ onClose, style }: { onClose: () => void; style?: CSSProperties }) {
  const player = useAudioPlayer()
  const panel = useRef<HTMLElement>(null)
  const [dragging, setDragging] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)
  /** The tracks Clear took away, for Undo, until something else is queued. */
  const [cleared, setCleared] = useState<ItemSummary[]>()
  // After a move or a removal, focus goes to the row (by its entry key) that React renders afresh; or to Close.
  const focusAfter = useRef<{ key: string; dir: 'up' | 'down' } | { key: string; dir: 'track' } | 'close' | null>(null)

  const current = player.current
  const keys = useMemo(() => queueKeys(player.queue), [player.queue])
  const upcoming = player.queue.slice(player.index + 1).map((track, i) => ({ track, key: keys[player.index + 1 + i] }))
  const undoable = cleared !== undefined && upcoming.length === 0

  useEffect(() => {
    panel.current?.querySelector<HTMLElement>('button')?.focus()
  }, [])

  // Something queued after a Clear: there's no going back to before it.
  useEffect(() => {
    if (upcoming.length > 0) setCleared(undefined)
  }, [upcoming.length])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    const onPointer = (e: PointerEvent) => {
      const target = e.target
      if (!(target instanceof Node) || panel.current?.contains(target)) return
      // The bar's own button toggles the panel; a click there mustn't close it first.
      if (target instanceof Element && target.closest('.now-playing')) return
      onClose()
    }
    document.addEventListener('keydown', onKey)
    document.addEventListener('pointerdown', onPointer)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('pointerdown', onPointer)
    }
  }, [onClose])

  useEffect(() => {
    const want = focusAfter.current
    if (!want) return
    focusAfter.current = null
    if (want === 'close') {
      panel.current?.querySelector<HTMLButtonElement>('.queue-close')?.focus()
      return
    }
    const row = panel.current?.querySelector<HTMLElement>(`[data-queue-key="${CSS.escape(want.key)}"]`)
    if (want.dir === 'track') {
      row?.querySelector<HTMLButtonElement>('.queue-track')?.focus()
      return
    }
    const same = row?.querySelector<HTMLButtonElement>(`.queue-move-${want.dir}`)
    const other = row?.querySelector<HTMLButtonElement>(want.dir === 'up' ? '.queue-move-down' : '.queue-move-up')
    ;(same && !same.disabled ? same : other)?.focus()
  })

  // The player as of the last render: the rows get one unchanging set of actions (`rowActions`) that use it, so a drag
  // step or the player moving on redraws only the rows whose own props changed, however long the queue.
  const latest = useRef({ player, keys })
  const drag = useRef({ dragging, over })
  useLayoutEffect(() => {
    latest.current = { player, keys }
    drag.current = { dragging, over }
  })
  const rowActions = useMemo<QueueRowActions>(() => {
    /** An upcoming entry's place in the queue now (-1 when it's gone). */
    const indexOf = (key: string) => {
      const { player, keys } = latest.current
      return keys.indexOf(key, player.index + 1)
    }
    const end = () => {
      setDragging(null)
      setOver(null)
    }
    return {
      skipTo: (key) => {
        const at = indexOf(key)
        if (at >= 0) latest.current.player.skipTo(at)
      },
      move: (key, dir) => {
        const at = indexOf(key)
        if (at < 0) return
        latest.current.player.moveInQueue(at, dir === 'up' ? at - 1 : at + 1)
        focusAfter.current = { key, dir }
      },
      remove: (key) => {
        const { player, keys } = latest.current
        const at = indexOf(key)
        if (at < 0) return
        // Focus goes to the track after it, or before it when it was the last; to Close when none is left.
        const next = keys[at + 1] ?? (at - 1 > player.index ? keys[at - 1] : undefined)
        focusAfter.current = next ? { key: next, dir: 'track' } : 'close'
        player.removeFromQueue(at)
      },
      dragStart: (key, track, e) => {
        setDragging(key)
        drag.current.dragging = key
        e.dataTransfer.effectAllowed = 'move'
        e.dataTransfer.setData('text/plain', track.title)
      },
      dragOver: (key, e) => {
        if (drag.current.dragging === null) return
        e.preventDefault()
        e.dataTransfer.dropEffect = 'move'
        setOver(key)
      },
      dragLeave: (key) => setOver((o) => (o === key ? null : o)),
      drop: (key, e) => {
        e.preventDefault()
        const from = drag.current.dragging === null ? -1 : indexOf(drag.current.dragging)
        const to = indexOf(key)
        if (from >= 0 && to >= 0 && from !== to) latest.current.player.moveInQueue(from, to)
        end()
      },
      dragEnd: end,
    }
  }, [])

  const clear = () => {
    setCleared(upcoming.map((u) => u.track))
    player.clearQueue()
  }
  const undo = () => {
    if (cleared) player.playNext(cleared)
    setCleared(undefined)
  }

  return (
    <section id="queue-panel" ref={panel} className="queue-panel" role="dialog" aria-label={t('Queue')} style={style}>
      <header className="queue-head">
        <h2>{t('Queue')}</h2>
        {/* One button, so focus stays on it from Clear to Undo and back. */}
        <button className="button quiet small" onClick={undoable ? undo : clear} disabled={!undoable && upcoming.length === 0}>
          {undoable ? t('Undo') : t('Clear')}
        </button>
        <button className="icon-button queue-close" aria-label={t('Close queue')} onClick={onClose}>
          <Close />
        </button>
      </header>
      <div className="queue-body">
        {current && (
          <>
            <h3 className="queue-heading">{t(Words.nowPlaying)}</h3>
            <div className="queue-item now">
              <div className="queue-track">
                <QueueTrackFace track={current} />
              </div>
            </div>
          </>
        )}
        <h3 className="queue-heading">{t('Next up')}</h3>
        {upcoming.length === 0 ? (
          <p className="queue-empty muted">{t('Nothing next. “Play next” or “Add to queue” on a track puts it here.')}</p>
        ) : (
          <ol className="queue-list">
            {upcoming.map(({ track, key }, k) => (
              <QueueRow
                key={key}
                entry={key}
                track={track}
                first={k === 0}
                last={k === upcoming.length - 1}
                dragging={dragging === key}
                over={over === key}
                actions={rowActions}
              />
            ))}
          </ol>
        )}
      </div>
    </section>
  )
}

/** What an upcoming track's row can have done to it, by its entry key (`queueKeys`). */
interface QueueRowActions {
  skipTo(entry: string): void
  move(entry: string, dir: 'up' | 'down'): void
  remove(entry: string): void
  dragStart(entry: string, track: ItemSummary, e: DragEvent): void
  dragOver(entry: string, e: DragEvent): void
  dragLeave(entry: string): void
  drop(entry: string, e: DragEvent): void
  dragEnd(): void
}

/** A track's cover, title and artist. */
function QueueTrackFace({ track }: { track: ItemSummary }) {
  return (
    <>
      <span className="queue-art" aria-hidden="true">
        {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 92)} alt="" loading="lazy" decoding="async" />}
      </span>
      <span className="queue-text">
        <strong>{track.title}</strong>
        <span className="muted">{track.trackArtist ?? track.seriesTitle}</span>
      </span>
    </>
  )
}

/** One upcoming track, drawn again only when something of its own changes (see `rowActions`); its place isn't a prop. */
const QueueRow = memo(function QueueRow({
  entry,
  track,
  first,
  last,
  dragging,
  over,
  actions,
}: {
  entry: string
  track: ItemSummary
  first: boolean
  last: boolean
  dragging: boolean
  over: boolean
  actions: QueueRowActions
}) {
  return (
    <li
      data-queue-key={entry}
      className={['queue-item', dragging && 'dragging', over && 'drag-over'].filter(Boolean).join(' ')}
      draggable
      onDragStart={(e) => actions.dragStart(entry, track, e)}
      onDragOver={(e) => actions.dragOver(entry, e)}
      onDragLeave={() => actions.dragLeave(entry)}
      onDrop={(e) => actions.drop(entry, e)}
      onDragEnd={actions.dragEnd}
    >
      <span className="queue-grip" aria-hidden="true">
        <Grip />
      </span>
      <button className="queue-track" onClick={() => actions.skipTo(entry)} aria-label={t('Play {title} now', { title: track.title })}>
        <QueueTrackFace track={track} />
      </button>
      <span className="queue-actions">
        <button className="icon-button queue-move-up" aria-label={t('Move {title} up', { title: track.title })} disabled={first} onClick={() => actions.move(entry, 'up')}>
          <ChevronUp />
        </button>
        <button className="icon-button queue-move-down" aria-label={t('Move {title} down', { title: track.title })} disabled={last} onClick={() => actions.move(entry, 'down')}>
          <ChevronDown />
        </button>
        <button className="icon-button" aria-label={t('Remove {title} from the queue', { title: track.title })} onClick={() => actions.remove(entry)}>
          <Close />
        </button>
      </span>
    </li>
  )
})
