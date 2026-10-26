// What Live TV's pages share: how programme times read, and how far through one we are.

import type { LiveProgramme } from '../api/client'

export const clock = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' })

export const programmeTime = (p: Pick<LiveProgramme, 'start' | 'end'>) => `${clock.format(new Date(p.start))} – ${clock.format(new Date(p.end))}`

/** How far through a programme we are, 0 to 1. */
export function progress(p: Pick<LiveProgramme, 'start' | 'end'>, now = Date.now()) {
  const start = Date.parse(p.start)
  const end = Date.parse(p.end)
  return end > start ? Math.min(1, Math.max(0, (now - start) / (end - start))) : 0
}

/** Dispatcharr's group names often carry the provider's decoration ("UK| NEWS ᴴᴰ/ᴿᴬᵂ"); show the words. */
export function tidyGroup(group: string) {
  return (
    group
      .replace(/[\u1D00-\u1DBF\u2070-\u209F\u02B0-\u02FF\u2C7C-\u2C7D]+/gu, '')
      .replace(/\s*\/\s*$/u, '')
      .replace(/\s*\|\s*/u, ' ')
      .replace(/\s{2,}/gu, ' ')
      .trim() || group
  )
}
