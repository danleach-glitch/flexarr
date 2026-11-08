import { render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Aurora } from './Aurora'

/** A canvas that counts whole frames (each starts by filling the background), and a screen refreshing at 60 Hz. */
let frames = 0
let now = 0
let queued: FrameRequestCallback[] = []

beforeEach(() => {
  frames = 0
  now = 0
  queued = []
  vi.spyOn(performance, 'now').mockImplementation(() => now)
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => queued.push(callback))
  vi.stubGlobal('cancelAnimationFrame', () => (queued = []))
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
    },
  )
  const gradient = { addColorStop() {} }
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(
    () =>
      ({
        set globalCompositeOperation(mode: string) {
          if (mode === 'source-over') frames++
        },
        fillRect() {},
        createRadialGradient: () => gradient,
      }) as unknown as CanvasRenderingContext2D,
  )
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

/** Runs the screen for `seconds`, and says how many frames were drawn in that time. */
function run(seconds: number) {
  const before = frames
  for (let i = 0; i < seconds * 60; i++) {
    now += 1000 / 60
    const due = queued
    queued = []
    for (const callback of due) callback(now)
  }
  return frames - before
}

const colours: [number, number, number][] = [[200, 80, 120]]

describe('the Now Playing light', () => {
  it('is drawn thirty times a second while the music plays, not at the screen’s full rate', () => {
    render(<Aurora colours={colours} analyser={() => null} playing still={false} />)
    expect(run(2)).toBe(60)
  })

  it('slows to ten a second once a pause has brought it nearly to a stop', () => {
    const { rerender } = render(<Aurora colours={colours} analyser={() => null} playing still={false} />)
    run(1)
    rerender(<Aurora colours={colours} analyser={() => null} playing={false} still={false} />)
    run(2)
    expect(run(2)).toBe(20)

    rerender(<Aurora colours={colours} analyser={() => null} playing still={false} />)
    run(0.5)
    expect(run(2)).toBe(60)
  })

  it('stops being drawn a few seconds into a pause, and moves again when the music plays', () => {
    const { rerender } = render(<Aurora colours={colours} analyser={() => null} playing still={false} />)
    run(1)
    rerender(<Aurora colours={colours} analyser={() => null} playing={false} still={false} />)
    run(6)
    expect(run(10)).toBe(0)
    expect(queued).toHaveLength(0)

    rerender(<Aurora colours={colours} analyser={() => null} playing still={false} />)
    expect(run(2)).toBe(60)
  })

  it('draws a stopped light once more for a new cover’s colours, then stops again', () => {
    const { rerender } = render(<Aurora colours={colours} analyser={() => null} playing={false} still={false} />)
    run(10)
    expect(run(2)).toBe(0)
    rerender(<Aurora colours={[[20, 140, 220]]} analyser={() => null} playing={false} still={false} />)
    expect(run(2)).toBeGreaterThan(0)
    run(6)
    expect(run(2)).toBe(0)
  })
})
