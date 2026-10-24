import type {
  Activity,
  ArrStatus,
  DirectoryListing,
  Download,
  Genre,
  Home,
  ItemDetail,
  ItemSummary,
  Library,
  LibraryRequest,
  Paged,
  Person,
  PlaybackInfo,
  PlaybackRequest,
  SearchResults,
  Settings,
  SettingsUpdate,
  SystemStatus,
  UpcomingItem,
} from './types'

export type * from './types'

const API_KEY_STORAGE = 'flexarr.apiKey'
const PROFILE_STORAGE = 'flexarr.profile'

/** Thrown when the server rejects the request for a missing or wrong API key. */
export class UnauthorizedError extends Error {
  constructor() {
    super('API key required')
  }
}

/** A non-2xx response. `messages` holds validation errors from ASP.NET problem details, if any. */
export class ApiError extends Error {
  readonly status: number
  readonly messages: string[]

  constructor(status: number, statusText: string, messages: string[]) {
    super(messages[0] ?? `${status} ${statusText}`)
    this.status = status
    this.messages = messages
  }
}

/** The server accepts the key as an X-Api-Key header; local-network requests may not need one. */
function apiKey(): string | null {
  try {
    return localStorage.getItem(API_KEY_STORAGE)
  } catch {
    return null
  }
}

function stored(name: string): string | null {
  try {
    return localStorage.getItem(name)
  } catch {
    return null
  }
}

/** The chosen profile's token (sent as X-Flexarr-Profile); null means the main profile. */
export const profileToken = () => stored(PROFILE_STORAGE)

export function saveProfileToken(token: string | null) {
  try {
    if (token) localStorage.setItem(PROFILE_STORAGE, token)
    else localStorage.removeItem(PROFILE_STORAGE)
  } catch {
    // Storage unavailable: the profile lasts until the page reloads.
  }
}

export function saveApiKey(key: string) {
  try {
    localStorage.setItem(API_KEY_STORAGE, key)
  } catch {
    // Storage unavailable (private mode); the key only lasts for this request.
  }
}

async function request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const headers: Record<string, string> = {}
  const key = apiKey()
  if (key) headers['X-Api-Key'] = key
  const profile = profileToken()
  if (profile) headers['X-Flexarr-Profile'] = profile
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const response = await fetch(path.startsWith('/api/') ? path : `/api/v1${path}`, {
    method,
    signal,
    headers: Object.keys(headers).length ? headers : undefined,
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === 401) {
    throw new UnauthorizedError()
  }
  if (!response.ok) {
    let messages: string[] = []
    try {
      const problem = (await response.json()) as { errors?: Record<string, string[]>; title?: string; detail?: string }
      messages = problem.errors ? Object.values(problem.errors).flat() : [problem.detail ?? problem.title].filter((m): m is string => !!m)
    } catch {
      // Not a JSON problem response.
    }
    throw new ApiError(response.status, response.statusText, messages)
  }
  if (response.status === 202 || response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}

export const apiGet = <T>(path: string, signal?: AbortSignal) => request<T>('GET', path, undefined, signal)

export const getSystemStatus = (signal?: AbortSignal) => apiGet<SystemStatus>('/system/status', signal)

export const getLibraries = (signal?: AbortSignal) => apiGet<Library[]>('/libraries', signal)
export const createLibrary = (library: LibraryRequest) => request<Library>('POST', '/libraries', library)
export const updateLibrary = (id: string, library: LibraryRequest) => request<Library>('PUT', `/libraries/${id}`, library)
export const deleteLibrary = (id: string) => request<void>('DELETE', `/libraries/${id}`)
export const scanLibrary = (id: string, refreshMetadata = false) =>
  request<void>('POST', `/libraries/${id}/scan${refreshMetadata ? '?refreshMetadata=true' : ''}`)

export function getLibraryItems(
  id: string,
  options: { search?: string; sort?: string; genre?: string; skip?: number; take?: number },
  signal?: AbortSignal,
) {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(options)) {
    if (value !== undefined && value !== '' && value !== 0) params.set(key, String(value))
  }
  const query = params.size ? `?${params}` : ''
  return apiGet<Paged<ItemSummary>>(`/libraries/${id}/items${query}`, signal)
}

