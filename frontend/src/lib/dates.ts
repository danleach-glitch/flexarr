/** "Tonight", "Tomorrow", "Friday" within a week, otherwise "Fri 7 Nov". */
export function dayLabel(iso: string, now = new Date()): string {
  const date = new Date(iso)
  const startOf = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const days = Math.round((startOf(date) - startOf(now)) / 86_400_000)
  if (days === 0) return date.getHours() >= 17 ? 'Tonight' : 'Today'
  if (days === 1) return 'Tomorrow'
  if (days === -1) return 'Yesterday'
  if (days > 1 && days < 7) return date.toLocaleDateString(undefined, { weekday: 'long' })
  return date.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })
}

export function timeLabel(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
}

/** Sonarr/Radarr report time left as "hh:mm:ss" (or "d.hh:mm:ss"); say it like a person would. */
export function friendlyTimeLeft(value: string | null): string | null {
  if (!value) return null
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(value)
  if (!match) return null
  const [, d, h, m] = match.map((x) => Number(x ?? 0))
  const minutes = (d * 24 + h) * 60 + m
  if (minutes < 1) return null
  if (minutes < 60) return `${minutes} min left`
  const hours = Math.round(minutes / 6) / 10
  return `${hours} h left`
}
