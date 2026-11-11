import { useLayoutEffect, useRef } from 'react'

/**
 * For a row that scrolls sideways on a phone (a library's views): marks which edges have more beyond them
 * (`data-more-start`, `data-more-end`, which the CSS fades), and brings the chosen item (`[aria-current]`) into view
 * whenever `chosen` changes. Put the returned ref on the row.
 */
export function useScrollHints<T extends HTMLElement>(chosen: unknown) {
  const row = useRef<T>(null)

  useLayoutEffect(() => {
    const el = row.current
    if (!el) return
    const mark = () => {
      const more = el.scrollWidth - el.clientWidth
      el.toggleAttribute('data-more-start', more > 1 && el.scrollLeft > 1)
      el.toggleAttribute('data-more-end', more > 1 && el.scrollLeft < more - 1)
    }
    mark()
    el.addEventListener('scroll', mark, { passive: true })
    const resized = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(mark)
    resized?.observe(el)
    return () => {
      el.removeEventListener('scroll', mark)
      resized?.disconnect()
    }
  }, [])

  useLayoutEffect(() => {
    const el = row.current
    const current = el?.querySelector<HTMLElement>('[aria-current]')
    if (!el || !current || el.scrollWidth <= el.clientWidth) return
    // Sideways only (scrollIntoView could move the page too): the chosen one in the middle where it can be.
    const box = el.getBoundingClientRect()
    const item = current.getBoundingClientRect()
    const to = el.scrollLeft + item.left - box.left - (box.width - item.width) / 2
    el.scrollLeft = Math.max(0, Math.min(el.scrollWidth - el.clientWidth, to))
  }, [chosen])

  return row
}
