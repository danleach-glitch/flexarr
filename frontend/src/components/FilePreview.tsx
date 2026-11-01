import { useEffect, useRef } from 'react'

/**
 * A picked image file, drawn onto a canvas (cropped to fill it) rather than put in an `<img src>`: nothing the page
 * reads from a file input ever becomes an address.
 */
export function FilePreview({ file, width = 300, height = 450 }: { file: File; width?: number; height?: number }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    let cancelled = false
    let bitmap: ImageBitmap | undefined
    if (typeof createImageBitmap !== 'function') return
    createImageBitmap(file)
      .then((b) => {
        bitmap = b
        const context = canvas.current?.getContext('2d')
        if (cancelled || !context) return
        const scale = Math.max(width / b.width, height / b.height)
        const w = b.width * scale
        const h = b.height * scale
        context.clearRect(0, 0, width, height)
        context.drawImage(b, (width - w) / 2, (height - h) / 2, w, h)
      })
      .catch(() => {})
    return () => {
      cancelled = true
      bitmap?.close()
    }
  }, [file, width, height])
  return <canvas ref={canvas} width={width} height={height} aria-hidden="true" />
}
