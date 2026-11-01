export type LibraryType = 'Movies' | 'TvShows' | 'Music' | 'Podcasts' | 'HomeVideos' | 'Audiobooks'
export type MediaKind = 'Movie' | 'Series' | 'Season' | 'Episode' | 'Artist' | 'Album' | 'Track' | 'Podcast' | 'PodcastEpisode' | 'Audiobook'
export type VideoRange = 'Sdr' | 'Hdr10' | 'Hlg' | 'DolbyVision'
export type StreamType = 'Video' | 'Audio' | 'Subtitle'
export type CreditRole = 'Actor' | 'Director' | 'Writer' | 'Producer' | 'Creator' | 'Author' | 'Narrator' | 'Translator'
export type PlaybackMethod = 'DirectPlay' | 'Transcode'
export type MarkerType = 'Intro' | 'Recap' | 'Credits'
export type HardwareAcceleration = 'Auto' | 'None' | 'VideoToolbox' | 'Vaapi' | 'Qsv' | 'Nvenc'
/** Readarr stands for any Readarr-compatible app (Readarr, Bookshelf, Chaptarr). */
export type ArrKind = 'Sonarr' | 'Radarr' | 'Lidarr' | 'Readarr'

export interface SystemStatus {
  appName: string
  version: string
  osDescription: string
  runtimeVersion: string
  startTime: string
  /** The server's address for away from home (over Tailscale), once remote access is on. */
  remoteUrl: string | null
  /** Whether errors go to Flexarr's developer (Sentry), and this server's random id for them; older servers leave it out. */
  errorReports?: ErrorReports
}

export interface ErrorReports {
  enabled: boolean
  installId: string | null
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
  /** Everything in it is fine for profiles with an age limit, rated or not. Absent from servers before 0.56. */
  fineForKids?: boolean
}

export interface LibraryRequest {
  name: string
  type: LibraryType
  folders: string[]
  fineForKids?: boolean
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
  /** Tracks only: the track's own artist when it isn't the album's (a compilation's tracks, a featured artist). */
  trackArtist?: string | null
  /** In a home video library: a "show" is a folder, its "seasons" folders in it, and its "episodes" videos, all shown by name. */
  homeVideo?: boolean
  /** A book's authors, narrator, series and length. Absent from servers before 0.58. */
  book?: BookSummary | null
  /**
   * Not from the server: where the player starts this queue entry, for a podcast episode played from the start rather
   * than from where it was left.
   */
  playFrom?: number
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
  /** Absent from servers before 0.53. */
  hdr10Plus?: boolean
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
  /** Bazarr can find subtitles for this film or episode. Absent from servers before 0.53. */
  canSearchSubtitles?: boolean
  /** For a film or show, whether it's on this profile's watchlist. Absent from servers before 0.56. */
  onWatchlist?: boolean | null
  /** The age it's suitable from, on the UK's steps (0 is U, 8 PG); null, unrated. Absent from servers before 0.56. */
  ratingAge?: number | null
  /** A parent's say for profiles with an age limit; only sent to the main profile. Absent from servers before 0.56. */
  kidsOverride?: KidsOverride | null
  /** A book's edition, series, chapters and bookmarks. Absent from servers before 0.58. */
  book?: BookDetail | null
}

export type KidsOverride = 'None' | 'Allow' | 'Block'

export interface Person {
  id: string
  name: string
  image: string | null
  credits: { item: ItemSummary; role: CreditRole; character: string | null }[]
  /** An author's or narrator's biography. Absent from servers before 0.58. */
  biography?: string | null
  /** An admin fixed this author by hand, so refreshes leave them alone. Absent from servers before 0.58. */
  locked?: boolean
  /** Where the photo came from (a PersonSource, or a newer one). Absent from servers before 0.58. */
  photoSource?: string | null
  /** Where the biography came from. Absent from servers before 0.58. */
  biographySource?: string | null
}

/** Where an author's photo or biography came from; servers may add more. */
export type PersonSource = 'folder' | 'readarr' | 'audible' | 'openlibrary' | 'tmdb' | 'tvmaze' | 'upload' | 'manual'

