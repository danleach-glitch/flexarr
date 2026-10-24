import { useEffect, useState, type CSSProperties } from 'react'
import { imageSrcSet, imageUrl } from '../api/client'

/**
 * Artwork with a typographic fallback: when there's no image (or it fails), the title is set in the
 * marquee face on a tint of the title's ambient colour, so a missing poster still looks deliberate. A failed image is
 * tried once more after a few seconds: the server turns new artwork away while it's busy fetching lots at once.
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
          onError={() => setState(attempt === 0 ? 'retrying' : 'failed')}
        />
      ) : (
        <span className="artwork-fallback" aria-hidden="true">
          {title}
        </span>
      )}
    </div>
  )
}
