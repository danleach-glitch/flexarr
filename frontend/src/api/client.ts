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

  // Any change can show up in any cached answer (a watched tick in a library page, a renamed collection), so a
  // change clears them all, before and after (anything cached while it was under way may predate it).
  const changes = method !== 'GET'
  if (changes) clearApiCache()
  const response = await fetch(path.startsWith('/api/') ? path : `/api/v1${path}`, {
    method,
    signal,
    headers,
    body: body === undefined ? undefined : file ? body : JSON.stringify(body),
  }).finally(() => changes && clearApiCache())

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

// ---------- A small cache for GETs that pages repeat

const FRESH_MS = 30_000
/** Most answers kept at once; the least recently used go first. */
const MAX_ENTRIES = 100
/** How long a shared GET may take before everyone waiting on it is told it failed. */
export const FETCH_TIMEOUT_MS = 30_000

interface Entry {
  at: number
  freshMs: number
  value?: unknown
  pending?: Promise<unknown>
}

const cache = new Map<string, Entry>()

let bypass = false

/** Forgets every cached answer, or just those whose path `matches`. Pages' own state (keepPage) stays. */
export function clearApiCache(matches?: (path: string) => boolean) {
  if (!matches) return cache.clear()
  for (const key of [...cache.keys()]) if (matches(key.slice(key.indexOf(' ') + 1))) cache.delete(key)
}

/** Drops answers past their time, then the least recently used beyond the cap (a Map iterates oldest first). */
function prune() {
  const now = Date.now()
  for (const [key, entry] of cache) if (!entry.pending && now - entry.at > entry.freshMs) cache.delete(key)
  for (const key of cache.keys()) {
    if (cache.size < MAX_ENTRIES) break
    cache.delete(key)
  }
}

// ---------- Pages' own state

const MAX_PAGES = 10
const pages = new Map<string, unknown>()

/**
 * Keeps a page's own state (how far down a long grid you were) for going back to it. It lives apart from the cache,
 * so a change (marking something watched, a progress report) doesn't throw it away: the page squares it with what
 * changed meanwhile. Only the last few pages are kept.
 */
export function keepPage(key: string, value: unknown) {
  const full = `${profileToken() ?? ''} ${key}`
  pages.delete(full)
  pages.set(full, value)
  for (const old of pages.keys()) {
    if (pages.size <= MAX_PAGES) break
    pages.delete(old)
  }
}

export const keptPage = <T>(key: string) => pages.get(`${profileToken() ?? ''} ${key}`) as T | undefined

/** Forgets every page's kept state. */
export function clearKeptPages() {
  pages.clear()
}

/** Runs `load` with its cached GETs fetched afresh (a page reloading on purpose); they must start synchronously. */
export function freshly<T>(load: () => T): T {
  bypass = true
  try {
    return load()
  } finally {
    bypass = false
  }
}

/**
 * A GET that's shared: while one is in flight, asking again joins it rather than sending another, and an answer is
 * reused for `freshMs` (any change through this client clears it first). Aborting stops only this caller's wait; a
 * request the server never answers fails for everyone after FETCH_TIMEOUT_MS, and the next ask starts afresh.
 */
function cachedGet<T>(path: string, signal?: AbortSignal, freshMs = FRESH_MS): Promise<T> {
  // Answers depend on the profile, which a header chooses.
  const key = `${profileToken() ?? ''} ${path}`
  let entry = cache.get(key)
  if (entry && (bypass || (!entry.pending && Date.now() - entry.at > entry.freshMs))) entry = undefined
  if (entry) {
    // Most recently used goes last.
    cache.delete(key)
    cache.set(key, entry)
  } else {
    const fresh: Entry = { at: Date.now(), freshMs }
    const timeout = new AbortController()
    const timer = setTimeout(() => timeout.abort(new DOMException('The server took too long to answer.', 'TimeoutError')), FETCH_TIMEOUT_MS)
    fresh.pending = apiGet<T>(path, timeout.signal)
      .then(
        (value) => {
          // Only keep it if nothing cleared the cache meanwhile.
          if (cache.get(key) === fresh) {
            fresh.value = value
            fresh.at = Date.now()
            fresh.pending = undefined
          }
          return value
        },
        (error: unknown) => {
          if (cache.get(key) === fresh) cache.delete(key)
          throw error
        },
      )
      .finally(() => clearTimeout(timer))
    cache.delete(key)
    prune()
    cache.set(key, fresh)
    entry = fresh
  }
  const answer = (entry.pending ?? Promise.resolve(entry.value)) as Promise<T>
  if (!signal) return answer
  if (signal.aborted) return Promise.reject(new DOMException('Aborted', 'AbortError'))
  return new Promise<T>((resolve, reject) => {
    const onAbort = () => reject(new DOMException('Aborted', 'AbortError'))
    signal.addEventListener('abort', onAbort, { once: true })
    answer.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort))
  })
}

