import type { RatingSystem } from '../api/client'
import { t } from '../i18n'

/**
 * The age limits a profile can have, youngest first, in the names a server uses (its country decides). The ages are
 * the same for both, and a server too old to say is the UK's.
 */
export const ratingSteps = (system: RatingSystem = 'uk') =>
  system === 'us'
    ? [
        { age: 0, label: 'G' },
        { age: 8, label: 'PG' },
        { age: 12, label: 'PG-13' },
        { age: 15, label: 'TV-14' },
        { age: 18, label: 'R' },
      ]
    : [
        { age: 0, label: 'U' },
        { age: 8, label: 'PG' },
        { age: 12, label: '12' },
        { age: 15, label: '15' },
        { age: 18, label: '18' },
      ]

/** A rating age's name: U, PG, 12, 15 or 18, or G, PG, PG-13, TV-14 or R. */
export const ratingLabel = (age: number, system?: RatingSystem) => ratingSteps(system).find((s) => s.age === age)?.label ?? String(age)

/** What an age limit lets through, for a profile's line in Settings: "Kids · up to PG", "Up to 15". */
export function ageLimitText(limit: { maxRatingAge?: number | null; isKids?: boolean }, system?: RatingSystem) {
  if (limit.maxRatingAge == null) return null
  const rating = ratingLabel(limit.maxRatingAge, system)
  return limit.isKids ? t('Kids profile, up to {rating}', { rating }) : t('Up to {rating}', { rating })
}
