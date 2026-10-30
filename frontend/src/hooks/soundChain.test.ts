import { describe, expect, it } from 'vitest'
import { layoutOf } from './soundChain'

describe('sound chain layouts', () => {
  it('takes mono, stereo and unknown tracks as stereo', () => {
    for (const n of [1, 2, null]) expect(layoutOf(n).kind).toBe('stereo')
  })

  it('lifts the centre only where there is one', () => {
    const centre = (n: number) => layoutOf(n).map.some(([input, output]) => output === 2 && input === 2)
    expect(centre(4)).toBe(false) // Quad: channel 2 is a surround.
    for (const n of [3, 5, 6, 7, 8]) expect(centre(n)).toBe(true)
  })

  it('folds every channel of 6.1 and 7.1 into 5.1 rather than dropping any', () => {
    for (const n of [7, 8]) {
      const inputs = new Set(layoutOf(n).map.map(([input]) => input))
      expect(inputs.size).toBe(n)
      expect(Math.max(...layoutOf(n).map.map(([, output]) => output))).toBe(5)
    }
  })
})