export const getLibraryGenres = (id: string, signal?: AbortSignal) =>
  apiGet<{ name: string; count: number }[]>(`/libraries/${id}/genres`, signal)

export const getHome = (signal?: AbortSignal) => apiGet<Home>('/home', signal)
export const getItem = (id: string, signal?: AbortSignal) => apiGet<ItemDetail>(`/items/${id}`, signal)
export const getSimilar = (id: string, signal?: AbortSignal) => apiGet<ItemSummary[]>(`/items/${id}/similar`, signal)
export const getNextUp = (id: string, signal?: AbortSignal) => apiGet<ItemSummary | undefined>(`/items/${id}/next-up`, signal)
export const setWatched = (id: string, watched: boolean) => request<void>('POST', `/items/${id}/watched`, { watched })
export const analyzeItem = (id: string) => request<void>('POST', `/items/${id}/analyze`)
export const getPerson = (id: string, signal?: AbortSignal) => apiGet<Person>(`/people/${id}`, signal)
export const search = (q: string, signal?: AbortSignal) => apiGet<SearchResults>(`/search?q=${encodeURIComponent(q)}`, signal)
export const getGenres = (signal?: AbortSignal) => apiGet<Genre[]>('/genres', signal)
export const getGenreItems = (name: string, kind?: string, signal?: AbortSignal) =>
  apiGet<Paged<ItemSummary>>(`/genres/${encodeURIComponent(name)}/items?take=120${kind ? `&kind=${kind}` : ''}`, signal)

export const startPlayback = (body: PlaybackRequest) => request<PlaybackInfo>('POST', '/playback', body)
export const reportProgress = (sessionId: string, positionSeconds: number, paused: boolean, ended = false) =>
  request<void>('POST', `/playback/${sessionId}/progress`, { positionSeconds, paused, ended })

/** Fire-and-forget progress for page unloads, when fetch may be cancelled. */
export function beaconProgress(sessionId: string, positionSeconds: number) {
  const body = new Blob([JSON.stringify({ positionSeconds, paused: true })], { type: 'application/json' })
  navigator.sendBeacon?.(`/api/v1/playback/${sessionId}/progress`, body)
}

export const stopPlayback = (sessionId: string) =>
  fetch(`/api/v1/playback/${sessionId}`, { method: 'DELETE', keepalive: true }).catch(() => undefined)

export const getSettings = (signal?: AbortSignal) => apiGet<Settings>('/settings', signal)
export const saveSettings = (update: SettingsUpdate) => request<Settings>('PUT', '/settings', update)
export const getApiKey = () => apiGet<{ apiKey: string }>('/settings/api-key')
export const getArrStatus = (signal?: AbortSignal) => apiGet<ArrStatus[]>('/arr/status', signal)
export const testArr = (connection: { name: string; kind: string; baseUrl: string; apiKey?: string }) =>
  request<ArrStatus>('POST', '/arr/test', connection)
export const getActivity = (signal?: AbortSignal) => apiGet<Activity>('/system/activity', signal)

export const browseFilesystem = (path?: string, signal?: AbortSignal) =>
  apiGet<DirectoryListing>(`/filesystem${path ? `?path=${encodeURIComponent(path)}` : ''}`, signal)

/** Artwork URLs from the API with a width hint for the image cache. */
export function imageUrl(url: string | null | undefined, width: number): string | undefined {
  if (!url) return undefined
  return `${url}${url.includes('?') ? '&' : '?'}w=${width}`
}

export interface PlexSource {
  path: string | null
  found: boolean
  accounts: { id: number; name: string; watched: number; inProgress: number }[]
}

export interface PlexImportResult {
  matched: number
  watched: number
  inProgress: number
  unmatched: number
  keptNewer: number
}

export const getPlexSource = (path?: string, signal?: AbortSignal) =>
  apiGet<PlexSource>(`/import/plex${path ? `?path=${encodeURIComponent(path)}` : ''}`, signal)