/** The photos and biographies an admin can choose from for an author: one of each per source at most. */
export interface PersonCandidates {
  photos: { source: string; image: string }[]
  biographies: { source: string; text: string }[]
}

/** An admin's change to an author. Choosing a photo or writing a biography locks them unless `locked` is false. */
export interface PersonUpdate {
  /** A candidate's source. */
  photo?: string
  biography?: string | null
  locked?: boolean
}

export interface HomeRow {
  id: string
  title: string
  /**
   * 'upcoming' marks where Coming soon goes; its titles are in Home.upcoming. 'tracks' rows play rather than open.
   * 'playlists' rows carry the profile's playlists in `playlists`, and 'mixes' rows its daily mixes in `mixes` (items
   * is empty for both).
   */
  style: 'poster' | 'wide' | 'top10' | 'upcoming' | 'tracks' | 'playlists' | 'mixes' | 'live'
  items: ItemSummary[]
  link: string | null
  playlists?: PlaylistSummary[] | null
  mixes?: MixSummary[] | null
  /** A 'live' row's favourite channels, with what's on (items is empty then). */
  channels?: LiveChannel[] | null
}

/** One of the profile's "Made for you" mixes for today: built from what they play, the same all day, new tomorrow. */
export interface MixSummary {
  /** Stable for the same mix from day to day; yesterday's mixes that didn't come back aren't found. */
  id: string
  /** "Rock Mix", or the artists it's made of when it has no genre. */
  name: string
  /** The artists in a genre mix; a word on where an artists mix came from. */
  description: string
  /** Up to four of its tracks' album covers, for a mosaic. */
  covers: string[]
  trackCount: number
}

export interface MixDetail {
  summary: MixSummary
  tracks: ItemSummary[]
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
  /** What each part-played featured title resumes, by its id. Absent from servers before 0.53. */
  resumes?: Record<string, ItemSummary> | null
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
  /** Audiobooks, apart from titles. Absent from servers before 0.58. */
  books?: ItemSummary[] | null
  bookSeries?: BookSeriesSearch[] | null
}

/** The profile's last few searches and what they opened from them, newest first, on every device (absent before 0.56). */
export interface RecentSearches {
  queries: string[]
  items: ItemSummary[]
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
  /** The server runs in the Mac app, which can only read folders chosen in macOS's folder window. */
  chooseOnMac?: boolean
}

export interface DeviceProfile {
  client: string
  containers: string[]
  videoCodecs: string[]
  audioCodecs: string[]
  hdr: boolean
  /** Shows Dolby Vision (Safari on an HDR screen). */
  dolbyVision?: boolean
  maxHeight: number
  maxAudioChannels: number
  maxBitrate: number
  /** Plays a file's sound later than its picture by itself (Web Audio), when the file is out of sync. */
  delaysAudio?: boolean
  /** Plays it earlier by itself: browsers can't, so the server moves the sound in its stream. */
  advancesAudio?: boolean
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
  /** A name for this device ("Safari on Mac"), for a book's "Jump to where you were on …". */
  deviceName?: string
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
  /** A subtitle file beside the video (which Bazarr can re-sync), not one inside it. Absent from servers before 0.53. */
  isExternal?: boolean
  /** Styled subtitles (ASS/SSA) as authored, drawn with their fonts and positions. Absent from servers before 0.53. */
  styledUrl?: string | null
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
  /** The file's sound plays this many seconds later (earlier when negative), for everyone. */
  audioDelaySeconds?: number
  /** The server's stream already moves the sound by audioDelaySeconds, so the player mustn't as well. */
  audioDelayInStream?: boolean
  /** The speed this profile watches the show at; absent or null for 1×. */
  speed?: number | null
  /** Bazarr can find subtitles for this (and re-sync files beside it). Absent from servers before 0.53. */
  canSearchSubtitles?: boolean
  /** The fonts attached to the file for its styled subtitles. Absent from servers before 0.53. */
  fontsUrl?: string | null
  /** A book's files on its timeline, chapters, bookmarks and place. Absent from servers before 0.58. */
  book?: BookManifest | null
}

