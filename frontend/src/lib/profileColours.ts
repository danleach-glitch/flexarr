import type { Profile } from '../api/client'

export const profileColours = [
  { value: '#a9c2ec', name: 'Blue' },
  { value: '#e8a0bf', name: 'Pink' },
  { value: '#9fd8b9', name: 'Green' },
  { value: '#f2c879', name: 'Yellow' },
  { value: '#c7a6f2', name: 'Purple' },
  { value: '#f0a27e', name: 'Orange' },
  { value: '#86d0e0', name: 'Teal' },
]

/** A new profile's colour: the first no one has yet, so profiles look different in the picker. */
export function freeColour(profiles: Profile[]) {
  const taken = new Set(profiles.map((p) => p.color.toLowerCase()))
  return (profileColours.find((c) => !taken.has(c.value)) ?? profileColours[profiles.length % profileColours.length]).value
}
