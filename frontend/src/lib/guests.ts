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

/**
 * "Gran · Slow relay · last used 2 hours ago": a device that joined with a code, in Settings › Remote access. A
 * household device just says whether it's online: how it's connected is a guest's concern, whose quality it sets.
 */
export function describeGuest(device: GuestDevice, withProfile = true) {
  const connection = !device.online ? t('Offline') : device.household ? t('Online') : pathLabel(device.path)
  return [withProfile ? device.profileName : null, connection, t('last used {when}', { when: relativeTime(device.lastUsedAt) })].filter(Boolean).join(' · ')
}