export const importFromPlex = (accountId: number, path?: string, profileId?: string) =>
  request<PlexImportResult>('POST', '/import/plex', { accountId, path, profileId })

export const getUpcoming = (days = 21, signal?: AbortSignal) => apiGet<UpcomingItem[]>(`/upcoming?days=${days}`, signal)
export const getDownloads = (signal?: AbortSignal) => apiGet<Download[]>('/downloads', signal)

export interface LibraryHealth {
  duplicates: {
    item: import('./types').ItemSummary
    seriesTitle: string | null
    files: { id: string; path: string; size: number; resolution: string | null; videoCodec: string | null; bitrate: number | null; keep: boolean }[]
    reclaimableBytes: number
  }[]
  reclaimableBytes: number
  unreadable: { fileId: string; path: string; error: string; item: import('./types').ItemSummary | null }[]
  missingArtwork: import('./types').ItemSummary[]
  standardDefinition: import('./types').ItemSummary[]
  standardDefinitionTotal: number
}

export const getLibraryHealth = (signal?: AbortSignal) => apiGet<LibraryHealth>('/library-health', signal)
export const searchAgain = (id: string) => request<void>('POST', `/items/${id}/search-again`)

export interface ExternalTitle {
  tmdbId: number
  mediaType: 'movie' | 'tv'
  title: string
  year: number | null
  overview: string | null
  poster: string | null
  backdrop: string | null
  rating: number
  availability: 'None' | 'Requested' | 'PartlyAvailable' | 'Available'
  libraryItemId: string | null
}

export const getRequestStatus = (signal?: AbortSignal) => apiGet<{ configured: boolean; reachable: boolean }>('/requests/status', signal)
export const getExternalDiscover = (signal?: AbortSignal) =>
  apiGet<{ id: string; title: string; items: ExternalTitle[] }[]>('/requests/discover', signal)
export const requestTitle = (mediaType: string, tmdbId: number) => request<void>('POST', '/requests', { mediaType, tmdbId })

export interface Profile {
  id: string
  name: string
  color: string
  hasPin: boolean
  isMain: boolean
  isCurrent: boolean
}

export const getProfiles = (signal?: AbortSignal) => apiGet<Profile[]>('/profiles', signal)
export const createProfile = (profile: { name: string; color: string; pin?: string }) => request<Profile>('POST', '/profiles', profile)
export const updateProfile = (id: string, profile: { name: string; color: string; pin?: string; removePin?: boolean }) =>
  request<Profile>('PUT', `/profiles/${id}`, profile)
export const deleteProfile = (id: string) => request<void>('DELETE', `/profiles/${id}`)

/** Switches to a profile (checking its PIN) and remembers it on this device. */
export async function selectProfile(id: string, pin?: string) {
  const result = await request<{ token: string; profile: Profile }>('POST', `/profiles/${id}/select`, { pin })
  saveProfileToken(result.profile.isMain ? null : result.token)
  return result.profile
}

export type NetworkZone = 'Local' | 'Tailnet' | 'Internet'

export interface Me {
  profileId: string
  method: 'api-key' | 'tailscale' | 'local' | 'none'
  zone: NetworkZone
  isAdmin: boolean
  tailscaleLogin: string | null
}

export interface RemoteAccess {
  yourConnection: NetworkZone
  yourTailscaleLogin: string | null
  tailscale: {
    installed: boolean
    running: boolean
    dnsName: string | null
    ownerLogin: string | null
    privateAccess: boolean
    publicAccess: boolean
    people: { login: string; name: string }[]
    problem: string | null
    url: string | null
  }
  links: { login: string; name: string; profileId: string | null }[]
  cloudflareTunnelRunning: boolean
  mainProfileHasPin: boolean
}

export const getMe = (signal?: AbortSignal) => apiGet<Me>('/me', signal)
export const getRemoteAccess = (signal?: AbortSignal) => apiGet<RemoteAccess>('/remote', signal)
export const setTailscaleAccess = (enabled: boolean) => request<RemoteAccess>('POST', '/remote/tailscale', { enabled })
export const linkTailscale = (login: string, profileId: string | null) => request<RemoteAccess>('POST', '/remote/tailscale/links', { login, profileId })
