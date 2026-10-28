import type { ItemSummary } from '../api/types'

/** Where an item's card goes. Seasons open on their series' page with the season selected. */
export function itemLink(item: ItemSummary) {
  return item.kind === 'Season' && item.parentId ? `/item/${item.parentId}?season=${item.seasonNumber}` : `/item/${item.id}`
}

/**
 * A link the server passed on from elsewhere (Sonarr's page for a show, a release on GitHub), if it's an ordinary web
 * address. Anything else (a javascript: URL in a misconfigured or hostile app's answer) isn't linked.
 */
export function webLink(url: string | null | undefined): string | undefined {
  if (!url) return undefined
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : undefined
  } catch {
    return undefined
  }
}
