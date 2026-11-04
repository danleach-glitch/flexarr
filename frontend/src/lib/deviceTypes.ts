import type { DeviceType } from '../api/client'
import { t, tk } from '../i18n'

const names: Record<DeviceType, string> = {
  AppleTv: 'Apple TV',
  IPhone: 'iPhone',
  IPad: 'iPad',
  AndroidPhone: tk('Android phone'),
  AndroidTablet: tk('Android tablet'),
  AndroidTv: 'Android TV',
  Mac: 'Mac',
}

/** "Android phone", when the device's app has said what it is; undefined for nothing, or a kind newer than this page. */
export const deviceTypeName = (type: DeviceType | null | undefined): string | undefined =>
  type && Object.hasOwn(names, type) ? t(names[type]) : undefined
