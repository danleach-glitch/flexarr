import type { KeyboardEvent } from 'react'

/** A menu's items: what Up and Down move between. */
export const MENU_ITEM = '[role^="menuitem"]'

/** Into a menu just opened: its chosen item (the subtitles on now, say), or else its first. */
export function focusMenu(menu: HTMLElement | null | undefined, items: string = MENU_ITEM) {
  if (!menu) return
  const chosen = menu.querySelector<HTMLElement>(`[aria-checked="true"]:not(:disabled)`)
  ;(chosen ?? menu.querySelector<HTMLElement>(items))?.focus()
}

/**
 * Up and Down (and Home, End) through a menu's items, going round at the ends, as on every platform's menus and a TV
 * remote's arrows. Says whether it moved, so the caller can leave every other key alone.
 */
export function moveInMenu(e: KeyboardEvent | globalThis.KeyboardEvent, menu: HTMLElement | null | undefined, items: string = MENU_ITEM): boolean {
  if (!menu || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return false
  const all = [...menu.querySelectorAll<HTMLElement>(items)].filter((el) => !(el as HTMLButtonElement).disabled)
  if (all.length === 0) return false
  const at = all.indexOf(document.activeElement as HTMLElement)
  const next = e.key === 'Home' ? 0 : e.key === 'End' ? all.length - 1 : (at + (e.key === 'ArrowDown' ? 1 : at === -1 ? 0 : -1) + all.length) % all.length
  e.preventDefault()
  all[next]?.focus()
  return true
}

/**
 * A row of tabs (seasons, Live TV's views): Left and Right (and Home, End) move to the next tab and choose it, so only
 * the chosen tab is a Tab stop (give it `tabIndex={0}` and the rest `-1`). Right-to-left pages go the other way.
 */
export function tablistKeys(e: KeyboardEvent<HTMLElement>) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return
  const tabs = [...e.currentTarget.querySelectorAll<HTMLElement>('[role="tab"]')].filter((el) => !(el as HTMLButtonElement).disabled)
  if (tabs.length === 0) return
  const at = Math.max(0, tabs.indexOf(document.activeElement as HTMLElement))
  const forward = getComputedStyle(e.currentTarget).direction === 'rtl' ? 'ArrowLeft' : 'ArrowRight'
  const next = e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : (at + (e.key === forward ? 1 : -1) + tabs.length) % tabs.length
  e.preventDefault()
  tabs[next]?.focus()
  tabs[next]?.click()
}
