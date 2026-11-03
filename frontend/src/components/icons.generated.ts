// Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON.

import { Antenna, Book, Bookmark, Calendar, CaptionsBubble, Check, CheckCircleFilled, DownloadCircle, Explicit, Film, Folder, Gear, History, Home, LayoutGrid, Microphone, More, MusicNote, Pause, Play, PlayStack, Playlist, Plus, Queue, Radio, Reset, Search, Shuffle, Sparkles, Stack, Trash, Tv, Users, Waveform } from './icons'

/** The icon for each action and place, the same idea on every client (design/tokens.json › icons). */
export const icon = {
  /** Play or Resume */
  play: Play,
  /** Pause */
  pause: Pause,
  /** Start over */
  startOver: Reset,
  /** Shuffle a show, an artist or a genre */
  shuffle: Shuffle,
  /** Artist radio */
  radio: Radio,
  /** Trailer */
  trailer: Film,
  /** Mark as watched */
  watched: Check,
  /** Already watched (the Watched button pressed) */
  isWatched: CheckCircleFilled,
  /** Mark as unwatched */
  markUnwatched: CheckCircleFilled,
  /** Add to watchlist */
  watchlist: Bookmark,
  /** On the watchlist */
  onWatchlist: Bookmark,
  /** Request a title or an album */
  request: Plus,
  /** Asked for, on its way */
  requested: History,
  /** Follow an artist's new releases */
  follow: Users,
  /** Following an artist */
  following: Check,
  /** Go to the artist */
  goToArtist: Microphone,
  /** Play next, after what's playing */
  playNext: Queue,
  /** Add to the end of the queue */
  addToQueue: Queue,
  /** Add to a playlist */
  addToPlaylist: Playlist,
  /** Download */
  download: DownloadCircle,
  /** Delete a title, album, artist or book */
  delete: Trash,
  /** Explicit (a badge on a track or episode) */
  explicit: Explicit,
  /** Search for subtitles */
  subtitles: CaptionsBubble,
  /** Go to the show */
  goToShow: PlayStack,
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
  downloads: DownloadCircle,
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
  music: MusicNote,
  /** Podcasts */
  podcasts: Microphone,
  /** An audiobooks library */
  audiobooks: Book,
  /** A home videos library */
  homeVideos: Folder,
} as const
