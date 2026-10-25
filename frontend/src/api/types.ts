export type LibraryType = 'Movies' | 'TvShows' | 'Music'
export type MediaKind = 'Movie' | 'Series' | 'Season' | 'Episode' | 'Artist' | 'Album' | 'Track'
export type VideoRange = 'Sdr' | 'Hdr10' | 'Hlg' | 'DolbyVision'
export type StreamType = 'Video' | 'Audio' | 'Subtitle'
export type CreditRole = 'Actor' | 'Director' | 'Writer' | 'Producer' | 'Creator'
export type PlaybackMethod = 'DirectPlay' | 'Transcode'
export type MarkerType = 'Intro' | 'Recap' | 'Credits'
export type HardwareAcceleration = 'Auto' | 'None' | 'VideoToolbox' | 'Vaapi' | 'Qsv' | 'Nvenc'
export type ArrKind = 'Sonarr' | 'Radarr' | 'Lidarr'

export interface SystemStatus {
  appName: string
  version: string
  osDescription: string
  runtimeVersion: string
  startTime: string
  /** The server's address for away from home (over Tailscale), once remote access is on. */
  remoteUrl: string | null
}

export interface ScanStatus {
  path: string
  total: number
  processed: number
}

export interface Library {
  id: string
  name: string
  type: LibraryType
  folders: string[]
  itemCount: number
  lastScannedAt: string | null
  scan: ScanStatus | null
}

export interface LibraryRequest {
  name: string
  type: LibraryType
  folders: string[]
}

export interface Artwork {
  poster: string | null
  backdrop: string | null
  logo: string | null
  thumb: string | null
}

export interface UserData {
  positionSeconds: number
  watched: boolean
  playCount: number
  lastPlayedAt: string | null
  unwatchedCount: number | null
}

/** What sort of release an album is, as the server boils Lidarr's types down; an album without one is a plain album. */
export type AlbumType = 'album' | 'ep' | 'single' | 'live' | 'compilation' | 'soundtrack' | 'other'

export interface ItemSummary {
  id: string
  libraryId: string
  parentId: string | null
  kind: MediaKind
  title: string
  year: number | null
  seasonNumber: number | null
  episodeNumber: number | null
  episodeNumberEnd: number | null
  airDate: string | null
  childCount: number
  durationSeconds: number | null
  resolution: string | null
  addedAt: string
  artwork: Artwork
  accentColor: string | null
  communityRating: number | null
  criticRating: number | null
  contentRating: string | null
  runtimeMinutes: number | null
  releaseDate: string | null
  userData: UserData
  /** The show for episodes and seasons; the artist for tracks and albums. */
  seriesTitle: string | null
  discNumber: number | null
  trackNumber: number | null
  /** The show's id for episodes and seasons (the artist's for tracks and albums). */
  seriesId?: string | null
  /** Albums only: what sort of release it is, when Lidarr has said. */
  albumType?: AlbumType | null
}

export interface Paged<T> {
  total: number
  items: T[]
}

export interface MediaStream {
  index: number
  type: StreamType
  codec: string
  profile: string | null
  language: string | null
  title: string | null
  isDefault: boolean
  isForced: boolean
  width: number | null
  height: number | null
  channels: number | null
}

export interface MediaFile {
  id: string
  path: string
  size: number
  container: string | null
  durationSeconds: number | null
  bitrate: number | null
  videoCodec: string | null
  width: number | null
  height: number | null
  videoRange: VideoRange
  audioCodec: string | null
  audioChannels: number | null
  probeError: string | null
  streams: MediaStream[]
}

export interface Credit {
  personId: string
  name: string
  role: CreditRole
  character: string | null
  image: string | null
}

export interface ItemDetail {
  item: ItemSummary
  path: string | null
  originalTitle: string | null
  overview: string | null
  studio: string | null
  status: string | null
  trailerYouTubeId: string | null
  genres: string[]
  tvdbId: string | null
  tmdbId: string | null
  imdbId: string | null
  ancestors: ItemSummary[]
  children: ItemSummary[]
  credits: Credit[]
  files: MediaFile[]
  childOverviews: Record<string, string | null>
  /** Matched by hand with Fix match: refreshes keep the chosen show or film. */
  matchLocked: boolean
  /** A series' Up Next episode, when asked for with its page (getItemPage). */
  nextUp?: ItemSummary | null
  /** The season a series' page opens on, with its episodes (getItemPage). */
  season?: ItemDetail | null
}

