import type { DeviceType } from '../api/client'
import { Phone, Tablet, Tv } from './icons'

/** A phone, a tablet, or a TV for the rest (and until a device says, as the Apple TV was the first). */
export function DeviceTypeIcon({ type }: { type: DeviceType | null | undefined }) {
  return type === 'IPhone' || type === 'AndroidPhone' ? <Phone />
    : type === 'IPad' || type === 'AndroidTablet' ? <Tablet />
    : <Tv />
}
