import type { ItemSummary } from '../api/types'

/** Where an item's card goes. Seasons open on their series' page with the season selected. */
export function itemLink(item: ItemSummary) {
  return item.kind === 'Season' && item.parentId ? `/item/${item.parentId}?season=${item.seasonNumber}` : `/item/${item.id}`
}
