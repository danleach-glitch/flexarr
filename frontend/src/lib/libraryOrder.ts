/**
 * The profile's library order after the shown libraries were put in a new order. Entries this app doesn't show
 * (Live TV's place, a library hidden from it) keep the places they had; the shown ones fill the rest in their new
 * order, and any the saved order didn't have yet follow.
 */
export function reorderedLibraries(saved: readonly string[], shown: readonly string[]): string[] {
  const showing = new Set(shown)
  const next = [...shown]
  const order = saved.map((id) => (showing.has(id) ? next.shift()! : id)).filter((id) => id !== undefined)
  return [...order, ...next]
}
