/**
 * Turns a spectrum into the few numbers the visualiser moves by: how much bass, middle and treble there is (0 to 1),
 * and a pulse that jumps on a kick drum and falls away. Each band keeps a slowly sinking peak to measure against, so a
 * quiet recording moves as much as a loud one; levels rise quickly and fall slowly, so the light breathes rather than
 * flickers.
 */
export interface Levels {
  bass: number
  mid: number
  high: number
  /** The bass above its own recent average: a beat. */
  pulse: number
}

const BANDS = { bass: [30, 160], mid: [300, 2000], high: [3000, 10000] } as const
type Band = keyof typeof BANDS

export const silence: Levels = { bass: 0, mid: 0, high: 0, pulse: 0 }

export function createLevels() {
  const peak: Record<Band, number> = { bass: 0.2, mid: 0.2, high: 0.2 }
  const level: Levels = { ...silence }
  let slowBass = 0

  /** Eases `from` toward `to` at `rate` per second, frame rate aside. */
  const ease = (from: number, to: number, rate: number, dt: number) => from + (to - from) * (1 - Math.exp(-rate * dt))

  return {
    /**
     * Reads one frame. `spectrum` is an AnalyserNode's byte frequency data; null (paused, or nothing to hear) lets
     * everything settle to rest.
     */
    update(spectrum: Uint8Array | null, sampleRate: number, dt: number): Levels {
      for (const band of Object.keys(BANDS) as Band[]) {
        let raw = 0
        if (spectrum) {
          const hzPerBin = sampleRate / 2 / spectrum.length
          const [lo, hi] = BANDS[band]
          const from = Math.max(1, Math.floor(lo / hzPerBin))
          const to = Math.min(spectrum.length - 1, Math.ceil(hi / hzPerBin))
          let sum = 0
          for (let i = from; i <= to; i++) sum += spectrum[i]
          raw = to >= from ? sum / (to - from + 1) / 255 : 0
        }
        // The peak sinks by about half every 8 seconds, never below a floor that keeps hiss from looking like music.
        peak[band] = Math.max(raw, 0.08, peak[band] * Math.exp(-dt * 0.09))
        const target = Math.min(1, raw / peak[band])
        level[band] = ease(level[band], target, target > level[band] ? 14 : 2.5, dt)
      }
      slowBass = ease(slowBass, level.bass, 1.2, dt)
      const kick = Math.max(0, level.bass - slowBass) * 2.4
      level.pulse = ease(level.pulse, Math.min(1, kick), kick > level.pulse ? 20 : 3, dt)
      return level
    },
  }
}
