import type { GuestDevice, TailnetPath } from '../api/client'
import { relativeTime } from './format'
import { t } from '../i18n'

const pathLabel = (path: TailnetPath) =>
  ({
    Direct: t('Direct'),
    PeerRelay: t('Through your relay'),
    Derp: t('Slow relay'),
    Unknown: t('Offline'),
  })[path]

/** "Gran · Slow relay · last used 2 hours ago": a guest's device in Settings › Remote access. */
export function describeGuest(device: GuestDevice, withProfile = true) {
  const connection = device.online ? pathLabel(device.path) : t('Offline')
  return [withProfile ? device.profileName : null, connection, t('last used {when}', { when: relativeTime(device.lastUsedAt) })].filter(Boolean).join('\u2003')
}
