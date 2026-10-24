import { useEffect, useState, type CSSProperties } from 'react'
import { imageSrcSet, imageUrl } from '../api/client'

// A poster grid loads far more than the browser's 250 default timings; keep room for the newest so a failed image's
// timing is there to read.
if (typeof performance !== 'undefined' && typeof performance.addEventListener === 'function') {
  performance.addEventListener('resourcetimingbufferfull', () => performance.clearResourceTimings())
}

/**
 * Whether a failed image is worth asking for again. An <img> can't see the status, so this reads the browser's timing
 * for it: where it reports the status (Chrome, Firefox), only "busy" (503) is retried; elsewhere (Safari) only a quick
 * failure, as the server turns busy requests away at once while a slow failure means it tried and the poster is gone.
 * With no timing to go on, it's retried: the server answers a poster it knows is gone straight away.
 */
function worthRetrying(image: HTMLImageElement) {
  const timing = performance.getEntriesByName?.(image.currentSrc || image.src, 'resource').at(-1) as PerformanceResourceTiming | undefined
  if (!timing) return true
  if (timing.responseStatus) return timing.responseStatus === 503
  return timing.duration < 1000
}

/**
 * Artwork with a typographic fallback: when there's no image (or it fails), the title is set in the
 * marquee face on a tint of the title's ambient colour, so a missing poster still looks deliberate. A failed image is
 * tried once more after a few seconds when it looks like the server was busy (it turns new artwork away while it's
 * fetching lots at once), not when the poster is gone.
 */
export function Artwork({
  src,
  title,
  width,
  shape = 'poster',
  accent,
  eager = false,
}: {
  src: string | null | undefined
  title: string
  width: number
  shape?: 'poster' | 'wide' | 'square' | 'portrait'
  accent?: string | null
  eager?: boolean
}) {
  const [state, setState] = useState<'showing' | 'retrying' | 'failed'>('showing')
  const [attempt, setAttempt] = useState(0)
  const url = imageUrl(src, width)

  useEffect(() => {
    if (state !== 'retrying') return
    const timer = setTimeout(() => {
      setAttempt((a) => a + 1)
      setState('showing')
    }, 5000)
    return () => clearTimeout(timer)
  }, [state])

  return (
    <div className={`artwork artwork-${shape}`} style={accent ? { '--ambient': accent } as CSSProperties : undefined}>
      {url && state === 'showing' ? (
        <img
          key={attempt}
          src={url}
          srcSet={imageSrcSet(src, width)}
          alt=""
          loading={eager ? 'eager' : 'lazy'}
          decoding="async"
          onError={(e) => setState(attempt === 0 && worthRetrying(e.currentTarget) ? 'retrying' : 'failed')}
        />
      ) : (
        <span className="artwork-fallback" aria-hidden="true">
          {title}
        </span>
      )}
    </div>
  )
}
