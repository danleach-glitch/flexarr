import type { DirectoryListing, ItemDetail, ItemSummary, Library, LibraryRequest, Paged, SystemStatus } from './types'

export type * from './types'

const API_KEY_STORAGE = 'flexarr.apiKey'

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
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const response = await fetch(`/api/v1${path}`, {
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
      const problem = (await response.json()) as { errors?: Record<string, string[]>; title?: string }
      messages = problem.errors ? Object.values(problem.errors).flat() : problem.title ? [problem.title] : []
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
export const getLibrary = (id: string, signal?: AbortSignal) => apiGet<Library>(`/libraries/${id}`, signal)
export const createLibrary = (library: LibraryRequest) => request<Library>('POST', '/libraries', library)
export const updateLibrary = (id: string, library: LibraryRequest) => request<Library>('PUT', `/libraries/${id}`, library)
export const deleteLibrary = (id: string) => request<void>('DELETE', `/libraries/${id}`)
export const scanLibrary = (id: string) => request<void>('POST', `/libraries/${id}/scan`)

export function getLibraryItems(id: string, options: { search?: string; skip?: number; take?: number }, signal?: AbortSignal) {
  const params = new URLSearchParams()
  if (options.search) params.set('search', options.search)
  if (options.skip) params.set('skip', String(options.skip))
  if (options.take) params.set('take', String(options.take))
  const query = params.size ? `?${params}` : ''
  return apiGet<Paged<ItemSummary>>(`/libraries/${id}/items${query}`, signal)
}

export const getRecentItems = (take = 24, signal?: AbortSignal) => apiGet<ItemSummary[]>(`/items/recent?take=${take}`, signal)
export const getItem = (id: string, signal?: AbortSignal) => apiGet<ItemDetail>(`/items/${id}`, signal)

export const browseFilesystem = (path?: string, signal?: AbortSignal) =>
  apiGet<DirectoryListing>(`/filesystem${path ? `?path=${encodeURIComponent(path)}` : ''}`, signal)
