import { afterEach, describe, expect, it, vi } from 'vitest'
import { drawStyled, FONTS_AT_ONCE, fontsFor } from './styledSubtitles'

const made: { subContent: string; fonts: Uint8Array[]; timeOffset: number; added: Uint8Array[][]; repaints: number }[] = []
/** Holds each handover of fonts to the renderer's worker until let go, as a busy worker would. */
let holdHandovers = false
let heldHandovers: (() => void)[] = []
vi.mock('jassub', () => ({
  default: class {
    ready = Promise.resolve()
    timeOffset = 0
    options: (typeof made)[number]
    renderer = {
      addFonts: (fonts: Uint8Array[]) => {
        this.options.added.push(fonts)
        return holdHandovers ? new Promise<boolean>((resolve) => heldHandovers.push(() => resolve(true))) : Promise.resolve(true)
      },
    }
    constructor(options: { subContent: string; fonts: Uint8Array[]; timeOffset: number }) {
      this.options = { ...options, added: [], repaints: 0 }
      made.push(this.options)
    }
    resize() {
      this.options.repaints += 1
      return Promise.resolve()
    }
    destroy() {
      return Promise.resolve()
    }
  },
}))

afterEach(() => {
  vi.unstubAllGlobals()
  made.length = 0
  holdHandovers = false
  heldHandovers = []
})

const fontList = (count: number) => Array.from({ length: count }, (_, i) => ({ name: `font-${i}.ttf`, url: `/fonts/${i}` }))

/** Answers the subtitle file, the list of fonts and each font (one byte, its number), a turn of the clock later. */
function server(fonts: { name: string; url: string }[], broken?: string, gate?: Promise<void>) {
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
      await gate
      state.inFlight -= 1
      return url === broken ? new Response('', { status: 500 }) : new Response(new Uint8Array([Number(url.split('/').pop())]))
    }),
  )
  return state
}

/** All the fonts handed over so far, each its one byte. */
const handed = (i = 0) => made[i].added.flat().map((f) => f[0])
const settle = () => vi.waitFor(() => new Promise((resolve) => setTimeout(resolve, 20)))

describe('styled subtitles', () => {
  it('starts drawing before the fonts arrive, and hands each over as it comes', async () => {
    let letFontsIn = () => {}
    const gate = new Promise<void>((resolve) => (letFontsIn = resolve))
    const state = server(fontList(4), undefined, gate)

    const drawn = await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 1.5, new AbortController().signal)

    // Drawing has started with the text alone, while the fonts are still on their way.
    expect(made).toHaveLength(1)
    expect(made[0].subContent).toBe('[Script Info]')
    expect(made[0].timeOffset).toBe(-1.5)
    expect(made[0].fonts).toEqual([])
    await vi.waitFor(() => expect(state.asked.length).toBeGreaterThan(0))
    expect(made[0].added).toEqual([])

    letFontsIn()
    await vi.waitFor(() => expect(handed().sort()).toEqual([0, 1, 2, 3]))
    // A paused picture is drawn again with them.
    expect(made[0].repaints).toBeGreaterThan(0)
    drawn.destroy()
  })

  it('fetches the attached fonts a few at a time', async () => {
    const state = server(fontList(10))

    const drawn = await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await vi.waitFor(() => expect(handed()).toHaveLength(10))

    expect(state.asked).toHaveLength(10)
    expect(state.most).toBe(FONTS_AT_ONCE)
    expect(handed().sort((a, b) => a - b)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])
    drawn.destroy()
  })

  it('hands over together the fonts that land while the worker is taking the last ones', async () => {
    holdHandovers = true
    server(fontList(7))

    const drawn = await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await vi.waitFor(() => expect(made[0].added).toHaveLength(1))
    await settle()
    // One handover at a time: the rest wait for the first, then go over in one.
    expect(made[0].added).toHaveLength(1)
    holdHandovers = false
    heldHandovers.forEach((release) => release())
    await vi.waitFor(() => expect(handed()).toHaveLength(7))
    expect(made[0].added).toHaveLength(2)
    drawn.destroy()
  })

  it('keeps the fonts that came when one can’t be fetched, asking for no more of them', async () => {
    const state = server(fontList(10), '/fonts/1')

    await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await settle()

    expect(made[0].fonts).toEqual([])
    expect(handed()).not.toContain(1)
    // Those already on their way (and at most one each started as they landed), not all ten.
    expect(state.asked.length).toBeLessThanOrEqual(FONTS_AT_ONCE * 2)
  })

  it('stops fetching and handing over fonts once it’s put away', async () => {
    let letFontsIn = () => {}
    const gate = new Promise<void>((resolve) => (letFontsIn = resolve))
    const state = server(fontList(10), undefined, gate)

    const drawn = await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await vi.waitFor(() => expect(state.asked.length).toBeGreaterThan(0))
    drawn.destroy()
    letFontsIn()
    await settle()

    expect(made[0].added).toEqual([])
    expect(state.asked).toHaveLength(FONTS_AT_ONCE)
  })

  it('draws a file with no fonts attached, or none listed', async () => {
    const state = server([])
    await drawStyled(document.createElement('video'), '/subs.ass', '/fonts', 0, new AbortController().signal)
    await drawStyled(document.createElement('video'), '/subs.ass', null, 0, new AbortController().signal)
    await settle()

    expect(made.map((m) => m.fonts)).toEqual([[], []])
    expect(made.map((m) => m.added)).toEqual([[], []])
    expect(state.asked).toEqual([])
  })

  it('asks for only the fonts the chosen track names', () => {
    expect(fontsFor('/api/v1/playback/s1/fonts.json', 'e3')).toBe('/api/v1/playback/s1/fonts.json?subtitle=e3')
    expect(fontsFor('/fonts.json?v=2', 'a b')).toBe('/fonts.json?v=2&subtitle=a%20b')
    expect(fontsFor(null, 'e3')).toBeNull()
    expect(fontsFor(undefined, 'e3')).toBeNull()
  })
})
