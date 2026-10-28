import { useId, useState, type ReactNode } from 'react'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'

/**
 * Asks before doing something that can't easily be taken back. Escape and the backdrop close it (unless it's busy, or
 * the promise `onConfirm` returned is still running), Tab stays inside it, and focus goes back to whatever opened it.
 * The safe choice is focused first.
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
  onConfirm: () => unknown
  onClose: () => void
}) {
  const id = useId()
  const [pending, setPending] = useState(false)
  const working = busy || pending
  // The safe choice is the first button, so it takes the focus.
  const dialog = useModal<HTMLDivElement>(onClose, !working)

  function confirm() {
    const result = onConfirm()
    if (!(result instanceof Promise)) return
    setPending(true)
    return result.finally(() => setPending(false))
  }

  return (
    <div className="dialog-backdrop" onClick={() => !working && onClose()}>
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
          <button className="button quiet" onClick={onClose} disabled={working} autoFocus>
            {cancelLabel}
          </button>
          <AsyncButton className={danger ? 'button danger' : 'button'} onClick={confirm} busy={working}>
            {confirmLabel}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}