export interface Person {
  id: string
  name: string
  image: string | null
  credits: { item: ItemSummary; role: CreditRole; character: string | null }[]
}

export interface HomeRow {
  id: string
  title: string
  /**
   * 'upcoming' marks where Coming soon goes; its titles are in Home.upcoming. 'tracks' rows play rather than open.
   * 'playlists' rows carry the profile's playlists in `playlists` (items is empty).
   */
  style: 'poster' | 'wide' | 'top10' | 'upcoming' | 'tracks' | 'playlists'
  items: ItemSummary[]
  link: string | null
  playlists?: PlaylistSummary[] | null
}

/** One of the profile's music playlists, as a list shows it. */
export interface PlaylistSummary {
  id: string
  name: string
  trackCount: number
  durationSeconds: number
  /** Up to four of its tracks' album covers, first tracks first, for a mosaic. */
  covers: string[]
  /** A cover chosen for it, shown instead of the mosaic. */
  cover: string | null
  createdAt: string
  updatedAt: string
}

/** A place in a playlist: the entry (what's moved or removed) and the track in it. */
export interface PlaylistTrack {
  entryId: number
  track: ItemSummary
}

export interface PlaylistDetail {
  summary: PlaylistSummary
  tracks: PlaylistTrack[]
}

export interface DownloadState {
  progress: number
  timeLeft: string | null
  status: string | null
}

export interface UpcomingItem {
  id: string
  kind: MediaKind
  title: string
  seriesTitle: string | null
  seasonNumber: number | null
  episodeNumber: number | null
  date: string
  when: string
  overview: string | null
  poster: string | null
  still: string | null
  libraryItemId: string | null
  available: boolean
  download: DownloadState | null
  /** The Sonarr or Radarr instance it comes from, by name. */
  source?: string | null
  /** Its page in that app's web UI. */
  manageUrl?: string | null
}

export interface Download {
  id: string
  kind: MediaKind
  title: string
  seriesTitle: string | null
  seasonNumber: number | null
  episodeNumber: number | null
  poster: string | null
  libraryItemId: string | null
  state: DownloadState
  source?: string | null
  /** The Sonarr/Radarr queue it's in, where a stuck download is sorted out. */
  manageUrl?: string | null
}

export interface Home {
  featured: ItemSummary[]
  /** What the first featured title resumes (the episode, for a show), when it's from Continue Watching. */
  resume?: ItemSummary | null
  overviews: Record<string, string | null>
  rows: HomeRow[]
  upcoming: UpcomingItem[]
  downloading: number
}

/** Titles are films, shows and artists; albums and tracks come grouped on their own, a track with its album's name. */
export interface SearchResults {
  titles: ItemSummary[]
  episodes: ItemSummary[]
  people: { id: string; name: string; image: string | null; credits: number }[]
  genres: string[]
  albums: ItemSummary[]
  tracks: { item: ItemSummary; album: string | null }[]
}

export interface Genre {
  name: string
  movies: number
  shows: number
  backdrop: string | null
}

export interface DirectoryListing {
  path: string
  parent: string | null
  directories: { name: string; path: string }[]
}

export interface DeviceProfile {
  client: string
  containers: string[]
  videoCodecs: string[]
  audioCodecs: string[]
  hdr: boolean
  maxHeight: number
  maxAudioChannels: number
  maxBitrate: number
}

export interface PlaybackRequest {
  itemId: string
  profile: DeviceProfile
  fileId?: string
  audioStreamIndex?: number
  subtitleKey?: string
  maxBitrate?: number
  maxHeight?: number
  startPositionSeconds?: number
  forceTranscode?: boolean
  /** This player's previous session, which the new one takes over from (only that one ends). */
  replacesSessionId?: string
  /** With a show's id, plays a random episode; with an episode's, Next is a random one. */
  shuffle?: boolean
  /** Getting the next episode ready during the credits: conversion starts now, and it never ends another screen. */
  prepare?: boolean
}

