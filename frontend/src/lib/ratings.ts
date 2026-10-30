/** The age limits a profile can have, youngest first, as the UK writes them. */
export const ratingSteps = [
  { age: 0, label: 'U' },
  { age: 8, label: 'PG' },
  { age: 12, label: '12' },
  { age: 15, label: '15' },
  { age: 18, label: '18' },
] as const

/** A rating age's UK name: U, PG, 12, 15 or 18. */
export const ratingLabel = (age: number) => ratingSteps.find((s) => s.age === age)?.label ?? String(age)

/** What an age limit lets through, for a profile's line in Settings: "Kids · up to PG", "Up to 15". */
export function ageLimitText(limit: { maxRatingAge?: number | null; isKids?: boolean }) {
  if (limit.maxRatingAge == null) return null
  const upTo = `up to ${ratingLabel(limit.maxRatingAge)}`
  return limit.isKids ? `Kids profile · ${upTo}` : upTo[0].toUpperCase() + upTo.slice(1)
}
