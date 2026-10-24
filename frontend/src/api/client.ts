export interface SystemStatus {
  appName: string
  version: string
  osDescription: string
  runtimeVersion: string
  startTime: string
}

const API_KEY_STORAGE = 'flexarr.apiKey'

/** Thrown when the server rejects the request for a missing or wrong API key. */
export class UnauthorizedError extends Error {
  constructor() {
    super('API key required')
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

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const key = apiKey()
  const response = await fetch(`/api/v1${path}`, {
    signal,
    headers: key ? { 'X-Api-Key': key } : undefined,
  })
  if (response.status === 401) {
    throw new UnauthorizedError()
  }
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export const getSystemStatus = (signal?: AbortSignal) => apiGet<SystemStatus>('/system/status', signal)
