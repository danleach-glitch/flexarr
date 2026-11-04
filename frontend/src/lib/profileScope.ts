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
