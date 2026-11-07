import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent } from 'react'
import { imageUrl, type ItemSummary } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { ChevronDown, ChevronUp, Close, Grip } from './icons'
import { t, Words } from '../i18n'

/**
 * The queue, above the now-playing bar: what's playing and what's to come. Upcoming tracks can be dragged into a new
 * order (or moved with the arrow buttons from the keyboard), removed, or cleared in one go. Escape or a click
 * elsewhere closes it.
 */
export function QueuePanel({ onClose, style }: { onClose: () => void; style?: CSSProperties }) {
  const player = useAudioPlayer()
  const panel = useRef<HTMLElement>(null)
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  // After a move, focus goes with the track to its new row (which React renders afresh).
  const focusAfter = useRef<{ index: number; dir: 'up' | 'down' } | null>(null)

  const current = player.current
  const upcoming = player.queue.slice(player.index + 1).map((track, i) => ({ track, index: player.index + 1 + i }))

  useEffect(() => {
    panel.current?.querySelector<HTMLElement>('button')?.focus()
  }, [])

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
    const row = panel.current?.querySelector<HTMLElement>(`[data-queue-index="${want.index}"]`)
    const same = row?.querySelector<HTMLButtonElement>(`.queue-move-${want.dir}`)
    const other = row?.querySelector<HTMLButtonElement>(want.dir === 'up' ? '.queue-move-down' : '.queue-move-up')
    ;(same && !same.disabled ? same : other)?.focus()
  })

  // The player as of the last render: the rows get one unchanging set of actions (`rowActions`) that use it, so a drag
  // step or the player moving on redraws only the rows whose own props changed, however long the queue.
  const latest = useRef(player)
  const drag = useRef({ dragging, over })
  useLayoutEffect(() => {
    latest.current = player
    drag.current = { dragging, over }
  })
  const rowActions = useMemo<QueueRowActions>(() => {
    const end = () => {
      setDragging(null)
      setOver(null)
    }
    return {
      skipTo: (at) => latest.current.skipTo(at),
      move: (from, to, dir) => {
        latest.current.moveInQueue(from, to)
        focusAfter.current = { index: to, dir }
      },
      remove: (at) => latest.current.removeFromQueue(at),
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
        if (from !== null && from !== at) latest.current.moveInQueue(from, at)
        end()
      },
      dragEnd: end,
    }
  }, [])

  return (
    <section id="queue-panel" ref={panel} className="queue-panel" role="dialog" aria-label={t('Queue')} style={style}>
      <header className="queue-head">
        <h2>{t('Queue')}</h2>
        <button className="button quiet small" onClick={player.clearQueue} disabled={upcoming.length === 0}>
          {t('Clear')}
        </button>
        <button className="icon-button" aria-label={t('Close queue')} onClick={onClose}>
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
            {upcoming.map(({ track, index: at }, k) => (
              <QueueRow
                key={`${track.id}-${at}`}
                track={track}
                at={at}
                first={k === 0}
                last={k === upcoming.length - 1}
                dragging={dragging === at}
                over={over === at}
                actions={rowActions}
              />
            ))}
          </ol>
        )}
      </div>
    </section>
  )
}

/** What an upcoming track's row can have done to it, by its place in the queue. */
interface QueueRowActions {
  skipTo(at: number): void
  move(from: number, to: number, dir: 'up' | 'down'): void
  remove(at: number): void
  dragStart(at: number, track: ItemSummary, e: DragEvent): void
  dragOver(at: number, e: DragEvent): void
  dragLeave(at: number): void
  drop(at: number, e: DragEvent): void
  dragEnd(): void
}

/** A track's cover, title and artist. */
function QueueTrackFace({ track }: { track: ItemSummary }) {
  return (
    <>
      <span className="queue-art" aria-hidden="true">
        {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 92)} alt="" loading="lazy" />}
      </span>
      <span className="queue-text">
        <strong>{track.title}</strong>
        <span className="muted">{track.trackArtist ?? track.seriesTitle}</span>
      </span>
    </>
  )
}

/** One upcoming track, drawn again only when something of its own changes (see `rowActions`). */
const QueueRow = memo(function QueueRow({
  track,
  at,
  first,
  last,
  dragging,
  over,
  actions,
}: {
  track: ItemSummary
  at: number
  first: boolean
  last: boolean
  dragging: boolean
  over: boolean
  actions: QueueRowActions
}) {
  return (
    <li
      data-queue-index={at}
      className={['queue-item', dragging && 'dragging', over && 'drag-over'].filter(Boolean).join(' ')}
      draggable
      onDragStart={(e) => actions.dragStart(at, track, e)}
      onDragOver={(e) => actions.dragOver(at, e)}
      onDragLeave={() => actions.dragLeave(at)}
      onDrop={(e) => actions.drop(at, e)}
      onDragEnd={actions.dragEnd}
    >
      <span className="queue-grip" aria-hidden="true">
        <Grip />
      </span>
      <button className="queue-track" onClick={() => actions.skipTo(at)} aria-label={t('Play {title} now', { title: track.title })}>
        <QueueTrackFace track={track} />
      </button>
      <span className="queue-actions">
        <button className="icon-button queue-move-up" aria-label={t('Move {title} up', { title: track.title })} disabled={first} onClick={() => actions.move(at, at - 1, 'up')}>
          <ChevronUp />
        </button>
        <button className="icon-button queue-move-down" aria-label={t('Move {title} down', { title: track.title })} disabled={last} onClick={() => actions.move(at, at + 1, 'down')}>
          <ChevronDown />
        </button>
        <button className="icon-button" aria-label={t('Remove {title} from the queue', { title: track.title })} onClick={() => actions.remove(at)}>
          <Close />
        </button>
      </span>
    </li>
  )
})
