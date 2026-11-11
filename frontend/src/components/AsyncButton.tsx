import { useEffect, useRef, useState, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from 'react'
import { noticeFailure } from '../lib/notice'

type Props = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onClick'> & {
  /** Return a promise and the button spins, and won't take another press, until it settles. */
  onClick?: (event: MouseEvent<HTMLButtonElement>) => unknown
  /** Spin for work the button didn't start itself (a scan under way, a form submitting). */
  busy?: boolean
  /** For a wait of minutes: while busy it stays focusable and only refuses presses, so the keyboard keeps its place. */
  keepFocus?: boolean
  /** The action says for itself when it fails (a line by the button, say), so no notice. */
  silentFailure?: boolean
}

/**
 * A button whose action takes a moment: while it's under way a spinner takes the label's place (styles in base.css,
 * keyed on aria-busy), so a press is never met with nothing. An action that fails says so in a notice, unless it says so
 * itself (`silentFailure`).
 */
export function AsyncButton({ onClick, busy, keepFocus, silentFailure, disabled, type = 'button', ...rest }: Props) {
  const [pending, setPending] = useState(false)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])

  const spinning = busy || pending
  return (
    <button
      {...rest}
      type={type}
      aria-busy={spinning || undefined}
      aria-disabled={(keepFocus && spinning) || undefined}
      disabled={disabled || (spinning && !keepFocus)}
      onClick={async (event) => {
        if (spinning) return
        let result: unknown
        try {
          result = onClick?.(event)
        } catch (e) {
          if (!silentFailure) noticeFailure(e)
          return
        }
        if (!(result instanceof Promise)) return
        setPending(true)
        try {
          await result
        } catch (e) {
          if (!silentFailure) noticeFailure(e)
        } finally {
          if (mounted.current) setPending(false)
        }
      }}
    />
  )
}

/** A small square button showing only an icon, named by `label` for screen readers and in a tip on hover. */
export function IconButton({ label, icon, className = 'button quiet small icon', ...rest }: Omit<Props, 'children'> & { label: string; icon: ReactNode }) {
  return (
    <AsyncButton {...rest} className={className} aria-label={rest['aria-label'] ?? label} data-tip={label}>
      {icon}
    </AsyncButton>
  )
}
