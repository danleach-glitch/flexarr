import type {
  Activity,
  LiveChannel,
  LiveGuide,
  LiveTvStatus,
  LiveTvTest,
  LiveWatch,
  Recording,
  Recordings,
  RecordingSeries,
  ArrStatus,
  DirectoryListing,
  DirectoryPodcast,
  Download,
  Genre,
  Home,
  ItemDetail,
  ItemSummary,
  Library,
  LibraryRequest,
  Loudness,
  Lyrics,
  ClientLogEntry,
  LogPage,
  LogQuery,
  LogSettings,
  LogSettingsUpdate,
  LogUpload,
  MixDetail,
  MixSummary,
  OpmlImport,
  Paged,
  Person,
  PlaybackInfo,
  PlaybackPreferences,
  PlaybackRequest,
  PlaylistDetail,
  PlaylistSummary,
  Podcast,
  PodcastCountry,
  PodcastEpisode,
  PodcastGenre,
  PodcastPreferences,
  PodcastStatus,
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

/**
 * A non-2xx response. `messages` holds validation errors from ASP.NET problem details, if any; `retryAfterSeconds`, how
 * long the server asked to wait (too many wrong PINs), when it said.
 */
export class ApiError extends Error {
  readonly status: number
  readonly messages: string[]
  readonly retryAfterSeconds?: number
  /** The server's problem as it sent it, with any details of its own (a recording's id, say). */
  readonly problem?: Record<string, unknown>

  constructor(status: number, statusText: string, messages: string[], retryAfterSeconds?: number, problem?: Record<string, unknown>) {
    super(messages[0] ?? `${status} ${statusText}`)
    this.status = status
    this.messages = messages
    this.retryAfterSeconds = retryAfterSeconds
    this.problem = problem
  }
}

/** Seconds from a number or a whole-seconds string, if it is one. */
function seconds(value: unknown): number | undefined {
  const n = typeof value === 'number' ? value : typeof value === 'string' && /^\d+$/.test(value.trim()) ? Number(value) : NaN
  return Number.isFinite(n) && n >= 0 ? n : undefined
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
 * Shows the profile picker on the next load, even over Tailscale or with one profile. The profile chosen here stays
 * until another is picked, so backing out of the picker changes nothing. With `profileId`, the picker goes straight to
 * that profile (its PIN).
 */
export function askForProfile(profileId?: string) {
  try {
    sessionStorage.removeItem('flexarr.profileChosen')
    sessionStorage.setItem('flexarr.pickProfile', '1')
    if (profileId) sessionStorage.setItem('flexarr.pickProfileFor', profileId)
    else sessionStorage.removeItem('flexarr.pickProfileFor')
  } catch {
    // Storage unavailable.
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

/** Whether this browser can set up a new server: nobody has signed in yet, and it's on the home network. */
export async function setupOpen(): Promise<boolean> {
  try {
    const response = await fetch('/api/v1/setup')
    return response.ok && ((await response.json()) as { open: boolean }).open
  } catch {
    return false
  }
}

/** Sets up a new server from this browser, which becomes its owner and is signed in. */
export async function claimSetup() {
  const response = await fetch('/api/v1/setup/claim', { method: 'POST', headers: { 'X-Requested-With': 'flexarr' } })
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
export function authHeaders(): Record<string, string> {
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

  if (!response.ok) throw await failure(response)
  if (response.status === 202 || response.status === 204) {
    return undefined as T
  }
  return JSON.parse(await response.text(), asUtc) as T
}

/** Why a request was refused: not signed in, or the server's problem with it. */
async function failure(response: Response): Promise<Error> {
  if (response.status === 401) return new UnauthorizedError()
  let messages: string[] = []
  let retryAfter = seconds(response.headers.get('Retry-After'))
  let details: Record<string, unknown> | undefined
  try {
    const problem = (await response.json()) as { errors?: Record<string, string[]>; title?: string; detail?: string; retryAfterSeconds?: unknown }
    messages = problem.errors ? Object.values(problem.errors).flat() : [problem.detail ?? problem.title].filter((m): m is string => !!m)
    retryAfter = seconds(problem.retryAfterSeconds) ?? retryAfter
    details = problem as Record<string, unknown>
  } catch {
    // Not a JSON problem response.
  }
  return new ApiError(response.status, response.statusText, messages, retryAfter, details)
}

/** Asks for a file (a backup, a settings file) with a POST, and saves it to the browser's downloads under the server's name for it. */
async function download(path: string, body: unknown, fallbackName: string) {
  const response = await fetch(`/api/v1${path}`, { method: 'POST', headers: { ...authHeaders(), 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  if (!response.ok) throw await failure(response)
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const name = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1] ?? /filename="?([^";]+)"?/i.exec(disposition)?.[1]
  const url = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = url
  link.download = name ? decodeURIComponent(name) : fallbackName
  document.body.append(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

/** A date and time with no zone: the server's times are all UTC, but older servers sent them without saying so. */
const zoneless = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$/

/** Reads zoneless times as UTC, so "scanned 4 h ago" isn't out by the browser's time zone. */
const asUtc = (_key: string, value: unknown) => (typeof value === 'string' && zoneless.test(value) ? `${value}Z` : value)

export const apiGet = <T>(path: string, signal?: AbortSignal) => request<T>('GET', path, undefined, signal)

// ---------- A small cache for GETs that pages repeat

const FRESH_MS = 30_000
/** Most answers kept at once; the least recently used go first. */
const MAX_ENTRIES = 100
/** How long a shared GET may take before everyone waiting on it is told it failed. */
export const FETCH_TIMEOUT_MS = 30_000

/** A GET that fails after FETCH_TIMEOUT_MS if the server hasn't answered, or as soon as `signal` aborts. */
function timedGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const timeout = new AbortController()
  const timer = setTimeout(() => timeout.abort(new DOMException('The server took too long to answer.', 'TimeoutError')), FETCH_TIMEOUT_MS)
  const onAbort = () => timeout.abort(signal?.reason)
  if (signal?.aborted) onAbort()
  else signal?.addEventListener('abort', onAbort, { once: true })
  return apiGet<T>(path, timeout.signal).finally(() => {
    clearTimeout(timer)
    signal?.removeEventListener('abort', onAbort)
  })
}

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

/** Forgets one page's kept state. */
export function forgetPage(key: string) {
  pages.delete(`${profileToken() ?? ''} ${key}`)
}

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
    fresh.pending = timedGet<T>(path).then(
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
  return (options.take ?? 0) > 200 ? timedGet<Paged<ItemSummary>>(path, signal) : cachedGet<Paged<ItemSummary>>(path, signal)
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
  /** Home shows one Latest unwatched row for all the film libraries, and one for all the TV ones. */
  combineLibraries: boolean
  /** There are two film libraries or two TV ones, so combining them changes anything. */
  combinable: boolean
}

export const getLayout = (signal?: AbortSignal) => apiGet<Layout>('/settings/layout', signal)
export const saveLayout = (layout: Layout) => {
  const strip = (rows: RowChoice[]) => rows.map(({ id, enabled }) => ({ id, enabled }))
  // Answers with the rows as saved, which change when combining is turned on or off.
  return request<Layout>('PUT', '/settings/layout', {
    home: strip(layout.home),
    movies: strip(layout.movies),
    shows: strip(layout.shows),
    combineLibraries: layout.combineLibraries,
  })
}
/** A library's own home page: hero and rows for just that library. */
export const getLibraryBrowse = (id: string, signal?: AbortSignal) => cachedGet<Home>(`/libraries/${id}/browse`, signal)
export const getItem = (id: string, signal?: AbortSignal) => cachedGet<ItemDetail>(`/items/${id}`, signal)
/** A title page's detail: a series also brings its Up Next episode and one season (`season`, else the first unwatched). */
export const getItemPage = (id: string, season: string | null, signal?: AbortSignal) =>
  cachedGet<ItemDetail>(`/items/${id}?includeSeason=true${season ? `&season=${encodeURIComponent(season)}` : ''}`, signal)
export const getSimilar = (id: string, signal?: AbortSignal) => cachedGet<ItemSummary[]>(`/items/${id}/similar`, signal)
/** Every track of an artist (or album): albums oldest first, then disc and track order. */
export const getTracks = (id: string, signal?: AbortSignal) => cachedGet<ItemSummary[]>(`/items/${id}/tracks`, signal)
/** A track's lyrics, or null when it has none: the server answers 404, which here is an answer rather than a failure. */
export const getLyrics = (id: string, signal?: AbortSignal) =>
  cachedGet<Lyrics>(`/items/${id}/lyrics`, signal).catch((error: unknown) => {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  })

/** A track's ReplayGain, or null when the server has no file for it (404). Tags it lacks come back as nulls inside. */
export const getLoudness = (id: string, signal?: AbortSignal) =>
  cachedGet<Loudness>(`/items/${id}/loudness`, signal).catch((error: unknown) => {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  })

/** Artist radio: about fifty tracks seeded from the artist. Not cached, so every press is a new station. */
export const getRadio = (id: string, signal?: AbortSignal) => apiGet<ItemSummary[]>(`/items/${id}/radio`, signal)
export const getNextUp = (id: string, signal?: AbortSignal) => cachedGet<ItemSummary | undefined>(`/items/${id}/next-up`, signal)
export const setWatched = (id: string, watched: boolean) => request<void>('POST', `/items/${id}/watched`, { watched })
/** Takes a title out of Continue Watching without marking it watched; for an episode, its show stays out until another is played. */
export const removeFromContinueWatching = (id: string) => request<void>('DELETE', `/home/continue/${id}`)
const deleted = new Set<string>()
/** What deleting a title does: Radarr or Sonarr delete it, or when neither manages it, Flexarr deletes these files. */
export interface DeletionPlan {
  managedBy: string | null
  files: string[]
}
export const getDeletion = (id: string, signal?: AbortSignal) => apiGet<DeletionPlan>(`/items/${id}/deletion`, signal)
/** Deletes a film, show or episode and its files, through Radarr or Sonarr when they manage it (main profile only). */
export async function deleteItem(id: string) {
  await request<void>('DELETE', `/items/${id}`)
  deleted.add(id)
}
/** Whether `id` was deleted from this browser (a page kept for going back may still show it). */
export const wasDeleted = (id: string) => deleted.has(id)
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
// ---------- Playlists (music; each profile's own)

export const getPlaylists = (signal?: AbortSignal) => cachedGet<PlaylistSummary[]>('/playlists', signal)
export const getPlaylist = (id: string, signal?: AbortSignal) => cachedGet<PlaylistDetail>(`/playlists/${id}`, signal)
/** Starts a playlist, with tracks, albums or artists in it if given (albums and artists bring all their tracks). */
export const createPlaylist = (name: string, itemIds: string[] = []) => request<PlaylistSummary>('POST', '/playlists', { name, itemIds })
export const renamePlaylist = (id: string, name: string) => request<PlaylistSummary>('PUT', `/playlists/${id}`, { name })
/** An album's or track's cover for the playlist, or (null) back to the mosaic of its tracks' covers. */
export const setPlaylistCover = (id: string, coverItemId: string | null) =>
  request<PlaylistSummary>('PUT', `/playlists/${id}`, coverItemId ? { coverItemId } : { clearCover: true })
export const deletePlaylist = (id: string) => request<void>('DELETE', `/playlists/${id}`)
/** Appends tracks, albums or artists; `skipDuplicates` leaves out tracks the playlist already has. */
export const addToPlaylist = (id: string, itemIds: string[], skipDuplicates = false) =>
  request<{ added: number; summary: PlaylistSummary }>('POST', `/playlists/${id}/items`, { itemIds, skipDuplicates })
export const removeFromPlaylist = (id: string, entryId: number) => request<void>('DELETE', `/playlists/${id}/items/${entryId}`)
/** Moves an entry to `position` (from 0; past the end means last). */
export const movePlaylistEntry = (id: string, entryId: number, position: number) =>
  request<void>('PUT', `/playlists/${id}/items/${entryId}/position`, { position })

// ---------- Made for you (daily music mixes, each profile's own, computed by the server)

export const getMixes = (signal?: AbortSignal) => cachedGet<MixSummary[]>('/music/mixes', signal)
export const getMix = (id: string, signal?: AbortSignal) => cachedGet<MixDetail>(`/music/mixes/${encodeURIComponent(id)}`, signal)

export const getGenreItems = (name: string, kind?: string, signal?: AbortSignal) =>
  apiGet<Paged<ItemSummary>>(`/genres/${encodeURIComponent(name)}/items?take=120${kind ? `&kind=${kind}` : ''}`, signal)

// ---------- Live TV

export const getLiveTvStatus = (signal?: AbortSignal) => cachedGet<LiveTvStatus>('/livetv/status', signal)
/** What's on now and next changes by the minute, so it's never served from the cache. */
export const getLiveChannels = (signal?: AbortSignal) => apiGet<LiveChannel[]>('/livetv/channels', signal)
export const getLiveGuide = (from: Date, hours: number, signal?: AbortSignal) =>
  apiGet<LiveGuide>(`/livetv/guide?from=${encodeURIComponent(from.toISOString())}&hours=${hours}`, signal)
/** Starts watching a channel; `replaces` is the viewer's previous channel, let go first so its connection is free. */
export const watchLiveChannel = (id: string, replaces?: string) => request<LiveWatch>('POST', `/livetv/channels/${id}/watch`, { replaces })

// ---------- Recordings (the DVR): the household's, so anyone can schedule, stop or delete them

export const getRecordings = (signal?: AbortSignal) => apiGet<Recordings>('/livetv/recordings', signal)
export const recordProgramme = (programmeId: number) => request<Recording>('POST', '/livetv/recordings', { programmeId })
/** A scheduled one comes off the list; one recording now stops (keeping what it has); a finished one is deleted. */
export const removeRecording = (id: string) => request<void>('DELETE', `/livetv/recordings/${id}`)
export const recordSeries = (programmeId: number, options: { anyChannel?: boolean; newOnly?: boolean; keepLatest?: number } = {}) =>
  request<RecordingSeries>('POST', '/livetv/recordings/series', { programmeId, ...options })
export const updateSeries = (id: string, changes: { anyChannel?: boolean; newOnly?: boolean; keepLatest?: number }) =>
  request<RecordingSeries>('PUT', `/livetv/recordings/series/${id}`, changes)
/** The series stops: its scheduled showings come off the list; what it recorded stays. */
export const removeSeries = (id: string) => request<void>('DELETE', `/livetv/recordings/series/${id}`)
export const liveHeartbeat = (viewer: string) => request<void>('POST', `/livetv/watch/${viewer}/heartbeat`)
export const stopLiveChannel = (viewer: string) => request<void>('DELETE', `/livetv/watch/${viewer}/`)
export const refreshLiveTv = () => request<void>('POST', '/livetv/refresh')
export const setFavouriteChannel = (id: string, favourite: boolean) => request<void>(favourite ? 'PUT' : 'DELETE', `/livetv/channels/${id}/favourite`)
export const testLiveTv = (source: { baseUrl: string; username: string; password?: string }) => request<LiveTvTest>('POST', '/settings/livetv/test', source)

// ---------- Podcasts

export const getPodcastStatus = (signal?: AbortSignal) => cachedGet<PodcastStatus>('/podcasts/status', signal)
/** The shows this profile follows, the most recently updated first. */
export const getPodcasts = (signal?: AbortSignal) => cachedGet<Podcast[]>('/podcasts', signal)
export const getPodcast = (id: string, signal?: AbortSignal) => cachedGet<Podcast>(`/podcasts/${id}`, signal)
/** Adds a show to the library (nobody follows it by that): from Apple's directory, or by its feed's address. */
export const addPodcast = (show: { iTunesId: number } | { feedUrl: string }) => request<Podcast>('POST', '/podcasts', show)
export const deletePodcast = (id: string) => request<void>('DELETE', `/podcasts/${id}`)
export const refreshPodcast = (id: string) => request<Podcast>('POST', `/podcasts/${id}/refresh`)
/** Follows a show; `oldestFirst` sets the order it's heard in (left out, it stays as it was). */
export const subscribePodcast = (id: string, changes: { oldestFirst?: boolean } = {}) => request<Podcast>('PUT', `/podcasts/${id}/subscription`, changes)
export const unsubscribePodcast = (id: string) => request<void>('DELETE', `/podcasts/${id}/subscription`)

export function getPodcastEpisodes(
  id: string,
  options: { offset?: number; limit?: number; sort?: 'newest' | 'oldest'; unplayed?: boolean; q?: string },
  signal?: AbortSignal,
) {
  const query = new URLSearchParams()
  if (options.offset) query.set('offset', String(options.offset))
  if (options.limit) query.set('limit', String(options.limit))
  if (options.sort === 'oldest') query.set('sort', 'oldest')
  if (options.unplayed) query.set('unplayed', 'true')
  if (options.q) query.set('q', options.q)
  return cachedGet<Paged<PodcastEpisode>>(`/podcasts/${id}/episodes?${query}`, signal)
}

export const getPodcastEpisode = (id: string, signal?: AbortSignal) => cachedGet<PodcastEpisode>(`/podcasts/episodes/${id}`, signal)
/** What auto-play goes on to after an episode: the show's next, else the newest unplayed of the shows followed. */
export const getNextEpisode = (id: string) => apiGet<PodcastEpisode | undefined>(`/podcasts/episodes/${id}/next`)
/** Up Next: the podcast episodes to hear after this one, the same on every device. */
export const getUpNext = (signal?: AbortSignal) => cachedGet<PodcastEpisode[]>('/podcasts/queue', signal)
/** Puts an episode in Up Next: first (`next`), or after the rest. */
export const addToUpNext = (episodeId: string, next = false) => request<PodcastEpisode[]>('POST', '/podcasts/queue', { episodeId, next })
export const reorderUpNext = (episodeIds: string[]) => request<PodcastEpisode[]>('PUT', '/podcasts/queue', { episodeIds })
export const removeFromUpNext = (episodeId: string) => request<void>('DELETE', `/podcasts/queue/${episodeId}`)
export const getPodcastPreferences = (signal?: AbortSignal) => cachedGet<PodcastPreferences>('/podcasts/preferences', signal)
export const savePodcastPreferences = (changes: Partial<PodcastPreferences>) => request<PodcastPreferences>('PUT', '/podcasts/preferences', changes)
export const getNewEpisodes = (signal?: AbortSignal, unplayed = false) =>
  cachedGet<PodcastEpisode[]>(`/podcasts/new${unplayed ? '?unplayed=true' : ''}`, signal)
export const getContinueListening = (signal?: AbortSignal) => cachedGet<PodcastEpisode[]>('/podcasts/continue', signal)
export const searchPodcasts = (q: string, signal?: AbortSignal) => cachedGet<DirectoryPodcast[]>(`/podcasts/search?q=${encodeURIComponent(q)}`, signal)
export const getTopPodcasts = (genre: number | null, signal?: AbortSignal) =>
  cachedGet<DirectoryPodcast[]>(`/podcasts/top${genre ? `?genre=${genre}` : ''}`, signal, 10 * 60_000)
export const getPodcastGenres = (signal?: AbortSignal) => cachedGet<PodcastGenre[]>('/podcasts/genres', signal, 60 * 60_000)
export const getPodcastCountries = (signal?: AbortSignal) => cachedGet<PodcastCountry[]>('/podcasts/countries', signal, 60 * 60_000)
export const importOpml = (file: Blob) => request<OpmlImport>('POST', '/podcasts/opml', file.type ? file : new Blob([file], { type: 'text/x-opml' }))
export const opmlUrl = '/api/v1/podcasts/opml'

export const startPlayback = (body: PlaybackRequest) => request<PlaybackInfo>('POST', '/playback', body)
export const getPlaybackPreferences = (signal?: AbortSignal) => apiGet<PlaybackPreferences>('/me/playback-preferences', signal)
export const setPlaybackPreferences = (preferences: PlaybackPreferences) => request<PlaybackPreferences>('PUT', '/me/playback-preferences', preferences)
export const setSubtitleDelay = (itemId: string, seconds: number) => request<void>('PUT', `/items/${itemId}/subtitle-delay`, { seconds })
/** The subtitles the viewer picked in the player (a track's key, or null for off), remembered for the title and its show. */
export const setSubtitleChoice = (sessionId: string, key: string | null) => request<void>('PUT', `/playback/${sessionId}/subtitle`, { key: key ?? 'off' })
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
/** The server asks again: the main profile's PIN when it has one, otherwise `confirm`. */
export interface KeyConfirmation {
  pin?: string
  confirm?: boolean
}
export const revealApiKey = (confirmation: KeyConfirmation) => request<{ apiKey: string }>('POST', '/settings/api-key/reveal', confirmation)
/** Replaces the API key. Browsers and TVs signed in with their own token carry on. */
export const rotateApiKey = (confirmation: KeyConfirmation & { signOutOthers?: boolean }) =>
  request<{ apiKey: string; signedOut: number }>('POST', '/settings/api-key/rotate', confirmation)
/** Replaces the secret in the webhook addresses; Sonarr, Radarr and Lidarr need the new ones. */
export const rotateWebhookSecret = () => request<{ webhookSecret: string }>('POST', '/settings/webhook-secret/rotate', {})
export const getArrStatus = (signal?: AbortSignal) => apiGet<ArrStatus[]>('/arr/status', signal)
export const testArr = (connection: { name: string; kind: string; baseUrl: string; apiKey?: string }) =>
  request<ArrStatus>('POST', '/arr/test', connection)
export const getActivity = (signal?: AbortSignal) => apiGet<Activity>('/system/activity', signal)

export const browseFilesystem = (path?: string, signal?: AbortSignal) =>
  apiGet<DirectoryListing>(`/filesystem${path ? `?path=${encodeURIComponent(path)}` : ''}`, signal)
/** What the Mac app does itself. Only there: elsewhere the server answers 404. */
export interface MacApp {
  /** NeedsApproval: turned on, but macOS waits for it to be allowed in System Settings › General › Login Items. */
  startAtLogin: 'On' | 'Off' | 'NeedsApproval'
}
export const getMacApp = (signal?: AbortSignal) => apiGet<MacApp>('/mac-app', signal)
export const setStartAtLogin = (enabled: boolean) => request<MacApp>('PUT', '/mac-app/start-at-login', { enabled })
/** In the Mac app: opens macOS's folder window on the Mac, in `start` if given (~ is home). Undefined when cancelled. */
export const chooseFolderOnMac = (start?: string) =>
  request<{ path: string } | undefined>('POST', `/filesystem/choose${start ? `?start=${encodeURIComponent(start)}` : ''}`)

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

// ---------- Backups

export type BackupKind = 'Manual' | 'Nightly' | 'PreUpgrade' | 'PreRestore'
export interface Backup {
  name: string
  kind: BackupKind
  createdAt: string
  size: number
  flexarrVersion: string | null
}
export interface Backups {
  /** Where they're kept now. */
  folder: string
  /** The folder chosen in Settings; null for the default, in the data folder. */
  folderSetting: string | null
  nightly: boolean
  keepNightly: number
  backups: Backup[]
  /** Why the folder can't be used now (a share that isn't mounted), if it can't. */
  folderProblem: string | null
}
export interface BackupContents {
  createdAt: string
  kind: BackupKind
  flexarrVersion: string
  profiles: number
  libraries: number
  titles: number
  /** Why it can't be restored here, if it can't. */
  problem: string | null
}
export interface SettingsFileLibrary {
  name: string
  type: import('./types').LibraryType
  folders: { path: string; exists: boolean }[]
  alreadyHere: boolean
}
export interface SettingsFile {
  exportedAt: string | null
  flexarrVersion: string | null
  sections: string[]
  libraries: SettingsFileLibrary[]
}
/** A backup or settings file brought in from this browser; `kind` is null while it waits for its passphrase. */
export interface BackupUpload {
  id: string
  encrypted: boolean
  kind: 'backup' | 'settings' | null
  backup: BackupContents | null
  settings: SettingsFile | null
  /** Made by a different Flexarr server: restoring it brings that server's keys and sign-ins. */
  fromAnotherServer: boolean
}
export interface SettingsImportResult {
  applied: string[]
  librariesAdded: number
  problems: string[]
}
export const getBackups = (signal?: AbortSignal) => apiGet<Backups>('/backups', signal)
export const saveBackupSettings = (update: { folder?: string; nightly?: boolean }) => request<Backups>('PUT', '/backups/settings', update)
export const backUpNow = () => request<Backup>('POST', '/backups')
export const deleteBackup = (name: string) => request<void>('DELETE', `/backups/${encodeURIComponent(name)}`)
/**
 * Saves a backup to this computer, protected with `passphrase` when there is one. Without one it's a plain link, so the
 * browser writes it straight to disk rather than holding it all in the page first.
 */
export async function downloadBackup(name: string, passphrase?: string) {
  if (passphrase) return download(`/backups/${encodeURIComponent(name)}/download`, { passphrase }, name)
  const link = document.createElement('a')
  link.href = `/api/v1/backups/${encodeURIComponent(name)}/download`
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
}
export const exportSettings = (passphrase?: string) => download('/backups/settings-export', { passphrase }, 'flexarr-settings.json')
/** Puts a backup kept on the server back in place; the server restarts to do it. */
export const restoreBackup = (name: string, confirmation: KeyConfirmation) =>
  request<void>('POST', `/backups/${encodeURIComponent(name)}/restore`, confirmation)
export const uploadBackupFile = (file: Blob) => request<BackupUpload>('POST', '/backups/uploads', file.type ? file : new Blob([file], { type: 'application/octet-stream' }))
export const openBackupFile = (id: string, passphrase: string) => request<BackupUpload>('POST', `/backups/uploads/${id}/open`, { passphrase })
export const restoreBackupFile = (id: string, confirmation: KeyConfirmation) => request<void>('POST', `/backups/uploads/${id}/restore`, confirmation)
export const importSettingsFile = (id: string, choice: { sections: string[]; libraries: { name: string; folders: string[] }[] } & KeyConfirmation) =>
  request<SettingsImportResult>('POST', `/backups/uploads/${id}/import`, choice)
export const forgetBackupFile = (id: string) => request<void>('DELETE', `/backups/uploads/${id}`)

// ---------- Updates

/** How the server was installed: a Mac install updates itself; Docker and the rest are updated by hand. */
export type InstallKind = 'MacLaunchAgent' | 'Docker' | 'MacApp' | 'Other'
export type UpdateStage = 'Idle' | 'Downloading' | 'Installing'
export interface Updates {
  version: string
  /** A build between releases: never updated from here. */
  isDevelopment: boolean
  install: InstallKind
  canInstall: boolean
  /** Notes are CHANGELOG.md's Markdown for that version, without its heading. */
  latest: { version: string; notes: string; url: string; publishedAt: string | null } | null
  available: boolean
  lastChecked: string | null
  checkError: string | null
  automatic: boolean
  /** `downloading` says what: "Flexarr 0.51.0", then "the latest .NET runtime" when the update needs it. */
  progress: { stage: UpdateStage; version: string | null; received: number; total: number | null; error: string | null; downloading?: string | null }
  lastResult: { from: string; to: string; ok: boolean; message: string; at: string } | null
  /** What installing now would interrupt, in words. */
  inUse: string[]
}

export const getUpdates = (signal?: AbortSignal) => apiGet<Updates>('/updates', signal)
export const checkForUpdates = () => request<Updates>('POST', '/updates/check')
export const saveUpdateSettings = (update: { automatic: boolean }) => request<Updates>('PUT', '/updates/settings', update)
export const installUpdate = () => request<Updates>('POST', '/updates/install')

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
  score: {
    score: number
    titles: number
    withProblems: number
    duplicates: number
    unreadable: number
    standardDefinition: number
    missingArtwork: number
    fileProblems: number
    /** Films and shows without artwork, left out of the score: no Radarr or Sonarr is connected to fill it in. */
    artworkNeedsConnection?: number
  }
  duplicates: {
    item: import('./types').ItemSummary
    seriesTitle: string | null
    files: { id: string; path: string; size: number; resolution: string | null; videoCodec: string | null; bitrate: number | null; keep: boolean }[]
    reclaimableBytes: number
  }[]
  reclaimableBytes: number
  unreadable: { fileId: string; path: string; error: string; item: import('./types').ItemSummary | null }[]
  missingArtwork: import('./types').ItemSummary[]
  /** Films and shows Sonarr and Radarr don't manage, matched by searching for the folder name. */
  matchedByTitle: import('./types').ItemSummary[]
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
  libraryType?: import('./types').LibraryType | null
}

/**
 * The held folder's files really are gone: the next scan (started now) hides them. Sends the numbers shown, so it's
 * refused if a scan has found something different since.
 */
export const confirmHeldRemoval = (f: HeldFolder) =>
  request<void>('POST', `/library-health/held/${f.folderId}/confirm`, { since: f.since, missing: f.missing, known: f.known })

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

/** A show (TVDB id) or film (TMDB id) a library item could be, from Sonarr's or Radarr's search. */
export interface MatchCandidate {
  id: number
  title: string
  year: number | null
  overview: string | null
  poster: string | null
  network: string | null
  /** What the item is matched to now. */
  current: boolean
  /** The Sonarr or Radarr instance that already has it, and its folder there; null if it would be added. */
  managedBy: string | null
  managedPath: string | null
}

export const searchMatches = (itemId: string, q: string, signal?: AbortSignal) =>
  apiGet<MatchCandidate[]>(`/items/${itemId}/match?q=${encodeURIComponent(q)}`, signal)

/** Matches the item to a show or film for good, adding it to Sonarr or Radarr (unmonitored, no search) if they don't have it. */
export const fixMatch = (itemId: string, id: number) =>
  request<{ instance: string; added: boolean; complete: boolean }>('POST', `/items/${itemId}/match`, { id })

/** Back to automatic matching. */
export const resetMatch = (itemId: string) => request<void>('DELETE', `/items/${itemId}/match`)
/** Keeps the match a title search found (Library health's Ignore): refreshes leave it as it is. */
export const keepMatch = (itemId: string) => request<void>('POST', `/items/${itemId}/match/keep`)

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
/** Not now, or not my TV: the code is dropped and the TV shows a new one. */
export const declinePairing = (code: string) => request<void>('POST', `/pairing/code/${encodeURIComponent(code.trim())}/decline`, {})

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

/** Changing or removing the main profile's PIN takes `currentPin`, or `apiKey` if it's forgotten. */
type ProfileChanges = { name: string; color: string; pin?: string; removePin?: boolean; hiddenLibraryIds?: string[]; currentPin?: string; apiKey?: string }

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
  // The main profile needs no token: it's the default, and over Tailscale its PIN signs this browser in instead (an
  // HttpOnly cookie). Its token is the same in every browser, so it isn't kept where scripts can read it.
  saveProfileToken(result.profile.isMain ? null : result.token)
  // This browser may have just been signed in, which the cache can't tell from the token: ask afresh.
  clearApiCache()
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
  /** Flexarr runs in a container (Docker), which can't reach the host's Tailscale. */
  inContainer: boolean
  /** The Mac app, whose sandbox can't run the tailscale command. */
  inMacApp?: boolean
  /** In the Mac app: this Mac's address on a tailnet, if it's on one. */
  tailnetAddress?: string | null
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
export const renameSignIn = (id: string, name: string) => request<void>('PATCH', `/sign-ins/${id}`, { name })
/** Signs out every browser and TV but this one; returns how many. */
export const signOutOthers = () => request<{ signedOut: number }>('POST', '/sign-ins/sign-out-others', {})

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

// ---------- Logs (the server's, for the main profile)

/** `?level=…&area=…` from whichever of them are set. */
function logQuery(query: LogQuery) {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) if (value !== undefined && value !== '') params.set(key, String(value))
  const text = params.toString()
  return text ? `?${text}` : ''
}

export const getLogs = (query: LogQuery, signal?: AbortSignal) => apiGet<LogPage>(`/logs${logQuery(query)}`, signal)
/** Server-sent events, one LogEntry each, from now on. EventSource sends the session cookie itself. */
export const logStreamUrl = (query: Pick<LogQuery, 'level' | 'area'>) => `/api/v1/logs/stream${logQuery(query)}`
export const getLogSettings = (signal?: AbortSignal) => apiGet<LogSettings>('/logs/settings', signal)
export const saveLogSettings = (changes: LogSettingsUpdate) => request<LogSettings>('PUT', '/logs/settings', changes)
export const getLogUploads = (signal?: AbortSignal) => apiGet<LogUpload[]>('/logs/uploads', signal)
export const logUploadUrl = (id: string) => `/api/v1/logs/uploads/${encodeURIComponent(id)}`
export const diagnosticsUrl = '/api/v1/logs/diagnostics'

/**
 * Sends this browser's errors to the server's log, and says how it went (the status, or 0 when it never got there).
 * Not through `request`: that clears the cache, and a failure here must never throw. `keepalive` lets it finish as
 * the page goes away.
 */
export async function sendClientLogs(appVersion: string, entries: ClientLogEntry[], keepalive = false): Promise<number> {
  try {
    const response = await fetch('/api/v1/logs/client', {
      method: 'POST',
      keepalive,
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ platform: 'web', appVersion, entries }),
    })
    return response.status
  } catch {
    return 0
  }
}