export interface SubtitleFont {
  name: string
  url: string
}

/** A subtitle a provider offers, through Bazarr, best match first. */
export interface SubtitleResult {
  /** Passed back to download it. */
  id: string
  provider: string
  language: string
  languageLabel: string
  /** The release it was made for, as the provider names it. */
  release: string
  hearingImpaired: boolean
  forced: boolean
  /** Out of 100. */
  score: number
  /** Made for this exact file (matched by hash), so it should be in sync. */
  matchesFile: boolean
  uploader: string | null
}

/** A background re-sync of a file's subtitle: `none` when there's been none since the server started. */
export interface SubtitleSync {
  state: 'running' | 'done' | 'failed' | 'none'
  key: string | null
  error: string | null
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
  /** Whether there's a TMDB key (it's never sent back), for films Radarr doesn't have. */
  hasTmdbKey?: boolean
  dataDirectory: string
  ffmpegPath: string
  ffprobePath: string
  /** What Sonarr, Radarr and Lidarr webhooks send instead of the API key. */
  webhookSecret?: string | null
  /** The podcast charts' country: the one chosen (null for the server's region), and the one in use. */
  podcasts?: { country: string | null; resolvedCountry: string }
  /** The Live TV source (Dispatcharr); the password is never sent back. */
  liveTv?: {
    baseUrl: string
    username: string
    hasPassword: boolean
    rewindMinutes: number
    /** Chosen in Settings; null for the default. */
    dvrFolder: string | null
    /** Where recordings go now (the chosen folder, or the default). */
    dvrFolderInUse: string
    /** Channels open at once; null for what Dispatcharr says. */
    maxStreams: number | null
    paddingBeforeMinutes: number
    paddingAfterMinutes: number
    minFreeGigabytes: number
  } | null
  /** The server's name on the network, put in place of localhost in links to apps; none in a container. */
  hostName?: string | null
  /** Bazarr, for finding subtitles; the API key is never sent back. */
  bazarr?: { baseUrl: string; hasApiKey: boolean } | null
  /** Whether the main profile has a PIN of its own (from 0.57; saving it does nothing there). */
  askPinForMainProfile?: boolean
  /** The admin's own Trakt app, if one replaces Flexarr's. Absent from servers before 0.57. */
  traktApp?: { clientId: string; hasSecret: boolean } | null
}

export interface SettingsUpdate {
  transcoding?: TranscodingSettings
  authenticationRequired?: Settings['authenticationRequired']
  pathMappings?: Settings['pathMappings']
  arr?: { name: string; kind: ArrKind; baseUrl: string; apiKey?: string }[]
  seerr?: { baseUrl: string; apiKey?: string }
  /** An empty country goes back to the server's region. */
  podcasts?: { country: string }
  /** An empty address removes the source; an omitted password keeps the saved one (for the same server). */
  liveTv?: LiveTvSettingsUpdate
  /** An empty key removes it. */
  tmdb?: { apiKey: string }
  /** An empty address removes Bazarr; an omitted key keeps the saved one (for the same server). */
  bazarr?: { baseUrl: string; apiKey?: string }
  askPinForMainProfile?: boolean
  /** An empty client id goes back to Flexarr's own app; a blank secret keeps the saved one. */
  traktApp?: { clientId: string; clientSecret?: string }
}

export interface ArrStatus {
  name: string
  kind: ArrKind
  ok: boolean
  version: string | null
  error: string | null
  /** Which of the Readarr family answered: "Readarr", "Bookshelf" or "Chaptarr". Absent from servers before 0.58. */
  app?: string | null
}

