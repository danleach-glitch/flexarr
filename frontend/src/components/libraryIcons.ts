import type { LibraryType } from '../api/types'
import { icon } from './icons.generated'

/** The icon for each kind of library, in the sidebar and in Settings (design/tokens.json › icons). */
export const libraryIcons: Record<LibraryType, typeof icon.movies> = {
  Movies: icon.movies,
  TvShows: icon.shows,
  Music: icon.music,
  Podcasts: icon.podcasts,
  HomeVideos: icon.homeVideos,
  Audiobooks: icon.audiobooks,
}
