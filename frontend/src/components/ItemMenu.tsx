import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate } from 'react-router'
import { setWatched, type ItemSummary } from '../api/client'
import { usePlay } from '../hooks/usePlay'
import { episodeCode, progressOf } from '../lib/format'

/** Fired after something is marked watched or unwatched, so pages showing it (Continue Watching) reload. */
export const WATCHED_CHANGED = 'flexarr:watched-changed'

export function useWatchedChanges(reload: () => void) {
  useEffect(() => {
    window.addEventListener(WATCHED_CHANGED, reload)
    return () => window.removeEventListener(WATCHED_CHANGED, reload)
  }, [reload])
}

export interface MenuPoint {
  x: number
  y: number
}

/** Films and TV get menus; music has its own pages. */
export const hasMenu = (item: ItemSummary) => ['Movie', 'Episode', 'Season', 'Series'].includes(item.kind)

type Action = { label: string; run: () => void | Promise<void> } | 'divider'

/** Quick actions for a title from its card: play it, or mark it watched. (The card's text links to its pages.) */
export function ItemMenu({ item, at, onClose }: { item: ItemSummary; at: MenuPoint; onClose: () => void }) {
  const navigate = useNavigate()
  const play = usePlay()
  const menu = useRef<HTMLDivElement>(null)
  const [position, setPosition] = useState(at)

  // Keep the menu on screen, then focus its first item.
  useLayoutEffect(() => {
    const el = menu.current
    if (!el) return
    const { width, height } = el.getBoundingClientRect()
    setPosition({ x: Math.max(8, Math.min(at.x, innerWidth - width - 8)), y: at.y + height > innerHeight - 8 ? Math.max(8, at.y - height - 12) : at.y })
    el.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus()
  }, [at])

  useEffect(() => {
    const close = (e: Event) => {
      if (e.type === 'pointerdown' && menu.current?.contains(e.target as Node)) return
      onClose()
    }
    window.addEventListener('pointerdown', close)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [onClose])

  const mark = (id: string, watched: boolean) => async () => {
    await setWatched(id, watched)
    window.dispatchEvent(new Event(WATCHED_CHANGED))
  }
  const go = (to: string) => () => navigate(to)
  const resumable = progressOf(item) !== null
  const showId = item.kind === 'Series' ? item.id : (item.seriesId ?? (item.kind === 'Season' ? item.parentId : null))
  const show = item.seriesTitle ?? 'the show'

  const actions: Action[] = []
  switch (item.kind) {
    case 'Movie':
      actions.push({ label: resumable ? 'Resume' : 'Play', run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: 'Play from the start', run: go(`/play/${item.id}?t=0`) })
      actions.push('divider')
      actions.push({ label: item.userData.watched ? 'Mark as unwatched' : 'Mark as watched', run: mark(item.id, !item.userData.watched) })
      break
    case 'Episode':
      actions.push({ label: `${resumable ? 'Resume' : 'Play'} ${episodeCode(item)}`, run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: 'Play from the start', run: go(`/play/${item.id}?t=0`) })
      actions.push('divider', { label: item.userData.watched ? 'Mark episode as unwatched' : 'Mark episode as watched', run: mark(item.id, !item.userData.watched) })
      if (showId) actions.push({ label: `Mark all of ${show} as watched`, run: mark(showId, true) })
      break
    case 'Season':
      actions.push({ label: 'Play', run: () => (showId ? play({ ...item, id: showId, kind: 'Series' }) : navigate(`/item/${item.id}`)) })
      actions.push('divider', { label: item.userData.watched ? 'Mark season as unwatched' : 'Mark season as watched', run: mark(item.id, !item.userData.watched) })
      break
    case 'Series':
      actions.push({ label: 'Play next episode', run: () => play(item) }, 'divider')
      actions.push({ label: item.userData.watched ? 'Mark show as unwatched' : 'Mark show as watched', run: mark(item.id, !item.userData.watched) })
      break
  }

  function onKeyDown(e: KeyboardEvent) {
    const items = [...(menu.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
    const index = items.indexOf(document.activeElement as HTMLButtonElement)
    if (e.key === 'Escape' || e.key === 'Tab') {
      e.preventDefault()
      onClose()
    } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      items[(index + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus()
    }
  }

  return createPortal(
    <div ref={menu} className="item-menu" role="menu" aria-label={item.title} style={{ left: position.x, top: position.y }} onKeyDown={onKeyDown}>
      {actions.map((a, i) =>
        a === 'divider' ? (
          <hr key={`d${i}`} />
        ) : (
          <button
            key={a.label}
            role="menuitem"
            onClick={async () => {
              onClose()
              await a.run()
            }}
          >
            {a.label}
          </button>
        ),
      )}
    </div>,
    document.body,
  )
}
