import type { RottenTomatoesState } from '../api/types'
import type { Scores } from '../lib/scores'
import { scoreMarks, type ScoreMarkDrawing } from './scoreMarks.generated'
import { locale, t, tk } from '../i18n'

const TOMATO_WORDS: Record<RottenTomatoesState, string> = { CertifiedFresh: tk('Certified Fresh'), Fresh: tk('Fresh'), Rotten: tk('Rotten') }
const TOMATO_MARKS: Record<RottenTomatoesState, ScoreMarkDrawing> = {
  CertifiedFresh: scoreMarks.certifiedFresh,
  Fresh: scoreMarks.fresh,
  Rotten: scoreMarks.rotten,
}

function Mark({ drawing }: { drawing: ScoreMarkDrawing }) {
  return (
    <svg className="score-mark" viewBox={`0 0 ${drawing.width} ${drawing.height}`} style={{ aspectRatio: `${drawing.width} / ${drawing.height}` }} aria-hidden="true">
      {drawing.paths.map((p, i) => (
        <path key={i} fill={p.fill} d={p.d} />
      ))}
    </svg>
  )
}

/** The scores as facts: each source's mark beside its number. Renders nothing when there are none. */
export function ScoreChips({ scores }: { scores: Scores }) {
  const { imdb, rottenTomatoes: rt } = scores
  return (
    <>
      {imdb !== null && (
        // role="img" so screen readers read the label: on a plain span many skip it, and the mark and number are hidden.
        <span className="score score-chip" role="img" aria-label={t('IMDb {score} out of 10', { score: imdb.toLocaleString(locale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })} title="IMDb">
          <Mark drawing={scoreMarks.imdb} />
          <span aria-hidden="true">{imdb.toFixed(1)}</span>
        </span>
      )}
      {rt && (
        <span
          className="score score-chip"
          role="img"
          aria-label={t('Rotten Tomatoes {score}%, {state}', { score: rt.score, state: t(TOMATO_WORDS[rt.state as RottenTomatoesState]) })}
          title={t('Rotten Tomatoes: {state}', { state: t(TOMATO_WORDS[rt.state as RottenTomatoesState]) })}
        >
          <Mark drawing={TOMATO_MARKS[rt.state as RottenTomatoesState]} />
          <span aria-hidden="true">{rt.score}%</span>
        </span>
      )}
    </>
  )
}
