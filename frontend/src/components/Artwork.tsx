import { useState, type CSSProperties } from 'react'
import { imageSrcSet, imageUrl } from '../api/client'

/**
 * Artwork with a typographic fallback: when there's no image (or it fails), the title is set in the
 * marquee face on a tint of the title's ambient colour, so a missing poster still looks deliberate.
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
  const [failed, setFailed] = useState(false)
  const url = imageUrl(src, width)

  return (
    <div className={`artwork artwork-${shape}`} style={accent ? { '--ambient': accent } as CSSProperties : undefined}>
      {url && !failed ? (
        <img src={url} srcSet={imageSrcSet(src, width)} alt="" loading={eager ? 'eager' : 'lazy'} decoding="async" onError={() => setFailed(true)} />
      ) : (
        <span className="artwork-fallback" aria-hidden="true">
          {title}
        </span>
      )}
    </div>
  )
}
