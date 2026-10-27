import { useEffect, useRef, useState, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from 'react'

type Props = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onClick'> & {
  /** Return a promise and the button spins, and won't take another press, until it settles. */
  onClick?: (event: MouseEvent<HTMLButtonElement>) => unknown
  /** Spin for work the button didn't start itself (a scan under way, a form submitting). */
  busy?: boolean
}

/**
 * A button whose action takes a moment: while it's under way a spinner takes the label's place (styles in base.css,
 * keyed on aria-busy), so a press is never met with nothing.
 */
export function AsyncButton({ onClick, busy, disabled, type = 'button', ...rest }: Props) {
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
      disabled={disabled || spinning}
      onClick={async (event) => {
        const result = onClick?.(event)
        if (!(result instanceof Promise)) return
        setPending(true)
        try {
          await result
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
