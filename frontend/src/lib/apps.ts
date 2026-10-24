import lidarr from '../assets/apps/lidarr.svg'
import radarr from '../assets/apps/radarr.svg'
import seerr from '../assets/apps/seerr.svg'
import sonarr from '../assets/apps/sonarr.svg'
import type { ArrKind } from '../api/types'

/** Each app's logo, from its own repository (GPL-3.0 for the *arr apps, MIT for Seerr). Radarr's is the one its dark theme uses. */
export const appLogos: Record<ArrKind | 'Seerr', string> = { Sonarr: sonarr, Radarr: radarr, Lidarr: lidarr, Seerr: seerr }

const loopback = /^(localhost|127(\.\d+){3}|\[::1\])$/i

/**
 * Where the browser opens an app at this address, or nothing if it isn't a web address. An app at localhost is only
 * localhost to this server, so the browser goes to the host it reached Flexarr on, at the app's port (as the links
 * on Coming soon do).
 */
export function appLink(baseUrl: string, here = window.location.hostname): string | undefined {
  let url: URL
  try {
    url = new URL(baseUrl.trim())
  } catch {
    return undefined
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return undefined
  if (loopback.test(url.hostname) && here) url.hostname = here
  return url.toString()
}
