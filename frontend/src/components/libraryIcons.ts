import type { LibraryType } from '../api/types'
import { Film, Folder, Microphone, Music, Tv } from './icons'

/** The icon for each kind of library, in the sidebar and in Settings. */
export const libraryIcons: Record<LibraryType, typeof Film> = { Movies: Film, TvShows: Tv, Music, Podcasts: Microphone, HomeVideos: Folder }
