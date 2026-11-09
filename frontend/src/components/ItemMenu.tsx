import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate } from 'react-router'
import { addToWatchlist, ApiError, getTracks, watchlistChanged, removeFromContinueWatching, removeFromWatchlist, setBookFinished, setWatched, type ItemSummary } from '../api/client'
import { openAddToCollection } from './AddToCollection'
import { openAddToPlaylist } from './AddToPlaylist'
import { useAgeLimits } from '../hooks/sessionContext'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { usePlay } from '../hooks/usePlay'
import { useWatchlistKeys } from '../hooks/useWatchlistKeys'
import { showNotice } from '../lib/notice'
import { announceWatched } from '../hooks/watched'
import { episodeCode, progressOf } from '../lib/format'
import { t, Words } from '../i18n'

export interface MenuPoint {
  x: number
  y: number
}

/** Films, TV, albums and books get menus; artists and tracks have their own pages. Podcast episodes do in Continue watching. */
export const hasMenu = (item: ItemSummary, continueWatching = false) =>
  ['Movie', 'Episode', 'Season', 'Series', 'Album', 'Audiobook'].includes(item.kind) || (continueWatching && item.kind === 'PodcastEpisode')

/** `marks` for an action that changes watch state, which a child's menus leave out. `checked` makes it one of a set to choose from. */
export type MenuAction = { label: string; run: () => void | Promise<void>; danger?: boolean; marks?: boolean; checked?: boolean } | 'divider'

/**
 * Quick actions for a title from its card: play it, or mark it watched. (The card's text links to its pages.) Something
 * part way through can be marked either way: unwatched forgets the place reached. `continueWatching` is for cards in
 * that row, where a show's next episode can be taken out of it.
 */
