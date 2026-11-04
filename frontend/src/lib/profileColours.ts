import type { Profile } from '../api/client'
import { t } from '../i18n'

export const profileColours = [
  {
    value: '#a9c2ec',
    get name() {
      return t('Blue')
    },
  },
  {
    value: '#e8a0bf',
    get name() {
      return t('Pink')
    },
  },
  {
    value: '#9fd8b9',
    get name() {
      return t('Green')
    },
  },
  {
    value: '#f2c879',
    get name() {
      return t('Yellow')
    },
  },
  {
    value: '#c7a6f2',
    get name() {
      return t('Purple')
    },
  },
  {
    value: '#f0a27e',
    get name() {
      return t('Orange')
    },
  },
  {
    value: '#86d0e0',
    get name() {
      return t('Teal')
    },
  },
]

/** A new profile's colour: the first no one has yet, so profiles look different in the picker. */
export function freeColour(profiles: Profile[]) {
  const taken = new Set(profiles.map((p) => p.color.toLowerCase()))
  return (profileColours.find((c) => !taken.has(c.value)) ?? profileColours[profiles.length % profileColours.length]).value
}
