/**
 * How long before the end the next episode is offered when no credits were found: 3% of the episode, between 30
 * seconds and 2 minutes (about 40 seconds of a half-hour show, 100 of an hour-long one). The apps use the same rule.
 */
export function unmarkedCreditsSeconds(duration: number): number {
  return Math.min(Math.max(duration * 0.03, 30), 120)
}
