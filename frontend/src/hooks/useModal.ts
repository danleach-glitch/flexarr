import { useEffect, useRef, useState } from 'react'

const FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), iframe, [tabindex]:not([tabindex="-1"])'

/**
 * What a dialog over the page needs for the keyboard: Escape closes it (while `canClose`), Tab and Shift-Tab stay inside
 * it, the focus starts inside it (on whatever has autoFocus, else its first control), and goes back to whatever opened
 * it when it closes. Put the returned ref on the dialog itself.
 */
export function useModal<T extends HTMLElement>(onClose: () => void, canClose = true) {
  const dialog = useRef<T>(null)
  const close = useRef(onClose)
  const closable = useRef(canClose)
  // Whatever had the focus when it opened: read while rendering, before anything inside takes it.
  const [opener] = useState(() => (document.activeElement instanceof HTMLElement ? document.activeElement : null))
  useEffect(() => {
    close.current = onClose
    closable.current = canClose
  })

  useEffect(() => {
    if (!dialog.current?.contains(document.activeElement)) dialog.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus()
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.stopPropagation()
        if (closable.current) close.current()
        return
      }
      if (dialog.current) trapTab(dialog.current, e)
    }
    document.addEventListener('keydown', onKey, true)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      if (opener?.isConnected) opener.focus()
    }
  }, [opener])

  return dialog
}

/**
 * Keeps Tab and Shift-Tab inside `container`: from its last control Tab goes to its first, from its first Shift-Tab to
 * its last, and from outside it either one comes in. For a view that handles its own Escape (Now Playing, whose menus
 * take Escape first) and so can't use `useModal`.
 */
export function trapTab(container: HTMLElement, e: KeyboardEvent) {
  if (e.key !== 'Tab') return
  const focusable = [...container.querySelectorAll<HTMLElement>(FOCUSABLE)]
  if (focusable.length === 0) return
  const first = focusable[0]
  const last = focusable[focusable.length - 1]
  const inside = container.contains(document.activeElement)
  if (e.shiftKey && (document.activeElement === first || !inside)) {
    e.preventDefault()
    last.focus()
  } else if (!e.shiftKey && (document.activeElement === last || !inside)) {
    e.preventDefault()
    first.focus()
  }
}
