/**
 * The colours of a cover, for the visualiser: its few strongest hues, each lifted to a level that glows on a dark room.
 * A grey or black-and-white cover has none to speak of, so the track's accent colour stands in.
 */

export type Rgb = [number, number, number]

const HUE_BINS = 12

function toHsl([r, g, b]: Rgb): [number, number, number] {
  const rn = r / 255
  const gn = g / 255
  const bn = b / 255
  const max = Math.max(rn, gn, bn)
  const min = Math.min(rn, gn, bn)
  const l = (max + min) / 2
  if (max === min) return [0, 0, l]
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  const h = max === rn ? (gn - bn) / d + (gn < bn ? 6 : 0) : max === gn ? (bn - rn) / d + 2 : (rn - gn) / d + 4
  return [h * 60, s, l]
}

function fromHsl(h: number, s: number, l: number): Rgb {
  const k = (n: number) => (n + h / 30) % 12
  const a = s * Math.min(l, 1 - l)
  const f = (n: number) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)))
  return [Math.round(f(0) * 255), Math.round(f(8) * 255), Math.round(f(4) * 255)]
}

/** Brings a colour into the range that reads as light in a dark room: not muddy, not white. */
export function glow(rgb: Rgb): Rgb {
  const [h, s, l] = toHsl(rgb)
  return fromHsl(h, Math.max(0.5, Math.min(0.9, s)), Math.max(0.42, Math.min(0.66, l)))
}

/**
 * The cover's main colours from its pixels (RGBA, as a canvas gives them): pixels are grouped by hue, each group
 * weighed by how much of the cover it covers and how vivid it is, and the strongest `count` groups are averaged.
 */
export function paletteFrom(pixels: Uint8ClampedArray, count = 4): Rgb[] {
  const bins = Array.from({ length: HUE_BINS }, () => ({ weight: 0, r: 0, g: 0, b: 0 }))
  for (let i = 0; i + 3 < pixels.length; i += 4) {
    if (pixels[i + 3] < 128) continue
    const rgb: Rgb = [pixels[i], pixels[i + 1], pixels[i + 2]]
    const [h, s, l] = toHsl(rgb)
    // Near-black, near-white and grey say nothing about the cover's colour.
    if (s < 0.18 || l < 0.08 || l > 0.94) continue
    const weight = s * (1 - Math.abs(l - 0.5))
    const bin = bins[Math.floor(h / (360 / HUE_BINS)) % HUE_BINS]
    bin.weight += weight
    bin.r += rgb[0] * weight
    bin.g += rgb[1] * weight
    bin.b += rgb[2] * weight
  }
  const total = bins.reduce((sum, b) => sum + b.weight, 0)
  return bins
    .filter((b) => total > 0 && b.weight / total > 0.04)
    .sort((a, b) => b.weight - a.weight)
    .slice(0, count)
    .map((b) => glow([b.r / b.weight, b.g / b.weight, b.b / b.weight]))
}

export function parseHex(hex: string | null | undefined): Rgb | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex ?? '')
  if (!m) return null
  const n = parseInt(m[1], 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

/** Fills a short palette out to `count` with neighbours of its colours, so the light always has a few hues to move. */
export function fillPalette(colours: Rgb[], count = 4): Rgb[] {
  const base = colours.length > 0 ? colours : [[150, 120, 210] as Rgb]
  const out = [...base]
  for (let i = 0; out.length < count; i++) {
    const [h, s, l] = toHsl(base[i % base.length])
    const turn = (i % 2 === 0 ? 1 : -1) * (28 + 14 * Math.floor(i / 2))
    out.push(glow(fromHsl((h + turn + 360) % 360, s, l)))
  }
  return out
}

const cache = new Map<string, Promise<Rgb[]>>()

/** A cover's palette, read once per image from a small copy of it. Empty when the image can't be read. */
export function coverPalette(url: string): Promise<Rgb[]> {
  let found = cache.get(url)
  if (!found) {
    found = new Promise<Rgb[]>((resolve) => {
      const img = new Image()
      img.crossOrigin = 'anonymous'
      img.onload = () => {
        try {
          const size = 32
          const canvas = document.createElement('canvas')
          canvas.width = size
          canvas.height = size
          const ctx = canvas.getContext('2d', { willReadFrequently: true })
          if (!ctx) return resolve([])
          ctx.drawImage(img, 0, 0, size, size)
          resolve(paletteFrom(ctx.getImageData(0, 0, size, size).data))
        } catch {
          resolve([]) // A tainted canvas: the accent colour will do.
        }
      }
      img.onerror = () => resolve([])
      img.src = url
    })
    cache.set(url, found)
  }
  return found
}
