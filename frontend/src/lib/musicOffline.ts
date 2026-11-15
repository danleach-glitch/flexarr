import { t } from '../i18n'

/** Lidarr not answering: named for the main profile, who can do something about it; plain words for everyone else. */
export const musicOfflineText = (admin: boolean) => (admin ? t('Lidarr isn’t answering.') : t('Music requests aren’t answering just now.'))
