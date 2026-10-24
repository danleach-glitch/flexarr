import { useEffect, useId, useRef, useState, type ReactNode } from 'react'

/**
 * Asks before doing something that can't easily be taken back. Escape and the backdrop close it (unless it's busy),
 * Tab stays inside it, and focus goes back to whatever opened it. The safe choice is focused first.
 */
export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  cancelLabel = 'Cancel',
  danger = false,
  busy = false,
  error,
  onConfirm,
  onClose,
}: {
  title: string
  children?: ReactNode
  confirmLabel: string
  cancelLabel?: string
  danger?: boolean
  busy?: boolean
  error?: string
  onConfirm: () => void
  onClose: () => void
}) {
  const id = useId()
  const dialog = useRef<HTMLDivElement>(null)
  const close = useRef(onClose)
  const busyNow = useRef(busy)
  // Whatever had the focus when it opened: read while rendering, before the safe choice takes the focus.
  const [opener] = useState(() => (document.activeElement instanceof HTMLElement ? document.activeElement : null))
  useEffect(() => {
    close.current = onClose
    busyNow.current = busy
  })

  useEffect(() => {
    if (!dialog.current?.contains(document.activeElement)) dialog.current?.querySelector<HTMLElement>('button:not([disabled])')?.focus()
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.stopPropagation()
        if (!busyNow.current) close.current()
        return
      }
      if (e.key !== 'Tab' || !dialog.current) return
      const focusable = [...dialog.current.querySelectorAll<HTMLElement>('button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])')]
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (e.shiftKey && (document.activeElement === first || !dialog.current.contains(document.activeElement))) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && (document.activeElement === last || !dialog.current.contains(document.activeElement))) {
        e.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKey, true)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      if (opener?.isConnected) opener.focus()
    }
  }, [opener])

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div
        ref={dialog}
        className="panel confirm-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        aria-describedby={children ? `${id}-detail` : undefined}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id={`${id}-title`}>{title}</h2>
        {children && <div id={`${id}-detail`} className="confirm-dialog-detail">{children}</div>}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose} disabled={busy} autoFocus>
            {cancelLabel}
          </button>
          <button className={danger ? 'button danger' : 'button'} onClick={onConfirm} disabled={busy}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
