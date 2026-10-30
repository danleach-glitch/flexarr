import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate } from 'react-router'
import { addToWatchlist, ApiError, getTracks, removeFromContinueWatching, removeFromWatchlist, setWatched, type ItemSummary } from '../api/client'
import { openAddToCollection } from './AddToCollection'
import { openAddToPlaylist } from './AddToPlaylist'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { usePlay } from '../hooks/usePlay'
import { useWatchlistKeys } from '../hooks/useWatchlistKeys'
import { showNotice } from '../lib/notice'
import { announceWatched } from '../hooks/watched'
import { episodeCode, progressOf } from '../lib/format'

export interface MenuPoint {
  x: number
  y: number
}

/** Films, TV and albums get menus; artists and tracks have their own pages. */
export const hasMenu = (item: ItemSummary) => ['Movie', 'Episode', 'Season', 'Series', 'Album'].includes(item.kind)

export type MenuAction = { label: string; run: () => void | Promise<void>; danger?: boolean } | 'divider'

/**
 * Quick actions for a title from its card: play it, or mark it watched. (The card's text links to its pages.) Something
 * part way through can be marked either way: unwatched forgets the place reached. `continueWatching` is for cards in
 * that row, where a show's next episode can be taken out of it.
 */
export function ItemMenu({ item, at, onClose, continueWatching = false }: { item: ItemSummary; at: MenuPoint; onClose: () => void; continueWatching?: boolean }) {
  const navigate = useNavigate()
  const play = usePlay()
  const music = useAudioPlayer()
  const mark =(target: Pick<ItemSummary, 'id' | 'parentId' | 'seriesId'>, watched: boolean) => async () => {
    await setWatched(target.id, watched)
    announceWatched(target)
  }
  const go = (to: string) => () => navigate(to)
  const resumable = progressOf(item) !== null
  const started = resumable && !item.userData.watched
  const remove = async () => {
    await removeFromContinueWatching(item.id)
    announceWatched(item)
  }
  const showId = item.kind === 'Series' ? item.id : (item.seriesId ?? (item.kind === 'Season' ? item.parentId : null))
  const show = item.seriesTitle ?? 'the show'
  // Add or Remove, once it's known which (and not at all on a server without a watchlist).
  const watchlist = useWatchlistKeys()
  const watchlistAction = (): MenuAction[] => {
    if (!watchlist) return []
    const key = { libraryItemId: item.id }
    const failed = (e: unknown) => showNotice(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'That didn’t work. Try again.')
    return watchlist.libraryItemIds.includes(item.id)
      ? [{ label: 'Remove from watchlist', run: () => removeFromWatchlist(key).catch(failed) }]
      : [{ label: 'Add to watchlist', run: () => addToWatchlist(key).then(() => showNotice(`Added ${item.title} to your watchlist`), failed) }]
  }

  const actions: MenuAction[] = []
  switch (item.kind) {
    case 'Movie':
      actions.push({ label: resumable ? 'Resume' : 'Play', run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: 'Play from the start', run: go(`/play/${item.id}?t=0`) })
      actions.push('divider')
      actions.push({ label: item.userData.watched ? 'Mark as unwatched' : 'Mark as watched', run: mark(item, !item.userData.watched) })
      if (started) actions.push({ label: 'Mark as unwatched', run: mark(item, false) })
      actions.push(...watchlistAction())
      actions.push({ label: 'Add to collection…', run: () => openAddToCollection(item) })
      break
    case 'Episode':
      actions.push({ label: [resumable ? 'Resume' : 'Play', episodeCode(item)].filter(Boolean).join(' '), run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: 'Play from the start', run: go(`/play/${item.id}?t=0`) })
      actions.push('divider', { label: item.userData.watched ? 'Mark episode as unwatched' : 'Mark episode as watched', run: mark(item, !item.userData.watched) })
      if (started) actions.push({ label: 'Mark episode as unwatched', run: mark(item, false) })
      else if (continueWatching && !item.userData.watched) actions.push({ label: 'Remove from Continue Watching', run: remove })
      if (showId) actions.push({ label: `Mark all of ${show} as watched`, run: mark({ id: showId, parentId: null, seriesId: null }, true) })
      break
    case 'Season':
      actions.push({ label: 'Play', run: () => (showId ? play({ ...item, id: showId, kind: 'Series' }) : navigate(`/item/${item.id}`)) })
      actions.push('divider', { label: item.userData.watched ? 'Mark season as unwatched' : 'Mark season as watched', run: mark(item, !item.userData.watched) })
      break
    case 'Series':
      actions.push({ label: 'Play next episode', run: () => play(item) }, 'divider')
      actions.push({ label: item.userData.watched ? 'Mark show as unwatched' : 'Mark show as watched', run: mark(item, !item.userData.watched) })
      actions.push(...watchlistAction())
      actions.push({ label: 'Add to collection…', run: () => openAddToCollection(item) })
      break
    case 'Album':
      // The album's tracks are fetched when asked for (the card only has the album).
      actions.push(
        { label: 'Play', run: async () => music.play(await getTracks(item.id), 0, { openNowPlaying: true }) },
        { label: 'Play next', run: async () => music.playNext(await getTracks(item.id)) },
        { label: 'Add to queue', run: async () => music.enqueue(await getTracks(item.id)) },
        { label: 'Add to playlist…', run: () => openAddToPlaylist(item) },
      )
      break
  }

  return <Menu label={item.title} at={at} actions={actions} onClose={onClose} />
}

/** A popup list of actions, anchored at a point and kept on screen; Escape, Tab or a click elsewhere closes it. */
export function Menu({ label, at, actions, onClose }: { label: string; at: MenuPoint; actions: MenuAction[]; onClose: () => void }) {
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
    <div ref={menu} className="item-menu" role="menu" aria-label={label} style={{ left: position.x, top: position.y }} onKeyDown={onKeyDown}>
      {actions.map((a, i) =>
        a === 'divider' ? (
          <hr key={`d${i}`} />
        ) : (
          <button
            key={a.label}
            role="menuitem"
            className={a.danger ? 'danger' : undefined}
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