export interface AudioTrack {
  index: number
  label: string
  codec: string
  language: string | null
  channels: number | null
  isDefault: boolean
}

export interface SubtitleTrack {
  key: string
  label: string
  codec: string
  language: string | null
  isForced: boolean
  isDefault: boolean
  isText: boolean
  url: string | null
}

export interface Marker {
  type: MarkerType
  start: number
  end: number
}

export interface PlaybackInfo {
  sessionId: string
  method: PlaybackMethod
  url: string
  reasons: string[]
  durationSeconds: number
  startPositionSeconds: number
  fileId: string
  audioTracks: AudioTrack[]
  selectedAudioIndex: number | null
  subtitles: SubtitleTrack[]
  selectedSubtitleKey: string | null
  subtitleBurnedIn: boolean
  markers: Marker[]
  item: ItemSummary
  ancestors: ItemSummary[]
  next: ItemSummary | null
  /** The episode before this one, in order (null on shuffle, and from servers before it existed). */
  previous?: ItemSummary | null
  transcodeSummary: string | null
  shuffle?: boolean
  preferences?: PlaybackPreferences | null
  /** This title's subtitles are shown this many seconds later (earlier when negative). */
  subtitleDelaySeconds?: number
}

export interface TranscodingSettings {
  hardwareAcceleration: HardwareAcceleration
  vaapiDevice: string
  softwarePreset: string
  maxStreamingBitrate: number
}

export interface ArrConnection {
  name: string
  kind: ArrKind
  baseUrl: string
  hasApiKey: boolean
}

export interface Settings {
  transcoding: TranscodingSettings
  availableHardwareAcceleration: HardwareAcceleration[]
  authenticationRequired: 'Enabled' | 'DisabledForLocalAddresses'
  pathMappings: { from: string; to: string }[]
  arr: ArrConnection[]
  seerr: { baseUrl: string; hasApiKey: boolean } | null
  dataDirectory: string
  ffmpegPath: string
  ffprobePath: string
  /** What Sonarr, Radarr and Lidarr webhooks send instead of the API key. */
  webhookSecret?: string | null
}

export interface SettingsUpdate {
  transcoding?: TranscodingSettings
  authenticationRequired?: Settings['authenticationRequired']
  pathMappings?: Settings['pathMappings']
  arr?: { name: string; kind: ArrKind; baseUrl: string; apiKey?: string }[]
  seerr?: { baseUrl: string; apiKey?: string }
}

export interface ArrStatus {
  name: string
  kind: ArrKind
  ok: boolean
  version: string | null
  error: string | null
}

export interface Activity {
  scan: ScanStatus | null
  episodesAnalysed: number
  episodesTotal: number
  itemsWithMetadata: number
  itemsTotal: number
  streams: {
    sessionId: string
    item: ItemSummary
    series: string | null
    method: PlaybackMethod
    transcode: string | null
    positionSeconds: number
    durationSeconds: number
    paused: boolean
  }[]
}

/** How a profile likes to watch: auto-play the next episode, and what to skip without asking. */
export interface PlaybackPreferences {
  autoPlayNext: boolean
  skipIntros: boolean
  skipRecaps: boolean
  skipCredits: boolean
  subtitleSize?: SubtitleSize
}

export type SubtitleSize = 'Small' | 'Standard' | 'Large' | 'ExtraLarge'

export interface LyricLine {
  /** Where in the track the line is sung, in seconds; null in unsynced lyrics. */
  time: number | null
  text: string
}

/** A track's lyrics: synced when every line has a time, otherwise plain text. */
export interface Lyrics {
  synced: boolean
  lines: LyricLine[]
}

/** A track's ReplayGain: gains in dB, peaks as a fraction of full scale, null for what its tags don't say. */
export interface Loudness {
  trackGain: number | null
  albumGain: number | null
  trackPeak: number | null
  albumPeak: number | null
}
