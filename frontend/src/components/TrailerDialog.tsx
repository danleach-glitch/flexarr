import { useEffect } from 'react'

/** A YouTube trailer over the page (privacy-enhanced embed); Escape or a click outside closes it. */
export function TrailerDialog({ videoId, title, onClose }: { videoId: string; title: string; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="trailer" role="dialog" aria-modal="true" aria-label={`${title} trailer`} onClick={(e) => e.stopPropagation()}>
        <iframe
          src={`https://www.youtube-nocookie.com/embed/${videoId}?autoplay=1&rel=0`}
          title={`${title} trailer`}
          allow="autoplay; encrypted-media; picture-in-picture; fullscreen"
          allowFullScreen
        />
        <button className="button quiet small trailer-close" onClick={onClose} autoFocus>
          Close
        </button>
      </div>
    </div>
  )
}
