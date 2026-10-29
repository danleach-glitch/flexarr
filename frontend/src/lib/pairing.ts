import { useSyncExternalStore } from 'react'
import { ApiError, type PairingDetails } from '../api/client'

/** Where the device asked from, as "from your home network (192.168.1.70)". */
export function askedFrom(details: PairingDetails) {
  const place = details.zone === 'Local' ? 'from your home network' : details.zone === 'Tailnet' ? 'over Tailscale' : 'from the internet'
  return details.requestedFrom ? `${place} (${details.requestedFrom})` : place
}

export const pairingError = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'Only the main profile can connect a device.'
    : e instanceof ApiError && e.messages[0]
      ? e.messages[0]
      : String(e)

// The code Connect a device (Settings, or /link from a TV's QR code) is asking about, so the app-wide prompt doesn't
// ask about the same device twice on one screen.
let open: string | undefined
const listeners = new Set<() => void>()

export function setPairingOpenElsewhere(code: string | undefined) {
  if (open === code) return
  open = code
  listeners.forEach((notify) => notify())
}

const subscribe = (notify: () => void) => {
  listeners.add(notify)
  return () => {
    listeners.delete(notify)
  }
}

export const usePairingOpenElsewhere = () => useSyncExternalStore(subscribe, () => open)
