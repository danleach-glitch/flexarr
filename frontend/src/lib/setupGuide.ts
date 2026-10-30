import type { SetupGuide, SetupStep } from '../api/client'

export const setupItems: { id: SetupStep; label: string }[] = [
  { id: 'apps', label: 'Connect Sonarr, Radarr or Lidarr' },
  { id: 'libraries', label: 'Add your libraries' },
  { id: 'profiles', label: 'Add everyone who watches' },
  { id: 'devices', label: 'Connect a TV or phone' },
  { id: 'away', label: 'Watch away from home' },
]

export const isDone = (guide: SetupGuide, id: SetupStep) => guide[id] || guide.skipped.includes(id)

/** Whether Home should list what's left: the guide was opened, not put away, and isn't finished. */
export const showsChecklist = (guide: SetupGuide | null | undefined): guide is SetupGuide =>
  !!guide && guide.opened && !guide.dismissed && setupItems.some((i) => !isDone(guide, i.id))
