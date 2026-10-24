export type LibraryType = 'Movies' | 'TvShows' | 'Music'
export type MediaKind = 'Movie' | 'Series' | 'Season' | 'Episode'
export type VideoRange = 'Sdr' | 'Hdr10' | 'Hlg' | 'DolbyVision'
export type StreamType = 'Video' | 'Audio' | 'Subtitle'

export interface SystemStatus {
  appName: string
  version: string
  osDescription: string
  runtimeVersion: string
  startTime: string
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

export interface ItemSummary {
  id: string
  libraryId: string
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

export interface ItemDetail {
  item: ItemSummary
  path: string | null
  tvdbId: string | null
  tmdbId: string | null
  imdbId: string | null
  ancestors: ItemSummary[]
  children: ItemSummary[]
  files: MediaFile[]
}

export interface DirectoryListing {
  path: string
  parent: string | null
  directories: { name: string; path: string }[]
}
