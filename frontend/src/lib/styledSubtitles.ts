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

/** `task` for each of `items`, at most `limit` at a time, answers in the items' order. One failing fails the lot, and nothing more is started. */
async function inTurn<T, R>(items: T[], limit: number, task: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length)
  let next = 0
  let failed = false
  const lane = async () => {
    while (next < items.length && !failed) {
      const i = next++
      try {
        results[i] = await task(items[i])
      } catch (e) {
        failed = true
        throw e
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, lane))
  return results
}

/**
 * Draws ASS/SSA subtitles over `video` with libass (JASSUB, loaded only when needed): their fonts, colours, positions,
 * karaoke and effects. The file and the fonts attached to the video are fetched here, with the profile's headers, and
 * handed over, so the renderer's worker never needs to be signed in. Fonts aren't looked for on this computer, which
 * would ask the viewer for permission; a style whose font isn't attached falls back to JASSUB's own.
 */
export async function drawStyled(video: HTMLVideoElement, url: string, fontsUrl: string | null | undefined, delay: number, signal: AbortSignal): Promise<StyledSubtitles> {
  const [{ default: Renderer }, content, fonts] = await Promise.all([
    import('jassub'),
    fetchOk(url, signal).then((r) => r.text()),
    fontsUrl
      ? fetchOk(fontsUrl, signal)
          .then((r) => r.json() as Promise<SubtitleFont[]>)
          .then((list) => inTurn(list, FONTS_AT_ONCE, (f) => fetchOk(f.url, signal).then(async (r) => new Uint8Array(await r.arrayBuffer()))))
          // Without its fonts the subtitles still show, in a plainer face.
          .catch((e: unknown) => (signal.aborted ? Promise.reject(e) : []))
      : Promise.resolve([]),
  ])
  signal.throwIfAborted()
  const renderer: JASSUB = new Renderer({ video, subContent: content, fonts, queryFonts: false, timeOffset: -delay })
  try {
    await renderer.ready
    signal.throwIfAborted()
  } catch (e) {
    void renderer.destroy()
    throw e
  }
  return {
    set delay(seconds: number) {
      renderer.timeOffset = -seconds
    },
    destroy: () => void renderer.destroy(),
  }
}
