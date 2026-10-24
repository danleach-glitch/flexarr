import { useEffect } from 'react'

// A YouTube video id: anything else (from metadata we don't control) never reaches the embed's address.
const YOUTUBE_ID = /^[A-Za-z0-9_-]{11}$/

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
        {YOUTUBE_ID.test(videoId) ? (
          <iframe
            src={`https://www.youtube-nocookie.com/embed/${videoId}?autoplay=1&rel=0`}
            title={`${title} trailer`}
            allow="autoplay; encrypted-media; picture-in-picture; fullscreen"
            // YouTube refuses embeds that don't say which site they're on (error 153). The app otherwise sends no
            // referrer off-site, so this iframe alone sends Flexarr's origin, never a page path.
            referrerPolicy="strict-origin-when-cross-origin"
            allowFullScreen
          />
        ) : (
          <p className="page-message muted">This trailer can’t be played.</p>
        )}
        <button className="button quiet small trailer-close" onClick={onClose} autoFocus>
          Close
        </button>
      </div>
    </div>
  )
}
