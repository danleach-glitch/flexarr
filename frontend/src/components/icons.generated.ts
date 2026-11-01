// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

import { Antenna, Book, Bookmark, Calendar, Captions, Check, Download, EyeOff, Film, Folder, Gear, Home, LayoutGrid, Microphone, More, Music, Play, Reset, Search, Shuffle, Sparkles, Stack, Tv, Waveform } from './icons'

/** The icon for each action and place, the same idea on every client (design/tokens.json › icons). */
export const icon = {
  /** Play or Resume */
  play: Play,
  /** Start over */
  startOver: Reset,
  /** Shuffle a show */
  shuffle: Shuffle,
  /** Trailer */
  trailer: Film,
  /** Mark as watched */
  watched: Check,
  /** Mark as unwatched */
  markUnwatched: EyeOff,
  /** Add to watchlist */
  watchlist: Bookmark,
  /** On the watchlist */
  onWatchlist: Bookmark,
  /** Download */
  download: Download,
  /** Search for subtitles */
  subtitles: Captions,
  /** Go to the show */
  goToShow: Stack,
  /** More actions */
  more: More,
  /** Home */
  home: Home,
  /** Libraries */
  libraries: LayoutGrid,
  /** Search */
  search: Search,
  /** Settings */
  settings: Gear,
  /** Downloads */
  downloads: Download,
  /** Discover */
  discover: Sparkles,
  /** Coming soon */
  comingSoon: Calendar,
  /** Live TV */
  liveTv: Antenna,
  /** Collections */
  collections: Stack,
  /** Now playing */
  nowPlaying: Waveform,
  /** A movie library */
  movies: Film,
  /** A TV library */
  shows: Tv,
  /** A music library */
  music: Music,
  /** Podcasts */
  podcasts: Microphone,
  /** An audiobooks library */
  audiobooks: Book,
  /** A home videos library */
  homeVideos: Folder,
} as const