export const getSystemStatus = (signal?: AbortSignal) => apiGet<SystemStatus>('/system/status', signal)

/** CHANGELOG.md as built into the running server. */
export async function getChangelog(signal?: AbortSignal) {
  const response = await fetch('/api/v1/system/changelog', { signal, headers: authHeaders() })
  if (!response.ok) throw new ApiError(response.status, response.statusText, [])
  return response.text()
}

export const getLibraries = (signal?: AbortSignal) => apiGet<Library[]>('/libraries', signal)
/** Every library, including ones hidden from the current profile (Settings; main profile only). */
export const getAllLibraries = (signal?: AbortSignal) => cachedGet<Library[]>('/libraries?all=true', signal)
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
  const path = `/libraries/${id}/items${query}`
  // A letter jump's big pages go once, straight into the grid: caching them would only crowd out everything else.
  return (options.take ?? 0) > 200 ? apiGet<Paged<ItemSummary>>(path, signal) : cachedGet<Paged<ItemSummary>>(path, signal)
}

export const getLibraryGenres = (id: string, signal?: AbortSignal) =>
  cachedGet<{ name: string; count: number }[]>(`/libraries/${id}/genres`, signal)

export const getHome = (signal?: AbortSignal) => cachedGet<Home>('/home', signal)

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
export const getLibraryBrowse = (id: string, signal?: AbortSignal) => cachedGet<Home>(`/libraries/${id}/browse`, signal)
export const getItem = (id: string, signal?: AbortSignal) => cachedGet<ItemDetail>(`/items/${id}`, signal)
/** A title page's detail: a series also brings its Up Next episode and one season (`season`, else the first unwatched). */
export const getItemPage = (id: string, season: string | null, signal?: AbortSignal) =>
  cachedGet<ItemDetail>(`/items/${id}?includeSeason=true${season ? `&season=${encodeURIComponent(season)}` : ''}`, signal)
export const getSimilar = (id: string, signal?: AbortSignal) => cachedGet<ItemSummary[]>(`/items/${id}/similar`, signal)
export const getNextUp = (id: string, signal?: AbortSignal) => cachedGet<ItemSummary | undefined>(`/items/${id}/next-up`, signal)
export const setWatched = (id: string, watched: boolean) => request<void>('POST', `/items/${id}/watched`, { watched })
/** Deletes a film, show or episode and its files through Radarr or Sonarr (main profile only). */
export const deleteItem = (id: string) => request<void>('DELETE', `/items/${id}`)
export const analyzeItem = (id: string) => request<void>('POST', `/items/${id}/analyze`)
export const getPerson = (id: string, signal?: AbortSignal) => cachedGet<Person>(`/people/${id}`, signal)
export const search = (q: string, signal?: AbortSignal) => apiGet<SearchResults>(`/search?q=${encodeURIComponent(q)}`, signal)
export const getGenres = (signal?: AbortSignal) => cachedGet<Genre[]>('/genres', signal)

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

export const getCollections = (signal?: AbortSignal, hidden = false) => cachedGet<CollectionSummary[]>(`/collections${hidden ? '?hidden=true' : ''}`, signal)
export const getCollection = (id: string, signal?: AbortSignal) => cachedGet<CollectionDetail>(`/collections/${id}`, signal)
export const getCollectionSuggestions = (signal?: AbortSignal) => apiGet<CollectionSuggestion[]>('/collections/suggestions', signal)
export const createCollection = (changes: CollectionChanges & { name: string }) => request<CollectionSummary>('POST', '/collections', changes)
export const updateCollection = (id: string, changes: CollectionChanges) => request<CollectionSummary>('PUT', `/collections/${id}`, changes)
export const deleteCollection = (id: string) => request<void>('DELETE', `/collections/${id}`)
export const addToCollection = (id: string, itemId: string) => request<void>('POST', `/collections/${id}/items`, { itemId })
export const removeFromCollection = (id: string, itemId: string) => request<void>('DELETE', `/collections/${id}/items/${itemId}`)
export const reorderCollection = (id: string, itemIds: string[]) => request<void>('PUT', `/collections/${id}/order`, { itemIds })
export const getItemCollections = (itemId: string, signal?: AbortSignal) => cachedGet<ItemCollection[]>(`/items/${itemId}/collections`, signal)
export const getGenreItems = (name: string, kind?: string, signal?: AbortSignal) =>
  apiGet<Paged<ItemSummary>>(`/genres/${encodeURIComponent(name)}/items?take=120${kind ? `&kind=${kind}` : ''}`, signal)

