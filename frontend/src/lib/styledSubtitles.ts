import type JASSUB from 'jassub'
import { authHeaders, type SubtitleFont } from '../api/client'

/** Styled subtitles drawn over a video as authored; move them with `delay` (seconds later), and stop with `destroy`. */
export interface StyledSubtitles {
  set delay(seconds: number)
  destroy(): void
}

async function fetchOk(url: string, signal: AbortSignal): Promise<Response> {
  const response = await fetch(url, { headers: authHeaders(), signal })
  if (!response.ok) throw new Error(`${url} answered ${response.status}`)
  return response
}

/**
 * How many attached fonts are fetched at once. A release can attach dozens (tens of MB); asked for all together they
 * take every connection the browser allows a server, and the video's own requests wait behind them.
 */
export const FONTS_AT_ONCE = 3

/**
 * `task` for each of `items`, at most `limit` at a time, each answer handed to `each` as it lands. One failing stops
 * anything more being started, and rejects once those already on their way have finished.
 */
async function inTurn<T, R>(items: T[], limit: number, task: (item: T) => Promise<R>, each: (result: R) => void): Promise<void> {
  let next = 0
  let failed = false
  const lane = async () => {
    while (next < items.length && !failed) {
      const item = items[next++]
      try {
        each(await task(item))
      } catch (e) {
        failed = true
        throw e
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, lane))
}

/**
 * The address of the fonts `subtitle` (a styled track's key) is drawn with: the server sends only the fonts its script
 * names, rather than every font attached to the file. Servers before the release after 0.62.1 ignore `subtitle` and
 * send them all, which still works.
 */
export function fontsFor(fontsUrl: string | null | undefined, subtitle: string): string | null {
  if (!fontsUrl) return null
  return `${fontsUrl}${fontsUrl.includes('?') ? '&' : '?'}subtitle=${encodeURIComponent(subtitle)}`
}

/**
 * Draws ASS/SSA subtitles over `video` with libass (JASSUB, loaded only when needed): their fonts, colours, positions,
 * karaoke and effects. The file and the fonts attached to the video are fetched here, with the profile's headers, and
 * handed over, so the renderer's worker never needs to be signed in. Fonts aren't looked for on this computer, which
 * would ask the viewer for permission; a style whose font isn't attached falls back to JASSUB's own.
 *
 * Drawing starts as soon as the file is here: the fonts follow as each arrives (a release can attach dozens, tens of
 * MB), and a line takes its own face once its font has come.
 */
export async function drawStyled(video: HTMLVideoElement, url: string, fontsUrl: string | null | undefined, delay: number, signal: AbortSignal): Promise<StyledSubtitles> {
  const [{ default: Renderer }, content] = await Promise.all([import('jassub'), fetchOk(url, signal).then((r) => r.text())])
  signal.throwIfAborted()
  const renderer: JASSUB = new Renderer({ video, subContent: content, fonts: [], queryFonts: false, timeOffset: -delay })
  try {
    await renderer.ready
    signal.throwIfAborted()
  } catch (e) {
    void renderer.destroy()
    throw e
  }
  // Stopped (a new track, the player closed): no more fonts are fetched or handed over.
  const stop = new AbortController()
  signal.addEventListener('abort', () => stop.abort(), { once: true })
  if (fontsUrl) void addFonts(renderer, fontsUrl, stop.signal)
  return {
    set delay(seconds: number) {
      renderer.timeOffset = -seconds
    },
    destroy: () => {
      stop.abort()
      void renderer.destroy()
    },
  }
}

/**
 * Fetches the fonts listed at `fontsUrl` a few at a time and hands them to `renderer` as they land: those that arrive
 * while it's taking the last ones go over together, since each handover makes libass look through all its fonts again.
 * A paused picture is drawn again with them. Without its fonts (the list or one of them can't be fetched) the
 * subtitles go on showing, in the faces they have.
 */
async function addFonts(renderer: JASSUB, fontsUrl: string, signal: AbortSignal) {
  let waiting: Uint8Array[] = []
  let handing: Promise<void> | null = null
  const handOver = () => {
    if (handing || !waiting.length || signal.aborted) return
    const fonts = waiting
    waiting = []
    handing = renderer.renderer
      .addFonts(fonts)
      .then(() => {
        if (!signal.aborted) void renderer.resize().catch(() => undefined)
      })
      .catch(() => undefined)
      .finally(() => {
        handing = null
        handOver()
      })
  }
  try {
    const list = (await fetchOk(fontsUrl, signal).then((r) => r.json())) as SubtitleFont[]
    const fetchFont = async (f: SubtitleFont) => {
      signal.throwIfAborted()
      return new Uint8Array(await (await fetchOk(f.url, signal)).arrayBuffer())
    }
    await inTurn(list, FONTS_AT_ONCE, fetchFont, (font) => {
      signal.throwIfAborted()
      waiting.push(font)
      handOver()
    })
  } catch {
    // Those that came are kept; the rest are done without.
  }
}
