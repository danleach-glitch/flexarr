import { ApiError } from '../api/client'
import { t, Words } from '../i18n'

/**
 * What went wrong, for people to read: the server's own words when it gave some, and otherwise translated words rather
 * than the browser's ("Failed to fetch", "Load failed", "NetworkError…") or a bare "502 Bad Gateway".
 */
export function errorText(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.messages.length) return e.messages.join(' ')
    // A proxy in front of the server that heard nothing from it.
    if (e.status === 502 || e.status === 504) return t(Words.serverDidntAnswer)
    if (e.status >= 500) return t('Flexarr had a problem. Try again.')
    return e.message
  }
  // fetch() rejects with a TypeError when the request never got an answer (server down, network gone, laptop asleep).
  if (e instanceof TypeError && /fetch|load failed|network/i.test(e.message)) return t(Words.serverDidntAnswer)
  if (e instanceof Error) return e.message
  return String(e)
}
