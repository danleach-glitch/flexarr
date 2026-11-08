import { lazy, Suspense, type ComponentType } from 'react'
import { onDemand } from '../lib/onDemand'

/**
 * A dialog whose code loads the first time it opens, so the first page doesn't carry for everyone what only the main
 * profile opens now and then. Nothing shows while it loads, and the page under it stays as it is; from then on it
 * opens at once.
 */
function dialog<P extends object>(load: () => Promise<ComponentType<P>>): ComponentType<P> {
  const Loaded = lazy(() => onDemand(async () => ({ default: (await load()) as ComponentType<object> })))
  return function OnDemand(props: P) {
    return (
      <Suspense fallback={null}>
        <Loaded {...props} />
      </Suspense>
    )
  }
}

export const DetailsEditor = dialog(() => import('./DetailsEditor').then((m) => m.DetailsEditor))
export const FixMatchDialog = dialog(() => import('./FixMatchDialog').then((m) => m.FixMatchDialog))
export const MusicMatchDialog = dialog(() => import('./MusicMatchDialog').then((m) => m.MusicMatchDialog))
export const PersonEditor = dialog(() => import('./PersonEditor').then((m) => m.PersonEditor))
export const BookEditor = dialog(() => import('./BookEditor').then((m) => m.BookEditor))
export const ChapterEditor = dialog(() => import('./ChapterEditor').then((m) => m.ChapterEditor))
