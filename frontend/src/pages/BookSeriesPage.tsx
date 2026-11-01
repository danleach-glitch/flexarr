import type { CSSProperties } from 'react'
import { Link, useParams } from 'react-router'
import { getBookSeries, imageUrl } from '../api/client'
import { Artwork } from '../components/Artwork'
import { Check, Play } from '../components/icons'
import { Overview } from '../components/Overview'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'
import { authorsLine } from '../lib/books'
import { formatDuration, progressOf } from '../lib/format'

/** A series in reading order, with the next book this profile hasn't finished picked out. */
export function BookSeriesPage() {
  const { id = '' } = useParams()
  const { data, error, reload } = useApi((signal) => getBookSeries(id, signal), [id])
  useWatchedChanges(reload)
  const player = useAudioPlayer()

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="music-page" aria-busy="true" />

  const next = data.books.find((b) => b.book.id === data.nextId)?.book
  const finished = data.books.filter((b) => b.book.userData.watched).length
  const cover = (next ?? data.books[0]?.book)?.artwork.poster
  const accent = (next ?? data.books[0]?.book)?.accentColor

  return (
    <article className="music-page book-series-page" style={accent ? ({ '--ambient': accent } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {cover && <img src={imageUrl(cover, 500)} alt="" />}
      </div>
      <header className="book-series-header">
        <h1>{data.name}</h1>
        <p className="facts">
          <span>{data.books.length === 1 ? '1 book' : `${data.books.length} books`}</span>
          {finished > 0 && <span>{finished === data.books.length ? 'All finished' : `${finished} finished`}</span>}
        </p>
        {data.description && <Overview text={data.description} />}
        {next && (
          <div className="detail-actions">
            <button className="button" onClick={() => player.play([next])}>
              <Play /> {progressOf(next) !== null ? 'Resume' : 'Play'} {next.title}
            </button>
          </div>
        )}
      </header>
      <ol className="series-books">
        {data.books.map(({ book, sequence }) => {
          const isNext = book.id === data.nextId
          const progress = progressOf(book)
          return (
            <li key={book.id} className={isNext ? 'series-book next' : book.userData.watched ? 'series-book finished' : 'series-book'}>
              <span className="series-book-number">{sequence ?? ''}</span>
              <Link to={`/item/${book.id}`} className="series-book-cover" tabIndex={-1} aria-hidden="true">
                <Artwork src={book.artwork.poster} title={book.title} width={185} shape="square" />
              </Link>
              <span className="series-book-text">
                <Link to={`/item/${book.id}`} className="series-book-title">
                  {book.title}
                </Link>
                <span className="muted meta-line">
                  {[authorsLine(book), formatDuration(book.book?.durationSeconds ?? book.durationSeconds), book.year?.toString()].filter(Boolean).map((fact) => (
                    <span key={fact}>{fact}</span>
                  ))}
                </span>
                {progress !== null && !book.userData.watched && (
                  <span className="detail-progress" aria-label={`${Math.round(progress * 100)}% listened`}>
                    <span style={{ width: `${progress * 100}%` }} />
                  </span>
                )}
              </span>
              <span className="series-book-state">
                {book.userData.watched ? (
                  <span className="muted series-book-done">
                    <Check /> Finished
                  </span>
                ) : (
                  isNext && <span className="series-book-next">Up next</span>
                )}
              </span>
              <button className="icon-button" aria-label={`${progress !== null && !book.userData.watched ? 'Resume' : 'Play'} ${book.title}`} onClick={() => player.play([book])}>
                <Play />
              </button>
            </li>
          )
        })}
      </ol>
    </article>
  )
}