export function ItemMenu({ item, at, onClose, continueWatching = false }: { item: ItemSummary; at: MenuPoint; onClose: () => void; continueWatching?: boolean }) {
  const navigate = useNavigate()
  const { kids } = useAgeLimits()
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
    // A book or episode loaded in the audio player stops first, so its place isn't sent again (putting it back).
    music.forget(item)
    await removeFromContinueWatching(item.id)
    announceWatched(item)
  }
  const showId = item.kind === 'Series' ? item.id : (item.seriesId ?? (item.kind === 'Season' ? item.parentId : null))
  // Add or Remove, once it's known which (and not at all on a server without a watchlist).
  const watchlist = useWatchlistKeys(!kids)
  const watchlistAction = (): MenuAction[] => {
    if (!watchlist || kids) return []
    const key = { libraryItemId: item.id }
    const failed = (e: unknown) => showNotice(e instanceof ApiError && e.messages[0] ? e.messages[0] : t('That didn’t work. Try again.'))
    return watchlist.libraryItemIds.includes(item.id)
      ? [
          {
            label: t(Words.removeFromWatchlist),
            // Gone already (it left when it was watched) is what was asked.
            run: () => removeFromWatchlist(key).catch((e: unknown) => (e instanceof ApiError && e.status === 404 ? watchlistChanged() : failed(e))),
          },
        ]
      : [{ label: t(Words.addToWatchlist), run: () => addToWatchlist(key).then(() => showNotice(t('Added {title} to your watchlist', { title: item.title })), failed) }]
  }

  const actions: MenuAction[] = []
  switch (item.kind) {
    case 'Movie':
      actions.push({ label: resumable ? t('Resume') : t(Words.play), run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: t(Words.startOver), run: go(`/play/${item.id}?t=0`) })
      actions.push('divider')
      actions.push({ label: item.userData.watched ? t(Words.markAsUnwatched) : t(Words.markAsWatched), run: mark(item, !item.userData.watched), marks: true })
      if (started) actions.push({ label: t(Words.markAsUnwatched), run: mark(item, false), marks: true })
      actions.push(...watchlistAction())
      if (!kids) actions.push({ label: t('Add to collection…'), run: () => openAddToCollection(item) })
      break
    case 'Episode': {
      const code = episodeCode(item)
      const label = code ? (resumable ? t('Resume {code}', { code }) : t('Play {code}', { code })) : resumable ? t('Resume') : t(Words.play)
      actions.push({ label, run: go(`/play/${item.id}`) })
      if (resumable) actions.push({ label: t(Words.startOver), run: go(`/play/${item.id}?t=0`) })
      actions.push('divider', { label: item.userData.watched ? t('Mark episode as unwatched') : t('Mark episode as watched'), run: mark(item, !item.userData.watched), marks: true })
      if (started) actions.push({ label: t('Mark episode as unwatched'), run: mark(item, false), marks: true })
      else if (continueWatching && !item.userData.watched) actions.push({ label: t('Remove'), run: remove })
      if (showId)
        actions.push({
          label: item.seriesTitle ? t('Mark all of {show} as watched', { show: item.seriesTitle }) : t('Mark all of the show as watched'),
          run: mark({ id: showId, parentId: null, seriesId: null }, true),
          marks: true,
        })
      break
    }
    case 'Season':
      actions.push({ label: t(Words.play), run: () => (showId ? play({ ...item, id: showId, kind: 'Series' }) : navigate(`/item/${item.id}`)) })
      actions.push('divider', { label: item.userData.watched ? t('Mark season as unwatched') : t(Words.markSeasonAsWatched), run: mark(item, !item.userData.watched), marks: true })
      break
    case 'Series':
      actions.push({ label: t('Play next episode'), run: () => play(item) }, 'divider')
      actions.push({ label: item.userData.watched ? t('Mark show as unwatched') : t('Mark show as watched'), run: mark(item, !item.userData.watched), marks: true })
      actions.push(...watchlistAction())
      if (!kids) actions.push({ label: t('Add to collection…'), run: () => openAddToCollection(item) })
      break
    case 'Album':
      // The album's tracks are fetched when asked for (the card only has the album).
      actions.push(
        { label: t(Words.play), run: async () => music.play(await getTracks(item.id), 0, { openNowPlaying: true }) },
        { label: t(Words.playNext), run: async () => music.playNext(await getTracks(item.id)) },
        { label: t(Words.addToQueue), run: async () => music.enqueue(await getTracks(item.id)) },
        { label: t('Add to playlist…'), run: () => openAddToPlaylist(item) },
      )
      break
    case 'Audiobook': {
      const finished = item.userData.watched
      actions.push({ label: resumable && !finished ? t('Resume') : t(Words.play), run: () => music.play([item]) }, 'divider')
      const markBook = (to: boolean) => async () => {
        await setBookFinished(item.id, to)
        announceWatched(item)
      }
      if (!finished) actions.push({ label: t(Words.markAsFinished), run: markBook(true), marks: true })
      // Unread: no place and not finished, as if never started. Not while it plays, which would set the place again: then
      // Remove (which stops it first) takes it off Continue listening instead, so the two are never offered together.
      const playing = music.current?.id === item.id
      if ((finished || started) && !playing) actions.push({ label: t(Words.markAsUnread), run: markBook(false), marks: true })
      else if (continueWatching && started) actions.push({ label: t('Remove'), run: remove })
      if (!kids) actions.push({ label: t('Add to collection…'), run: () => openAddToCollection(item) })
      break
    }
    case 'PodcastEpisode':
      actions.push({ label: started ? t('Resume') : t(Words.play), run: () => music.play([item]) })
      // Played takes it out of the row as well; Remove forgets the place without marking it.
      if (continueWatching) actions.push('divider', { label: t(Words.markAsPlayed), run: mark(item, true), marks: true }, { label: t('Remove'), run: remove })
      break
  }

  return <Menu label={item.title} at={at} actions={kids ? withoutWatched(actions) : actions} onClose={onClose} />
}

/** A child's menus leave watch state alone: what's watched is what they watched. */
function withoutWatched(actions: MenuAction[]) {
  const kept = actions.filter((a) => a === 'divider' || !a.marks)
  // No divider left at the end, or two together.
  return kept.filter((a, i) => a !== 'divider' || (i < kept.length - 1 && kept[i + 1] !== 'divider'))
}

const ITEM = '[role^="menuitem"]'

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
    const first = el.querySelector<HTMLButtonElement>('[aria-checked="true"]') ?? el.querySelector<HTMLButtonElement>(ITEM)
    first?.focus()
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
    const items = [...(menu.current?.querySelectorAll<HTMLButtonElement>(ITEM) ?? [])]
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
            role={a.checked === undefined ? 'menuitem' : 'menuitemradio'}
            aria-checked={a.checked}
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
