import type { SetupGuide, SetupStep } from '../api/client'

/** What's left once the guide is put away, each with where it's done in Settings. */
export const setupItems: { id: SetupStep; label: string; settings: string }[] = [
  { id: 'apps', label: 'Connect Sonarr, Radarr or Lidarr', settings: '/settings/connections' },
  { id: 'libraries', label: 'Add your libraries', settings: '/settings/libraries' },
  { id: 'profiles', label: 'Add everyone who watches', settings: '/settings/profiles' },
  { id: 'devices', label: 'Connect a TV or phone', settings: '/settings/tv' },
  { id: 'away', label: 'Watch away from home', settings: '/settings/remote' },
]

export const isDone = (guide: SetupGuide, id: SetupStep) => guide[id] || guide.skipped.includes(id)

/** Whether Home should list what's left: the guide was opened, not put away, and isn't finished. */
export const showsChecklist = (guide: SetupGuide | null | undefined): guide is SetupGuide =>
  !!guide && guide.opened && !guide.dismissed && setupItems.some((i) => !isDone(guide, i.id))

/**
 * A folder too broad to be a library: a whole disk, or someone's home folder. Reading all of it takes hours and finds
 * everything but the films. Folders like /data or /media are left alone, since media often lives right there.
 */
export function tooBroad(path: string) {
  const p = path.replace(/[\\/]+$/, '') || '/'
  return (
    p === '/' ||
    /^[A-Za-z]:$/.test(p) ||
    /^\/(Volumes|Users|home|System|Library|Applications|private)$/.test(p) ||
    /^\/Volumes\/[^/]+$/.test(p) ||
    /^\/(Users|home)\/[^/]+$/.test(p) ||
    /^\/root$/.test(p)
  )
}
