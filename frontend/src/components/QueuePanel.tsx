import { useEffect, useRef, useState, type DragEvent } from 'react'
import { imageUrl, type ItemSummary } from '../api/client'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { ChevronDown, ChevronUp, Close, Grip } from './icons'

/**
 * The queue, above the now-playing bar: what's playing and what's to come. Upcoming tracks can be dragged into a new
 * order (or moved with the arrow buttons from the keyboard), removed, or cleared in one go. Escape or a click
 * elsewhere closes it.
 */
export function QueuePanel({ onClose }: { onClose: () => void }) {
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

  const move = (from: number, to: number, dir: 'up' | 'down') => {
    player.moveInQueue(from, to)
    focusAfter.current = { index: to, dir }
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
    if (dragging !== null && dragging !== at) player.moveInQueue(dragging, at)
    setDragging(null)
    setOver(null)
  }
  const dragEnd = () => {
    setDragging(null)
    setOver(null)
  }

  const art = (track: ItemSummary) => (
    <span className="queue-art" aria-hidden="true">
      {track.artwork.poster && <img src={imageUrl(track.artwork.poster, 92)} alt="" />}
    </span>
  )
  const text = (track: ItemSummary) => (
    <span className="queue-text">
      <strong>{track.title}</strong>
      <span className="muted">{track.seriesTitle}</span>
    </span>
  )

  return (
    <section id="queue-panel" ref={panel} className="queue-panel" role="dialog" aria-label="Queue">
      <header className="queue-head">
        <h2>Queue</h2>
        <button className="button quiet small" onClick={player.clearQueue} disabled={upcoming.length === 0}>
          Clear
        </button>
        <button className="icon-button" aria-label="Close queue" onClick={onClose}>
          <Close />
        </button>
      </header>
      <div className="queue-body">
        {current && (
          <>
            <h3 className="queue-heading">Now playing</h3>
            <div className="queue-item now">
              <div className="queue-track">
                {art(current)}
                {text(current)}
              </div>
            </div>
          </>
        )}
        <h3 className="queue-heading">Next up</h3>
        {upcoming.length === 0 ? (
          <p className="queue-empty muted">Nothing next. “Play next” or “Add to queue” on a track puts it here.</p>
        ) : (
          <ol className="queue-list">
            {upcoming.map(({ track, index: at }, k) => (
              <li
                key={`${track.id}-${at}`}
                data-queue-index={at}
                className={['queue-item', dragging === at && 'dragging', over === at && 'drag-over'].filter(Boolean).join(' ')}
                draggable
                onDragStart={dragStart(at, track)}
                onDragOver={dragOver(at)}
                onDragLeave={() => over === at && setOver(null)}
                onDrop={drop(at)}
                onDragEnd={dragEnd}
              >
                <span className="queue-grip" aria-hidden="true">
                  <Grip />
                </span>
                <button className="queue-track" onClick={() => player.skipTo(at)} aria-label={`Play ${track.title} now`}>
                  {art(track)}
                  {text(track)}
                </button>
                <span className="queue-actions">
                  <button className="icon-button queue-move-up" aria-label={`Move ${track.title} up`} disabled={k === 0} onClick={() => move(at, at - 1, 'up')}>
                    <ChevronUp />
                  </button>
                  <button
                    className="icon-button queue-move-down"
                    aria-label={`Move ${track.title} down`}
                    disabled={k === upcoming.length - 1}
                    onClick={() => move(at, at + 1, 'down')}
                  >
                    <ChevronDown />
                  </button>
                  <button className="icon-button" aria-label={`Remove ${track.title} from the queue`} onClick={() => player.removeFromQueue(at)}>
                    <Close />
                  </button>
                </span>
              </li>
            ))}
          </ol>
        )}
      </div>
    </section>
  )
}
