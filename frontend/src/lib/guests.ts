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

/** How long after its last use a household device last seen at home still reads as at home. */
const AT_HOME_FOR = 24 * 60 * 60 * 1000

/**
 * "Gran · Slow relay · last used 2 hours ago": a device that joined with a code, in Settings › Remote access. A
 * household device just says whether it's online: how it's connected is a guest's concern, whose quality it sets.
 */
export function describeGuest(device: GuestDevice, withProfile = true) {
  // A household device at home talks to the home address and stops its tunnel, so the tailnet calls it offline. Only
  // while it's been used lately, though: one switched off at home for days is offline like any other.
  const atHome = device.household && device.lastRoute === 'home' && Date.now() - Date.parse(device.lastUsedAt) < AT_HOME_FOR
  const connection =
    atHome
      ? t('At home')
      : !device.online
        ? t('Offline')
        : device.household
          ? device.lastRoute === 'away'
            ? t('Away from home')
            : t('Online')
          : pathLabel(device.path)
  return [withProfile ? device.profileName : null, connection, t('last used {when}', { when: relativeTime(device.lastUsedAt) })].filter(Boolean).join(' · ')
}