export const startPlayback = (body: PlaybackRequest) => request<PlaybackInfo>('POST', '/playback', body)
export const reportProgress = (sessionId: string, positionSeconds: number, paused: boolean, ended = false) =>
  request<void>('POST', `/playback/${sessionId}/progress`, { positionSeconds, paused, ended })

/** Fire-and-forget progress for page unloads, when fetch may be cancelled. */
export function beaconProgress(sessionId: string, positionSeconds: number) {
  clearApiCache()
  const body = new Blob([JSON.stringify({ positionSeconds, paused: true })], { type: 'application/json' })
  navigator.sendBeacon?.(`/api/v1/playback/${sessionId}/progress`, body)
}

export function stopPlayback(sessionId: string) {
  clearApiCache()
  return fetch(`/api/v1/playback/${sessionId}`, { method: 'DELETE', keepalive: true }).catch(() => undefined)
}

export const getSettings = (signal?: AbortSignal) => cachedGet<Settings>('/settings', signal)
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

/** The widths the server's image cache makes (ArtworkUrl.Widths); other widths snap up to one of these. */
const IMAGE_WIDTHS = [92, 185, 342, 500, 780, 1280, 1920]

/**
 * A srcset for artwork drawn at about half `width` CSS pixels (the usual sizing): `width` for 2x screens, and the
 * cache's next size up from half of it for 1x screens, which then download a quarter of the pixels or so.
 */
export function imageSrcSet(url: string | null | undefined, width: number): string | undefined {
  const half = IMAGE_WIDTHS.find((w) => w >= width / 2) ?? width
  if (!url || half >= width) return undefined
  return `${imageUrl(url, half)} 1x, ${imageUrl(url, width)} 2x`
}

/** A srcset offering each of `widths`, for artwork whose drawn size an img's `sizes` gives (a full-width backdrop). */
export function imageWidths(url: string | null | undefined, widths: number[]): string | undefined {
  if (!url) return undefined
  return widths.map((w) => `${imageUrl(url, w)} ${w}w`).join(', ')
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
  /** Library folders whose files scans are keeping, as the folder looked unmounted or cleared out. */
  held: HeldFolder[]
}

export interface HeldFolder {
  folderId: number
  library: string
  path: string
  reason: 'FolderMissing' | 'FolderEmpty' | 'MostFilesGone'
  /** Files the scans would have hidden, of `known`. */
  missing: number
  known: number
  since: string
  scans: number
  confirmed: boolean
  /** Not for a folder that isn't there at all. */
  canConfirm: boolean
}

/** The held folder's files really are gone: the next scan (started now) hides them. */
export const confirmHeldRemoval = (folderId: number) => request<void>('POST', `/library-health/held/${folderId}/confirm`)

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

export const getProfiles = (signal?: AbortSignal) => cachedGet<Profile[]>('/profiles', signal, 5 * 60_000)
export const createProfile = (profile: Omit<ProfileChanges, 'removePin'>) => request<Profile>('POST', '/profiles', profile)
export const updateProfile = (id: string, profile: ProfileChanges) =>
  request<Profile>('PUT', `/profiles/${id}`, profile)
export const deleteProfile = (id: string) => request<void>('DELETE', `/profiles/${id}`)
export const uploadProfileImage = (id: string, image: Blob) => request<Profile>('PUT', `/profiles/${id}/image`, image)
export const removeProfileImage = (id: string) => request<Profile>('DELETE', `/profiles/${id}/image`)

/** Switches to a profile (checking its PIN) and remembers it on this device. */
export async function selectProfile(id: string, pin?: string) {
  const result = await request<{ token: string; profile: Profile }>('POST', `/profiles/${id}/select`, { pin })
  // The main profile's token is kept when it has a PIN: over Tailscale, it's what lets this browser manage the server.
  saveProfileToken(result.profile.isMain && !result.profile.hasPin ? null : result.token)
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

export const getMe = (signal?: AbortSignal) => cachedGet<Me>('/me', signal, 5 * 60_000)
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