/** One of an app's root folders, and where Flexarr finds it. */
export interface ArrFolder {
  app: string
  kind: ArrKind
  /** As the app sees it. */
  path: string
  /** Found: here at the same path. Mapped: a saved mapping leads here. Suggested: not here, but its titles say where. */
  state: 'Found' | 'Mapped' | 'Suggested' | 'Missing'
  /** Where it is here, when found or mapped. */
  local: string | null
  suggested: { from: string; to: string } | null
  /** How many titles the suggestion explains, of how many were compared (0 when it's only a folder of the same name). */
  agreeing: number
  compared: number
  /** Enough titles agree for the server to save it unasked. */
  certain: boolean
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
  /** Absent from servers before 0.53. */
  subtitlePosition?: SubtitlePosition
  /** In films wider than the screen, subtitles go in the black bar below the picture. Absent from servers before 0.53. */
  subtitlesInBlackBars?: boolean
}

export type SubtitleSize = 'Small' | 'Standard' | 'Large' | 'ExtraLarge'
export type SubtitlePosition = 'Bottom' | 'Raised' | 'Higher' | 'Top'

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

// ---------- Podcasts

export interface PodcastStatus {
  /** There's a Podcasts library this profile can see. */
  available: boolean
  /** There's no Podcasts library at all: the main profile can add one in Settings. */
  needsLibrary: boolean
  /** The country whose charts and directory Browse shows. */
  country: string
}

export interface PodcastSubscription {
  subscribedAt: string
  /** Heard in order: listed oldest first, and phones keep the oldest unplayed episodes. */
  oldestFirst: boolean
}

/** A profile's podcast settings, for every show it follows. */
export interface PodcastPreferences {
  /** Phones keep the newest episodes of followed shows without being asked (on Wi-Fi). */
  autoDownload: boolean
  /** How many of each show's newest unplayed episodes they keep. */
  keepEpisodes: number
  speed: number
  /** Podcasts has a place in the sidebar. */
  showInSidebar: boolean
}

export interface Podcast {
  id: string
  title: string
  author: string | null
  overview: string | null
  artwork: Artwork
  accentColor: string | null
  link: string | null
  categories: string[]
  explicit: boolean
  episodeCount: number
  unplayedCount: number
  latestEpisodeAt: string | null
  iTunesId: number | null
  feedUrl: string
  checkedAt: string | null
  /** Why the feed couldn't be fetched last time. */
  error: string | null
  /** Null when this profile doesn't follow it. */
  subscription: PodcastSubscription | null
}

export type PodcastEpisodeType = 'Full' | 'Trailer' | 'Bonus'

export interface PodcastEpisode {
  id: string
  podcastId: string
  podcastTitle: string
  title: string
  /** The notes as plain text: cut short in lists, whole from a single episode's page. */
  overview: string | null
  publishedAt: string | null
  durationSeconds: number | null
  season: number | null
  episode: number | null
  episodeType: PodcastEpisodeType
  explicit: boolean
  artwork: Artwork
  accentColor: string | null
  userData: UserData
  sizeBytes: number | null
  hasTranscript: boolean
}

/** A named part of an episode: from the feed's chapters file, or the times its notes list. */
export interface PodcastChapter {
  startSeconds: number
  title: string
}

/** A show in Apple's podcast directory. */
export interface DirectoryPodcast {
  iTunesId: number
  title: string
  author: string | null
  artwork: string | null
  genre: string | null
  explicit: boolean
  episodeCount: number | null
  /** False when Apple gives no feed for it, so it can't be added. */
  hasFeed: boolean
  /** Set when it's in the library already. */
  podcastId: string | null
  subscribed: boolean
}

export interface PodcastGenre {
  id: number
  name: string
}

export interface PodcastCountry {
  code: string
  name: string
}

export interface OpmlImport {
  added: number
  alreadySubscribed: number
  failed: string[]
}

// ---------- Live TV

export interface LiveTvSettingsUpdate {
  baseUrl: string
  username?: string
  password?: string
  rewindMinutes?: number
  /** Empty for the default. */
  dvrFolder?: string
  /** 0 for what Dispatcharr says. */
  maxStreams?: number
  paddingBeforeMinutes?: number
  paddingAfterMinutes?: number
  minFreeGigabytes?: number
}

