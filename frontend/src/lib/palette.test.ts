import { describe, expect, it } from 'vitest'
import { fillPalette, paletteFrom, parseHex, type Rgb } from './palette'

const pixels = (...runs: [Rgb, number][]) => {
  const out: number[] = []
  for (const [[r, g, b], n] of runs) for (let i = 0; i < n; i++) out.push(r, g, b, 255)
  return new Uint8ClampedArray(out)
}

describe('paletteFrom', () => {
  it('puts the colour that covers most of the cover first', () => {
    const colours = paletteFrom(pixels([[200, 30, 30], 60], [[30, 60, 200], 30]))
    expect(colours).toHaveLength(2)
    expect(colours[0][0]).toBeGreaterThan(colours[0][2]) // red
    expect(colours[1][2]).toBeGreaterThan(colours[1][0]) // blue
  })

  it('ignores black, white and grey', () => {
    expect(paletteFrom(pixels([[0, 0, 0], 50], [[255, 255, 255], 50], [[128, 128, 128], 50]))).toEqual([])
  })

  it('lifts dark colours so they glow', () => {
    const [[r]] = paletteFrom(pixels([[90, 10, 10], 10]))
    expect(r).toBeGreaterThan(150)
  })
})

describe('fillPalette', () => {
  it('always gives enough colours to move', () => {
    expect(fillPalette([[200, 40, 40]])).toHaveLength(4)
    expect(fillPalette([])).toHaveLength(4)
  })
})

it('reads hex colours', () => {
  expect(parseHex('#ff8000')).toEqual([255, 128, 0])
  expect(parseHex('nope')).toBeNull()
})
