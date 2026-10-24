export interface SystemStatus {
  appName: string
  version: string
  osDescription: string
  runtimeVersion: string
  startTime: string
}

const API_KEY_STORAGE = 'flexarr.apiKey'

/** The server accepts the key as an X-Api-Key header; local-network requests may not need one. */
function apiKey(): string | null {
  try {
    return localStorage.getItem(API_KEY_STORAGE)
  } catch {
    return null
  }
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const key = apiKey()
  const response = await fetch(`/api/v1${path}`, {
    signal,
    headers: key ? { 'X-Api-Key': key } : undefined,
  })
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export const getSystemStatus = (signal?: AbortSignal) => apiGet<SystemStatus>('/system/status', signal)
