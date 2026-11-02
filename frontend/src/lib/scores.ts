import type { RottenTomatoes, RottenTomatoesState } from '../api/types'

/** A title's two scores (docs/design.md › Scores): IMDb out of 10 and Rotten Tomatoes critics with its tomato. */
export type Scores = { imdb: number | null; rottenTomatoes: RottenTomatoes | null }

const STATES: readonly RottenTomatoesState[] = ['CertifiedFresh', 'Fresh', 'Rotten']

/**
 * The tomato for a critics' score: the server's, or (from servers before 0.59, or a state this app doesn't know)
 * Fresh at 60% and up and Rotten below.
 */
export function tomatoOf(score: number, state?: string | null): RottenTomatoesState {
  return state && (STATES as readonly string[]).includes(state) ? (state as RottenTomatoesState) : score >= 60 ? 'Fresh' : 'Rotten'
}

/** A title's scores from what any server sends: older ones only have `criticRating`. */
export function scoresOf(item: {
  imdbRating?: number | null
  rottenTomatoes?: RottenTomatoes | null
  criticRating?: number | null
}): Scores {
  const rt = item.rottenTomatoes ?? (item.criticRating != null ? { score: item.criticRating, state: null } : null)
  return {
    imdb: item.imdbRating ?? null,
    rottenTomatoes: rt ? { score: rt.score, state: tomatoOf(rt.score, rt.state) } : null,
  }
}

export const hasScores = (s: Scores) => s.imdb !== null || s.rottenTomatoes !== null
