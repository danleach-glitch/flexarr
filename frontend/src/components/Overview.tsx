import { useLayoutEffect, useRef, useState } from 'react'

/** A title's description, cut to a few lines with More to read the rest, so a long one can't push everything else away. */
export function Overview({ text }: { text: string }) {
  const ref = useRef<HTMLParagraphElement>(null)
  // Open for this text only: a different title's description starts short again.
  const [openFor, setOpenFor] = useState<string | null>(null)
  const open = openFor === text
  const [cut, setCut] = useState(false)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const measure = () => setCut(el.scrollHeight > el.clientHeight + 1)
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [text])

  return (
    <div className="detail-overview-block">
      <p ref={ref} className={open ? 'detail-overview' : 'detail-overview clamped'}>
        {text}
      </p>
      {(cut || open) && (
        <button type="button" className="overview-toggle" aria-expanded={open} onClick={() => setOpenFor(open ? null : text)}>
          {open ? 'Less' : 'More'}
        </button>
      )}
    </div>
  )
}
