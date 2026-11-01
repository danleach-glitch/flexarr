import type { GuestDevice, TailnetPath } from '../api/client'
import { relativeTime } from './format'

const pathLabel: Record<TailnetPath, string> = {
  Direct: 'Direct',
  PeerRelay: 'Through your relay',
  Derp: 'Slow relay',
  Unknown: 'Offline',
}

/** "Gran · Slow relay · last used 2 hours ago": a guest's device in Settings › Remote access. */
export function describeGuest(device: GuestDevice, withProfile = true) {
  const connection = device.online ? pathLabel[device.path] : 'Offline'
  return [withProfile ? device.profileName : null, connection, `last used ${relativeTime(device.lastUsedAt)}`].filter(Boolean).join('\u2003')
}
