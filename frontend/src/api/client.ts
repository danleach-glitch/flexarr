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

// Where earlier versions kept the API key itself; it's traded for a session cookie and removed.
const LEGACY_API_KEY_STORAGE = 'flexarr.apiKey'
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

/**
 * Signs this browser in: the API key goes to the server once, and comes back as an HttpOnly session cookie of this
 * browser's own, so the key is never kept anywhere scripts can read it. Local-network and Tailscale visitors may not
 * need this at all.
 */
export async function signIn(key: string) {
  const response = await fetch('/api/v1/session', { method: 'POST', headers: { 'X-Api-Key': key } })
  if (response.status === 401) throw new UnauthorizedError()
  if (!response.ok) throw new ApiError(response.status, response.statusText, [])
}

/** An API key left in storage by an earlier version: swap it for a session cookie, then forget it. */
export async function migrateStoredApiKey() {
  const key = stored(LEGACY_API_KEY_STORAGE)
  if (!key) return
  try {
    await signIn(key)
  } catch (error) {
    // Offline: keep it for next time. Anything else (a wrong key) isn't worth keeping.
    if (error instanceof TypeError) return
  }
  try {
    localStorage.removeItem(LEGACY_API_KEY_STORAGE)
  } catch {
    // Storage unavailable.
  }
}

/** The chosen profile, plus the header that lets the session cookie make changes (another site can't send it). */
function authHeaders(): Record<string, string> {
  const headers: Record<string, string> = { 'X-Requested-With': 'flexarr' }
  const profile = profileToken()
  if (profile) headers['X-Flexarr-Profile'] = profile
  return headers
}

