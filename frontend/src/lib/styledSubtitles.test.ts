import { afterEach, describe, expect, it, vi } from 'vitest'
import { drawStyled, FONTS_AT_ONCE, fontsFor } from './styledSubtitles'

const made: { subContent: string; fonts: Uint8Array[]; timeOffset: number }[] = []
vi.mock('jassub', () => ({
  default: class {
    ready = Promise.resolve()
    timeOffset = 0
    constructor(options: { subContent: string; fonts: Uint8Array[]; timeOffset: number }) {
      made.push(options)
    }
    destroy() {
      return Promise.resolve()
    }
  },
}))

afterEach(() => {
  vi.unstubAllGlobals()
  made.length = 0
})

const fontList = (count: number) => Array.from({ length: count }, (_, i) => ({ name: `font-${i}.ttf`, url: `/fonts/${i}` }))

/** Answers the subtitle file, the list of fonts and each font (one byte, its number), a turn of the clock later. */
function server(fonts: { name: string; url: string }[], broken?: string) {
  const state = { inFlight: 0, most: 0, asked: [] as string[] }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url === '/subs.ass') return new Response('[Script Info]')
      if (url === '/fonts') return new Response(JSON.stringify(fonts))
      state.asked.push(url)
      state.inFlight += 1
      state.most = Math.max(state.most, state.inFlight)
      await new Promise((resolve) => setTimeout(resolve, 5))
      state.inFlight -= 1
      return url === broken ? new Response('', { status: 500 }) : new Response(new Uint8Array([Number(url.split('/').pop())]))
    }),
  )
  return state
}

describe('styled subtitles', () => {
  it('fetches the attached fonts a few at a time, and hands them all over in the order they were listed', async () => {
    const fonts = fontList(10)
    const state = server(fonts)

    const drawn = await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 1.5, new AbortController().signal)

    expect(state.asked).toHaveLength(10)
    expect(state.most).toBe(FONTS_AT_ONCE)
    expect(made).toHaveLength(1)
    expect(made[0].subContent).toBe('[Script Info]')
    expect(made[0].timeOffset).toBe(-1.5)
    expect(made[0].fonts.map((f) => f[0])).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])
    drawn.destroy()
  })

  it('draws without fonts when one can’t be fetched, asking for no more of them', async () => {
    const state = server(fontList(10), '/fonts/1')

    await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)

    expect(made[0].fonts).toEqual([])
    // Those already on their way (and at most one each started as they landed), not all ten.
    expect(state.asked.length).toBeLessThanOrEqual(FONTS_AT_ONCE * 2)
  })

  it('draws a file with no fonts attached, or none listed', async () => {
    const state = server([])
    await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await drawStyled(document.createElement('video'), '/subs.ass', null, 0, new AbortController().signal)

    expect(made.map((m) => m.fonts)).toEqual([[], []])
    expect(state.asked).toEqual([])
  })

  it('asks for only the fonts the chosen track names', () => {
    expect(fontsFor('/api/v1/playback/s1/fonts.json', 'e3')).toBe('/api/v1/playback/s1/fonts.json?subtitle=e3')
    expect(fontsFor('/fonts.json?v=2', 'a b')).toBe('/fonts.json?v=2&subtitle=a%20b')
    expect(fontsFor(null, 'e3')).toBeNull()
    expect(fontsFor(undefined, 'e3')).toBeNull()
  })
})