export interface LiveTvStatus {
  configured: boolean
  channels: number
  refreshing: boolean
  refreshedAt: string | null
  error: string | null
  rewindMinutes: number
}

export interface LiveProgramme {
  id: number
  start: string
  end: string
  title: string
  subtitle: string | null
  description: string | null
  category: string | null
  season: number | null
  episode: number | null
  isNew: boolean
  /** On the DVR's list (in the guide only). */
  recording?: 'scheduled' | 'recording' | null
  /** A series covers it (in the guide only). */
  series?: boolean
}

export interface Recording {
  id: string
  ruleId: string | null
  channelId: string
  channelName: string
  title: string
  subtitle: string | null
  description: string | null
  season: number | null
  episode: number | null
  isNew: boolean
  start: string
  end: string
  state: 'scheduled' | 'recording' | 'completed' | 'failed' | 'cancelled' | 'skipped'
  /** Why it failed or was skipped, or a note ("Stopped early."). */
  reason: string | null
  size: number | null
  /** Won't record as things stand: recordings scheduled before it want every stream. */
  clashes: boolean
  /** In the library, to play. */
  itemId: string | null
}

export interface RecordingSeries {
  id: string
  title: string
  /** Only on this channel; null for any. */
  channelId: string | null
  channelName: string | null
  newOnly: boolean
  /** 0 keeps them all. */
  keepLatest: number
  upcoming: number
  recorded: number
}

export interface Recordings {
  upcoming: Recording[]
  recent: Recording[]
  series: RecordingSeries[]
  /** How many channels can be open at once, recordings and live viewing together. */
  streams: number
  folder: string
  freeBytes: number | null
}

export interface LiveChannel {
  id: string
  number: number
  name: string
  group: string | null
  logoUrl: string | null
  /** Starred by this profile. */
  isFavourite: boolean
  now: LiveProgramme | null
  next: LiveProgramme | null
  /** Profiles with an age limit may watch it. Absent from servers before 0.56. */
  fineForKids?: boolean
}

export interface LiveGuide {
  from: string
  until: string
  channels: (Omit<LiveChannel, 'now' | 'next'> & { programmes: LiveProgramme[] })[]
}

export interface LiveWatch {
  /** The viewer's id: the capability in the playlist's address. */
  viewer: string
  playlist: string
  channel: LiveChannel
  /** How the channel reaches the player ("Original", "Converted to 720p, deinterlaced"). */
  method: string
  rewindSeconds: number
}

export interface LiveTvTest {
  ok: boolean
  channels: number | null
  maxConnections: number | null
  expiresAt: string | null
  error: string | null
}

// ---------- Logs

export type LogLevel = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

/** One line of the server's log (or a device's, sent to it), newest first. */
export interface LogEntry {
  id: string
  timestamp: string
  level: LogLevel
  /** The area's key: general, playback, library… */
  area: string
  /** The logger's name. */
  source: string
  message: string
  exception: string | null
  /** What it was about: playbackId, requestId, device, profile, platform. */
  properties: Record<string, string>
}

export interface LogPage {
  entries: LogEntry[]
  /** Passed as `before` for the next older page; null at the end. */
  nextCursor: string | null
}

export interface LogQuery {
  level?: LogLevel
  area?: string
  q?: string
  before?: string
  limit?: number
}

export interface LogSettings {
  level: LogLevel
  /** Each area's own level; null follows `level`. */
  areas: { key: string; name: string; level: LogLevel | null }[]
  retentionDays: number
  maxSizeMb: number
  /** Errors from the server and its apps go to Flexarr's developer. Off unless switched on. */
  errorReports: boolean
}

export interface LogSettingsUpdate {
  level?: LogLevel
  areas?: { key: string; level: LogLevel | null }[]
  retentionDays?: number
  maxSizeMb?: number
  errorReports?: boolean
}

/** Diagnostics an Apple TV or iPhone sent from its own settings. */
export interface LogUpload {
  id: string
  device: string
  platform: string
  profile: string | null
  appVersion: string | null
  note: string | null
  uploadedAt: string
  sizeBytes: number
}

