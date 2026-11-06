/** Where the chosen profile's token is kept (see profileToken in the API client). */
export const PROFILE_STORAGE = 'flexarr.profile'

/**
 * A storage key for something that belongs to the profile chosen in this browser (a book's place, the music queue), so
 * another profile on the same computer never picks it up. The main profile (no token) keeps the plain key it always had.
 */
export function profileKey(base: string): string {
  let token: string | null = null
  try {
    token = localStorage.getItem(PROFILE_STORAGE)
  } catch {
    // Storage unavailable: as the main profile.
  }
  const profile = token ? token.slice(0, Math.max(0, token.lastIndexOf('.'))) || token : null
  return profile ? `${base}.p-${profile}` : base
}

/** Where this browser keeps its place in each book (books.ts): the book's id follows, then the profile. */
export const BOOK_PLACE_KEY = 'flexarr.bookPlace.'

/** Where a tab keeps the play queue for a reload (queue.ts): its order, and its tracks under a longer name. */
export const QUEUE_KEY = 'flexarr.musicQueue'

/** Every key in a storage area that starts with `prefix`. Listed first: removing while counting through skips some. */
export function storedKeys(storage: Storage, prefix: string): string[] {
  const keys: string[] = []
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i)
    if (key?.startsWith(prefix)) keys.push(key)
  }
  return keys
}

/**
 * Forgets what this browser kept about what was played here, for every profile: each book's place and the play queue,
 * which name titles by their ids. For when the browser is signed out, so whoever uses it next finds none of them.
 * Settings that say nothing of what was played (the language, sound, the sidebar) stay.
 */
export function forgetPlayed() {
  try {
    for (const key of storedKeys(localStorage, BOOK_PLACE_KEY)) localStorage.removeItem(key)
  } catch {
    // Storage unavailable: nothing was kept.
  }
  try {
    for (const key of storedKeys(sessionStorage, QUEUE_KEY)) sessionStorage.removeItem(key)
  } catch {
    // The same.
  }
}
