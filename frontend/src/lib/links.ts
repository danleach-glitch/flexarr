import type { ItemSummary, LibraryType } from '../api/types'

/** Where an item's card goes. Seasons open on their series' page with the season selected. */
export function itemLink(item: ItemSummary) {
  return item.kind === 'Season' && item.parentId ? `/item/${item.parentId}?season=${item.seasonNumber}` : `/item/${item.id}`
}

/** A genre in a library: albums for music, a books library's genre page, or All filtered for movies and shows. */
export function libraryGenreLink(libraryId: string, type: LibraryType, genre: string) {
  const g = encodeURIComponent(genre)
  if (type === 'Music') return `/library/${libraryId}?view=albums&genre=${g}`
  if (type === 'Audiobooks') return `/library/${libraryId}?view=genre&genre=${g}`
  return `/library/${libraryId}?genre=${g}`
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
