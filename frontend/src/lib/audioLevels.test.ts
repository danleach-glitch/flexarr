import { describe, expect, it } from 'vitest'
import { createLevels } from './audioLevels'

const spectrum = (bass: number, high: number) => {
  // 1024 bins over 0–24 kHz, about 23 Hz a bin.
  const s = new Uint8Array(1024)
  for (let i = 1; i < 8; i++) s[i] = bass
  for (let i = 128; i < 430; i++) s[i] = high
  return s
}

const run = (levels: ReturnType<typeof createLevels>, s: Uint8Array | null, seconds: number) => {
  let last = levels.update(s, 48000, 1 / 60)
  for (let t = 0; t < seconds; t += 1 / 60) last = levels.update(s, 48000, 1 / 60)
  return { ...last }
}

describe('createLevels', () => {
  it('hears bass and treble in their own bands', () => {
    const levels = run(createLevels(), spectrum(220, 0), 1)
    expect(levels.bass).toBeGreaterThan(0.8)
    expect(levels.high).toBeLessThan(0.05)
  })

  it('moves as much for a quiet recording as a loud one', () => {
    const quiet = run(createLevels(), spectrum(60, 60), 10)
    const loud = run(createLevels(), spectrum(240, 240), 10)
    expect(Math.abs(quiet.bass - loud.bass)).toBeLessThan(0.1)
  })

  it('settles when there is nothing to hear', () => {
    const levels = createLevels()
    run(levels, spectrum(220, 220), 1)
    const after = run(levels, null, 3)
    expect(after.bass).toBeLessThan(0.05)
    expect(after.pulse).toBeLessThan(0.05)
  })

  it('pulses on a kick and falls back while it holds', () => {
    const levels = createLevels()
    run(levels, spectrum(40, 0), 2)
    const kick = run(levels, spectrum(240, 0), 0.1)
    const held = run(levels, spectrum(240, 0), 4)
    expect(kick.pulse).toBeGreaterThan(0.3)
    expect(held.pulse).toBeLessThan(kick.pulse)
  })
})
