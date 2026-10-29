import type { DeviceType } from '../api/client'

const names: Record<DeviceType, string> = {
  AppleTv: 'Apple TV',
  IPhone: 'iPhone',
  IPad: 'iPad',
  AndroidPhone: 'Android phone',
  AndroidTablet: 'Android tablet',
  AndroidTv: 'Android TV',
}

/** "Android phone", when the device's app has said what it is; undefined for nothing, or a kind newer than this page. */
export const deviceTypeName = (type: DeviceType | null | undefined): string | undefined =>
  type && Object.hasOwn(names, type) ? names[type] : undefined