/** An error or warning from an app, for the server's log. */
export interface ClientLogEntry {
  timestamp: string
  level: 'Debug' | 'Information' | 'Warning' | 'Error'
  category: string
  message: string
  error?: string
  properties?: Record<string, string>
}

// ---------- Audiobooks (absent from servers before 0.58)

/** What a list shows about a book. */
export interface BookSummary {
  /** Up to two authors' names, "Lee Child, Andrew Child". */
  authors: string | null
  authorCount: number
  narrator: string | null
  series: string | null
  /** Its number in `series` ("3", "1.5"). */
  sequence: string | null
  durationSeconds: number
  finishedAt: string | null
}

export interface BookChapter {
  index: number
  title: string
  startSeconds: number
  endSeconds: number
}

export interface Bookmark {
  id: string
  positionSeconds: number
  note: string | null
  createdAt: string
}

/** A series a book is in, its number there, and how many books of it the library has. */
export interface BookSeriesRef {
  id: string
  name: string
  sequence: string | null
  bookCount: number
}

export type ChapterSource = 'None' | 'Embedded' | 'Cue' | 'Files' | 'Even' | 'Audible' | 'Edited'

/** A book's own page. */
export interface BookDetail {
  subtitle: string | null
  asin: string | null
  isbn: string | null
  publisher: string | null
  language: string | null
  abridged: boolean
  explicit: boolean
  durationSeconds: number
  chapterSource: ChapterSource
  finishedAt: string | null
  /** This profile's speed for the book; null, its default. */
  speed: number | null
  series: BookSeriesRef[]
  chapters: BookChapter[]
  bookmarks: Bookmark[]
  moreInSeries: ItemSummary[]
  byAuthor: ItemSummary[]
  readByNarrator: ItemSummary[]
}

/** One of a book's files on its timeline. */
export interface BookFile {
  index: number
  fileId: string
  offsetSeconds: number
  durationSeconds: number
  container: string | null
  audioCodec: string | null
  size: number
  url: string
  converted: boolean
}

/** Where this profile is in a book, and which device put it there. */
export interface BookProgress {
  positionSeconds: number
  finished: boolean
  finishedAt: string | null
  updatedAt: string | null
  device: string | null
}

/** How this profile listens to books. */
export interface BookListening {
  speed: number
  skipBackSeconds: number
  skipForwardSeconds: number
  smartRewind: boolean
}

/** Everything a player needs to play a book. */
export interface BookManifest {
  itemId: string
  durationSeconds: number
  files: BookFile[]
  chapters: BookChapter[]
  bookmarks: Bookmark[]
  progress: BookProgress
  speed: number
  listening: BookListening
  nextInSeries: ItemSummary | null
}

/** A series in a library's list of them. */
export interface BookSeriesSummary {
  id: string
  name: string
  bookCount: number
  finishedCount: number
  /** Its first few books' artwork, for a stacked cover. */
  covers: Artwork[]
  /** The next book in it this profile hasn't finished. */
  next: ItemSummary | null
}

/** A series in reading order. */
export interface BookSeries {
  id: string
  libraryId: string
  name: string
  description: string | null
  books: { book: ItemSummary; sequence: string | null }[]
  nextId: string | null
}

/** An author or narrator in a library's list of them. */
export interface BookPerson {
  id: string
  name: string
  image: string | null
  bookCount: number
}

/** An audiobooks library's front page. */
export interface BookShelves {
  continueListening: ItemSummary[]
  nextInSeries: ItemSummary[]
  recentlyAdded: ItemSummary[]
  series: BookSeriesSummary[]
  authors: BookPerson[]
  narrators: BookPerson[]
  genres: { name: string; count: number }[]
  finished: ItemSummary[]
  /** The languages its books are in, as their tags have them ("en", "eng"); optional, absent from servers that don't send it. */
  languages?: string[]
}

export interface BookSeriesSearch {
  id: string
  name: string
  bookCount: number
}
