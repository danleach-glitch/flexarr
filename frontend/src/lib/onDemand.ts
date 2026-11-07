import { session } from './session'

/**
 * Loads code on demand. A tab opened before an update can ask for a file the update replaced; then the page reloads
 * once to pick up the new version rather than going blank. (Deploys also keep a week of old files.)
 */
export async function onDemand<T>(load: () => Promise<T>): Promise<T> {
  try {
    const loaded = await load()
    session.remove('flexarr.reloadedForUpdate')
    return loaded
  } catch (error) {
    if (!session.get('flexarr.reloadedForUpdate')) {
      session.set('flexarr.reloadedForUpdate', '1')
      location.reload()
      return new Promise<T>(() => {}) // the reload replaces this page
    }
    throw error
  }
}