async function request<T>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const headers = authHeaders()
  // Files (profile pictures) go up as they are; everything else is JSON.
  const file = body instanceof Blob
  if (body !== undefined) headers['Content-Type'] = file ? body.type || 'application/octet-stream' : 'application/json'

  const response = await fetch(path.startsWith('/api/') ? path : `/api/v1${path}`, {
    method,
    signal,
    headers,
    body: body === undefined ? undefined : file ? body : JSON.stringify(body),
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

/** CHANGELOG.md as built into the running server. */
export async function getChangelog(signal?: AbortSignal) {
  const response = await fetch('/api/v1/system/changelog', { signal, headers: authHeaders() })
  if (!response.ok) throw new ApiError(response.status, response.statusText, [])
  return response.text()
}

export const getLibraries = (signal?: AbortSignal) => apiGet<Library[]>('/libraries', signal)
/** Every library, including ones hidden from the current profile (Settings; main profile only). */
export const getAllLibraries = (signal?: AbortSignal) => apiGet<Library[]>('/libraries?all=true', signal)
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

export interface RowChoice {
  id: string
  label: string
  enabled: boolean
}

/** Which rows Home and each kind of library's Browse view show, in order. */
export interface Layout {
  home: RowChoice[]
  movies: RowChoice[]
  shows: RowChoice[]
}

export const getLayout = (signal?: AbortSignal) => apiGet<Layout>('/settings/layout', signal)
export const saveLayout = (layout: Layout) => {
  const strip = (rows: RowChoice[]) => rows.map(({ id, enabled }) => ({ id, enabled }))
  return request<void>('PUT', '/settings/layout', { home: strip(layout.home), movies: strip(layout.movies), shows: strip(layout.shows) })
}
/** A library's own home page: hero and rows for just that library. */
export const getLibraryBrowse = (id: string, signal?: AbortSignal) => apiGet<Home>(`/libraries/${id}/browse`, signal)
export const getItem = (id: string, signal?: AbortSignal) => apiGet<ItemDetail>(`/items/${id}`, signal)
export const getSimilar = (id: string, signal?: AbortSignal) => apiGet<ItemSummary[]>(`/items/${id}/similar`, signal)
export const getNextUp = (id: string, signal?: AbortSignal) => apiGet<ItemSummary | undefined>(`/items/${id}/next-up`, signal)
export const setWatched = (id: string, watched: boolean) => request<void>('POST', `/items/${id}/watched`, { watched })
/** Deletes a film, show or episode and its files through Radarr or Sonarr (main profile only). */
export const deleteItem = (id: string) => request<void>('DELETE', `/items/${id}`)
export const analyzeItem = (id: string) => request<void>('POST', `/items/${id}/analyze`)
export const getPerson = (id: string, signal?: AbortSignal) => apiGet<Person>(`/people/${id}`, signal)
export const search = (q: string, signal?: AbortSignal) => apiGet<SearchResults>(`/search?q=${encodeURIComponent(q)}`, signal)
export const getGenres = (signal?: AbortSignal) => apiGet<Genre[]>('/genres', signal)

// ---------- Collections

export type CollectionKind = 'Manual' | 'Smart' | 'Franchise'

/** What a smart collection holds; every rule is optional. */
export interface SmartRules {
  libraryIds?: string[] | null
  type?: 'movie' | 'series' | null
  genres?: string[] | null
  yearFrom?: number | null
  yearTo?: number | null
  thisYear?: boolean
  minRating?: number | null
  watched?: 'watched' | 'unwatched' | null
  addedWithinDays?: number | null
  personId?: string | null
  contains?: string | null
  sort?: 'rating' | 'added' | 'year' | 'title' | 'popularity'
  limit?: number
}

export interface CollectionSummary {
  id: string
  name: string
  description: string | null
  kind: CollectionKind
  personal: boolean
  canEdit: boolean
  itemCount: number
  watchedCount: number
  missingCount: number
  posters: string[]
  poster: string | null
  showOnHome: boolean
  hidden: boolean
}

export interface MissingTitle {
  tmdbId: number
  title: string
  year: number | null
  overview: string | null
  poster: string | null
}

export interface CollectionDetail {
  summary: CollectionSummary
  backdrop: string | null
  items: ItemSummary[]
  missing: MissingTitle[]
  rules: SmartRules | null
}

export interface CollectionSuggestion {
  name: string
  description: string
  rules: SmartRules
  count: number
  posters: string[]
}

export interface ItemCollection {
  id: string
  name: string
  kind: CollectionKind
  personal: boolean
  contains: boolean
  canEdit: boolean
}

export type CollectionChanges = { name?: string; description?: string; personal?: boolean; rules?: SmartRules; showOnHome?: boolean; hidden?: boolean }

export const getCollections = (signal?: AbortSignal, hidden = false) => apiGet<CollectionSummary[]>(`/collections${hidden ? '?hidden=true' : ''}`, signal)
export const getCollection = (id: string, signal?: AbortSignal) => apiGet<CollectionDetail>(`/collections/${id}`, signal)
export const getCollectionSuggestions = (signal?: AbortSignal) => apiGet<CollectionSuggestion[]>('/collections/suggestions', signal)
export const createCollection = (changes: CollectionChanges & { name: string }) => request<CollectionSummary>('POST', '/collections', changes)
export const updateCollection = (id: string, changes: CollectionChanges) => request<CollectionSummary>('PUT', `/collections/${id}`, changes)
export const deleteCollection = (id: string) => request<void>('DELETE', `/collections/${id}`)
export const addToCollection = (id: string, itemId: string) => request<void>('POST', `/collections/${id}/items`, { itemId })
export const removeFromCollection = (id: string, itemId: string) => request<void>('DELETE', `/collections/${id}/items/${itemId}`)
export const reorderCollection = (id: string, itemIds: string[]) => request<void>('PUT', `/collections/${id}/order`, { itemIds })
export const getItemCollections = (itemId: string, signal?: AbortSignal) => apiGet<ItemCollection[]>(`/items/${itemId}/collections`, signal)
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
/** Shows the server's API key (main profile only). A POST, so only Flexarr's own pages can ask. */
export const revealApiKey = () => request<{ apiKey: string }>('POST', '/settings/api-key/reveal', {})
/** Replaces the API key. Browsers and TVs signed in with their own token carry on. */
export const rotateApiKey = () => request<{ apiKey: string }>('POST', '/settings/api-key/rotate', {})
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
export interface PlexAvatarResult {
  matched: { profile: string; plexUser: string }[]
  unmatchedPlexUsers: string[]
}

export const importPlexAvatars = (path?: string) =>
  request<PlexAvatarResult>('POST', `/import/plex/avatars${path ? `?path=${encodeURIComponent(path)}` : ''}`)

export const importFromPlex = (accountId: number, path?: string, profileId?: string) =>
  request<PlexImportResult>('POST', '/import/plex', { accountId, path, profileId })

export const getUpcoming = (days = 21, signal?: AbortSignal) => apiGet<UpcomingItem[]>(`/upcoming?days=${days}`, signal)
export const getDownloads = (signal?: AbortSignal) => apiGet<Download[]>('/downloads', signal)

export interface LibraryHealth {
  /** The share (0–100) of films and episodes with nothing wrong, and how many titles each problem touches. */
  score: { score: number; titles: number; withProblems: number; duplicates: number; unreadable: number; standardDefinition: number; missingArtwork: number; fileProblems: number }
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
  /** Titles Sonarr or Radarr are already downloading, by item id. */
  downloading: Record<string, { progress: number; timeLeft: string | null; status: string | null }>
}

export const getLibraryHealth = (signal?: AbortSignal) => apiGet<LibraryHealth>('/library-health', signal)

export interface HealthCheckReport {
  running: boolean
  checked: number
  total: number
  finishedAt: string | null
  problems: { file: { fileId: string; itemId: string; path: string; problem: 'Missing' | 'Empty' | 'Unreadable'; detail: string | null }; item: ItemSummary | null }[]
}

export const getHealthCheck = (signal?: AbortSignal) => apiGet<HealthCheckReport>('/library-health/check', signal)
export const startHealthCheck = () => request<void>('POST', '/library-health/check')

/** Deletes a title's spare copies from disk, keeping the best and the one Sonarr/Radarr use. */
/** Deletes one chosen copy (never the one Sonarr/Radarr use, or a title's last copy). */
export const deleteCopy = (fileId: string) => request<{ removed: number; freedBytes: number }>('DELETE', `/library-health/files/${fileId}`)

export const removeDuplicates = (itemId: string) =>
  request<{ removed: number; freedBytes: number; skipped: { path: string; reason: string }[] }>('POST', `/items/${itemId}/remove-duplicates`)
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
/** Every film and show TMDB knows (through Seerr), marked when you already have them. */
export const searchEverything = (query: string, signal?: AbortSignal) =>
  apiGet<ExternalTitle[]>(`/requests/search?q=${encodeURIComponent(query)}`, signal)

export interface PairingDetails {
  code: string
  deviceName: string
  expiresAt: string
  /** The address the TV asked from, and which kind of network that is. */
  requestedFrom: string | null
  zone: NetworkZone
}

/** A TV waiting to connect, by the code it shows (main profile only). */
export const getPairing = (code: string, signal?: AbortSignal) => apiGet<PairingDetails>(`/pairing/code/${encodeURIComponent(code.trim())}`, signal)
export const approvePairing = (code: string) => request<void>('POST', `/pairing/code/${encodeURIComponent(code.trim())}/approve`, {})

export const requestTitle = (mediaType: string, tmdbId: number) => request<void>('POST', '/requests', { mediaType, tmdbId })

/** A film or show you don't have, with what Seerr knows about it. */
export interface ExternalDetail {
  tmdbId: number
  mediaType: 'movie' | 'tv'
  title: string
  tagline: string | null
  year: number | null
  overview: string | null
  poster: string | null
  backdrop: string | null
  genres: string[]
  runtimeMinutes: number | null
  seasons: number | null
  certification: string | null
  status: string | null
  tmdbRating: number | null
  /** Rotten Tomatoes, percent. */
  criticScore: number | null
  audienceScore: number | null
  imdbRating: number | null
  trailerYouTubeId: string | null
  cast: { name: string; character: string | null; image: string | null }[]
  /** Directors (films) or creators (shows). */
  madeBy: string[]
  studio: string | null
  availability: ExternalTitle['availability']
  libraryItemId: string | null
  recommendations: ExternalTitle[]
}

export const getExternalTitle = (mediaType: string, tmdbId: string, signal?: AbortSignal) =>
  apiGet<ExternalDetail>(`/requests/title/${mediaType}/${tmdbId}`, signal)

export interface Profile {
  id: string
  name: string
  color: string
  hasPin: boolean
  isMain: boolean
  isCurrent: boolean
  /** Libraries this profile can't see anywhere. */
  hiddenLibraryIds: string[]
  /** Profile picture URL; null shows the initial on the profile's colour. */
  image: string | null
}

type ProfileChanges = { name: string; color: string; pin?: string; removePin?: boolean; hiddenLibraryIds?: string[] }

export const getProfiles = (signal?: AbortSignal) => apiGet<Profile[]>('/profiles', signal)
export const createProfile = (profile: Omit<ProfileChanges, 'removePin'>) => request<Profile>('POST', '/profiles', profile)
export const updateProfile = (id: string, profile: ProfileChanges) =>
  request<Profile>('PUT', `/profiles/${id}`, profile)
export const deleteProfile = (id: string) => request<void>('DELETE', `/profiles/${id}`)
export const uploadProfileImage = (id: string, image: Blob) => request<Profile>('PUT', `/profiles/${id}/image`, image)
export const removeProfileImage = (id: string) => request<Profile>('DELETE', `/profiles/${id}/image`)

/** Switches to a profile (checking its PIN) and remembers it on this device. */
export async function selectProfile(id: string, pin?: string) {
  const result = await request<{ token: string; profile: Profile }>('POST', `/profiles/${id}/select`, { pin })
  saveProfileToken(result.profile.isMain ? null : result.token)
  return result.profile
}

export type NetworkZone = 'Local' | 'Tailnet' | 'Internet'

export interface Me {
  profileId: string
  /** api-key: the server's key; session: this browser's sign-in; device: an app's own token. */
  method: 'api-key' | 'session' | 'device' | 'tailscale' | 'local' | 'none'
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

/** A browser or TV signed in to this server with its own token. */
export interface SignIn {
  id: string
  kind: 'Browser' | 'Device'
  name: string
  createdAt: string
  lastUsedAt: string
  zone: NetworkZone
  address: string | null
  profileId: string
  profileName: string | null
  /** The browser you're using now. */
  isThisOne: boolean
}

export const getSignIns = (signal?: AbortSignal) => apiGet<SignIn[]>('/sign-ins', signal)
export const revokeSignIn = (id: string) => request<void>('DELETE', `/sign-ins/${id}`)

/** Signs this browser out: the server forgets its session, and the profile chosen here is forgotten too. */
export async function signOut() {
  await request<void>('DELETE', '/session')
  saveProfileToken(null)
  try {
    sessionStorage.removeItem('flexarr.profileChosen')
  } catch {
    // Storage unavailable.
  }
}
