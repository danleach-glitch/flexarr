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
 * Set in this tab by signing out, until its next page starts. The page that signed out is on its way out, and what it
 * would write as it goes (its queue, the place in a book still playing) is what was just forgotten. The next page isn't
 * always the sign-in form: at home, or over Tailscale, it may open signed in another way and would find them.
 */
const SIGNED_OUT_KEY = 'flexarr.signedOut'

/** Whether this page still keeps what's played here: not once it has signed out. */
export function keepsPlayed(): boolean {
  try {
    return sessionStorage.getItem(SIGNED_OUT_KEY) === null
  } catch {
    return true
  }
}

/**
 * Forgets what this browser kept about what was played here, for every profile: each book's place and the play queue,
 * which name titles by their ids. For when the browser is signed out, so whoever uses it next finds none of them.
 * Settings that say nothing of what was played (the language, sound, the sidebar) stay.
 *
 * With `signedOut` (this page signed out, and is about to go), this page keeps nothing more either.
 */
export function forgetPlayed({ signedOut = false } = {}) {
  try {
    for (const key of storedKeys(localStorage, BOOK_PLACE_KEY)) localStorage.removeItem(key)
  } catch {
    // Storage unavailable: nothing was kept.
  }
  try {
    for (const key of storedKeys(sessionStorage, QUEUE_KEY)) sessionStorage.removeItem(key)
    if (signedOut) sessionStorage.setItem(SIGNED_OUT_KEY, '1')
  } catch {
    // The same.
  }
}

/**
 * Forgets the book places and play queues this browser kept for profiles other than the one chosen here now, as they
 * name titles by their ids. For a change of profile: the next one (a child, say) finds none of the last one's. Settings
 * that say nothing of what was played (the language, sound, the sidebar) stay, as do the chosen profile's own.
 */
export function forgetOtherProfiles() {
  const own = profileKey('')
  const others = (key: string) => (own ? !key.endsWith(own) : key.includes('.p-'))
  try {
    for (const key of storedKeys(localStorage, BOOK_PLACE_KEY).filter(others)) localStorage.removeItem(key)
  } catch {
    // Storage unavailable: nothing was kept.
  }
  try {
    for (const key of storedKeys(sessionStorage, QUEUE_KEY).filter(others)) sessionStorage.removeItem(key)
  } catch {
    // The same.
  }
}

/**
 * For the start of a page: when the last page in this tab signed out, forgets anything it still left as it went, and
 * keeps what's played from here on as usual. Whatever the last page left for a profile no longer chosen here (it
 * switched profile on its way out) goes too.
 */
export function startKeepingPlayed() {
  if (keepsPlayed()) {
    forgetOtherProfiles()
    return
  }
  forgetPlayed()
  try {
    sessionStorage.removeItem(SIGNED_OUT_KEY)
  } catch {
    // Storage unavailable.
  }
}
