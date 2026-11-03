import { errorReporter } from './errorReporter'

/**
 * Times each start, from Play to the first picture (or sound), and sends it to the server's log (Settings › Logs) as
 * one Information entry. docs/fast-start.md lists the fields.
 */
export interface StartTiming {
  kind: 'video' | 'book'
  /** performance.now() when Play was pressed (or the player opened). */
  pressedAt: number
  /** When the server's start answer came (the same moment as pressedAt for a warm-up taken over). */
  answeredAt?: number
  warmed?: boolean
  method?: string
  playbackId?: string
}

/** Best guess whether this browser reaches the server from away: not this machine, a private address or a .local name. */
export function looksRemote(hostname: string = location.hostname): boolean {
  const host = hostname.replace(/^\[|\]$/g, '').toLowerCase()
  if (host === 'localhost' || host.endsWith('.local') || host === '::1') return false
  const v4 = host.match(/^(\d+)\.(\d+)\.\d+\.\d+$/)
  if (v4) {
    const [a, b] = [Number(v4[1]), Number(v4[2])]
    return !(a === 127 || a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254))
  }
  if (/^(fc|fd|fe80)/.test(host) && host.includes(':')) return false
  return true
}

/** Sends one start's timings; `firstAt` is when the picture (or sound) came. */
export function reportStart(timing: StartTiming, firstAt: number = performance.now(), report = errorReporter.report) {
  const ms = (at: number | undefined) => String(Math.max(0, Math.round((at ?? firstAt) - timing.pressedAt)))
  report({
    level: 'Information',
    category: 'playback',
    // The id keeps each start its own entry (the reporter sends a message once a minute).
    message: `Playback ${timing.playbackId ?? 'unknown'} started`,
    properties: {
      kind: timing.kind,
      method: timing.method ?? 'unknown',
      warmed: String(timing.warmed ?? false),
      remote: String(looksRemote()),
      requestMs: ms(timing.answeredAt),
      firstFrameMs: ms(firstAt),
      playbackId: timing.playbackId ?? '',
    },
  })
}
