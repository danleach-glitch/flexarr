import { Link } from 'react-router'
import type { BookPerson, BookSeriesSummary } from '../api/types'
import { Artwork } from './Artwork'

/** A series: its first books' covers fanned behind one another, its name, and how far through it this profile is. */
export function BookSeriesCard({ series }: { series: BookSeriesSummary }) {
  const covers = series.covers.slice(0, 3)
  const count = series.bookCount === 1 ? '1 book' : `${series.bookCount} books`
  return (
    <div className="card book-series-card">
      <Link to={`/book-series/${series.id}`} className="card-art" aria-hidden="true" tabIndex={-1}>
        <div className={`book-stack book-stack-${Math.max(1, covers.length)}`}>
          {covers.length === 0 ? (
            <div className="card-frame">
              <Artwork src={null} title={series.name} width={342} shape="square" />
            </div>
          ) : (
            covers.map((cover, i) => (
              <div key={i} className="card-frame">
                <Artwork src={cover.poster} title={series.name} width={342} shape="square" />
              </div>
            ))
          )}
        </div>
      </Link>
      <span className="card-title">
        <Link to={`/book-series/${series.id}`}>{series.name}</Link>
      </span>
      <span className="card-subtitle">{series.finishedCount > 0 ? `${series.finishedCount} of ${count} finished` : count}</span>
    </div>
  )
}

/** An author or narrator: their photo, name and how many books they have here. */
export function BookPersonCard({ person }: { person: BookPerson }) {
  return (
    <Link to={`/person/${person.id}`} className="card person-card">
      <div className="card-frame">
        <Artwork src={person.image} title={person.name} width={185} shape="portrait" />
      </div>
      <span className="card-title">{person.name}</span>
      <span className="card-subtitle">{person.bookCount === 1 ? '1 book' : `${person.bookCount} books`}</span>
    </Link>
  )
}
