import type { RequestDestination } from '../api/client'

/** The libraries a profile's requests of one kind could go to: those Seerr can add to that it can see. */
export function requestChoices(destinations: RequestDestination[], mediaType: RequestDestination['mediaType'], hiddenLibraryIds: string[]) {
  return destinations.filter((d) => d.mediaType === mediaType && !hiddenLibraryIds.includes(d.id))
}

/** Whether there's anything to choose: two or more libraries of a kind. */
export function hasRequestChoice(destinations: RequestDestination[], hiddenLibraryIds: string[]) {
  return (['tv', 'movie'] as const).some((t) => requestChoices(destinations, t, hiddenLibraryIds).length > 1)
}

export const requestRows = [
  { mediaType: 'tv', key: 'requestShowsLibraryId', label: 'New shows go to', help: 'The library a show is added to when you request it.' },
  { mediaType: 'movie', key: 'requestFilmsLibraryId', label: 'New films go to', help: 'The library a film is added to when you request it.' },
] as const
