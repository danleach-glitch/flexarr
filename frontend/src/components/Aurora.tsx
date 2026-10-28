import { useEffect, useRef } from 'react'
import { createLevels, silence, type Levels } from '../lib/audioLevels'
import type { Rgb } from '../lib/palette'

/**
 * Light in the cover's colours, drifting behind Now Playing. Large soft fields wander on slow, never-repeating paths;
 * the bass makes them swell, a beat makes them flare, the middle hurries them along and the treble brings out small
 * bright glints. With nothing to hear (paused, or no `analyser`) they drift on their own; when `still` (the setting is
 * off, or the viewer asks for less motion) one calm frame is drawn and nothing moves.
 *
 * Drawn small on a canvas and scaled up blurred, so it costs little: a handful of gradients a frame.
 */
export function Aurora({ colours, analyser, playing, still }: { colours: Rgb[]; analyser: () => AnalyserNode | null; playing: boolean; still: boolean }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  // Read by the drawing loop each frame, so a new track or a pause changes the light without restarting it.
  const live = useRef({ colours, playing })
  useEffect(() => {
    live.current = { colours, playing }
  }, [colours, playing])
  // A still frame is drawn once, so it's drawn again for a new track's colours.
  const stillColours = still ? colours : null

  useEffect(() => {
    const el = canvas.current
    const ctx = el?.getContext('2d')
    if (!el || !ctx) return
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
    const levels = createLevels()
    let spectrum: Uint8Array<ArrayBuffer> | null = null
    // Each field's own clock, advanced by the music's pace, so a change of pace never makes anything jump.
    let clock = 0
    let pace = 0.02
    let last = performance.now()
    let frame = 0

    const resize = () => {
      // About a tenth of the screen's pixels: the blur hides it, and it's what keeps this cheap.
      const scale = 0.12
      el.width = Math.max(32, Math.round(el.clientWidth * scale))
      el.height = Math.max(32, Math.round(el.clientHeight * scale))
    }
    resize()
    const observer = new ResizeObserver(() => {
      resize()
      if (still || reduced) draw(silence)
    })
    observer.observe(el)

    function draw(level: Levels) {
      if (!ctx || !el) return
      const { colours } = live.current
      const w = el.width
      const h = el.height
      const span = Math.max(w, h)
      const [r0, g0, b0] = colours[0] ?? [120, 100, 170]
      ctx.globalCompositeOperation = 'source-over'
      ctx.fillStyle = `rgb(${Math.round(r0 * 0.08 + 6)} ${Math.round(g0 * 0.08 + 5)} ${Math.round(b0 * 0.08 + 10)})`
      ctx.fillRect(0, 0, w, h)
      ctx.globalCompositeOperation = 'screen'

      // Five large fields, each on its own Lissajous path, the cover's colours shared out between them.
      for (let i = 0; i < 5; i++) {
        const [r, g, b] = colours[i % Math.max(1, colours.length)] ?? [r0, g0, b0]
        const t = clock * (0.55 + i * 0.13) + i * 1.7
        const x = w * (0.5 + 0.42 * Math.sin(t * 0.71 + i) * Math.cos(t * 0.23))
        const y = h * (0.5 + 0.38 * Math.cos(t * 0.53 + i * 2.1) * Math.sin(t * 0.31 + 0.6))
        // Low fields (the first two) answer the bass; the rest the middle.
        const drive = i < 2 ? level.bass : level.mid
        const radius = span * (0.36 + 0.06 * Math.sin(t * 0.9)) * (1 + 0.22 * drive + 0.18 * level.pulse)
        const alpha = 0.34 + 0.24 * drive + 0.2 * level.pulse
        const field = ctx.createRadialGradient(x, y, 0, x, y, radius)
        field.addColorStop(0, `rgb(${r} ${g} ${b} / ${alpha})`)
        field.addColorStop(0.55, `rgb(${r} ${g} ${b} / ${alpha * 0.35})`)
        field.addColorStop(1, `rgb(${r} ${g} ${b} / 0)`)
        ctx.fillStyle = field
        ctx.fillRect(0, 0, w, h)
      }

      // Glints: small, quicker, nearly white versions of the colours, there only while the treble is.
      if (level.high > 0.05) {
        for (let i = 0; i < 4; i++) {
          const [r, g, b] = colours[(i + 1) % Math.max(1, colours.length)] ?? [r0, g0, b0]
          const t = clock * (1.6 + i * 0.37) + i * 2.3
          const x = w * (0.5 + 0.45 * Math.sin(t * 0.83 + i * 1.3))
          const y = h * (0.5 + 0.4 * Math.sin(t * 0.61 + i * 0.7) * Math.cos(t * 0.29))
          const radius = span * (0.09 + 0.05 * level.high)
          const alpha = 0.5 * level.high * (0.6 + 0.4 * Math.sin(t * 3.1 + i))
          const glint = ctx.createRadialGradient(x, y, 0, x, y, radius)
          glint.addColorStop(0, `rgb(${(r + 255 * 2) / 3} ${(g + 255 * 2) / 3} ${(b + 255 * 2) / 3} / ${alpha})`)
          glint.addColorStop(1, `rgb(${r} ${g} ${b} / 0)`)
          ctx.fillStyle = glint
          ctx.fillRect(0, 0, w, h)
        }
      }
    }

    if (still || reduced) {
      if (stillColours) live.current.colours = stillColours
      draw(silence)
      return () => observer.disconnect()
    }

    const tick = (now: number) => {
      // A long gap (the tab was hidden) counts as one ordinary frame, so nothing leaps when it comes back.
      const dt = Math.min(0.1, (now - last) / 1000)
      last = now
      const node = live.current.playing ? analyser() : null
      if (node) {
        if (spectrum?.length !== node.frequencyBinCount) spectrum = new Uint8Array(node.frequencyBinCount)
        node.getByteFrequencyData(spectrum)
      }
      const level = levels.update(node ? spectrum : null, node?.context.sampleRate ?? 48000, dt)
      // Playing, it drifts and the middle quickens it; paused, it slows almost to a stop.
      const target = live.current.playing ? 0.05 + 0.12 * level.mid + 0.05 * level.pulse : 0.008
      pace += (target - pace) * (1 - Math.exp(-dt * 1.5))
      clock += pace * dt * 4
      draw(level)
      frame = requestAnimationFrame(tick)
    }
    frame = requestAnimationFrame(tick)
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
    }
  }, [analyser, still, stillColours])

  return <canvas ref={canvas} className="aurora" aria-hidden="true" />
}
