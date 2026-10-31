import { useState, type CSSProperties } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  deleteBookmark,
  getBookListening,
  imageUrl,
  listenAgain,
  setBookFinished,
  type Credit,
  type ItemDetail,
  type ItemSummary,
} from '../api/client'
import { openAddToCollection } from '../components/AddToCollection'
import { Artwork } from '../components/Artwork'
import { BookmarkList } from '../components/BookmarkList'
import { AsyncButton } from '../components/AsyncButton'
import { PosterCard } from '../components/Cards'
import { ChapterEditor } from '../components/ChapterEditor'
import { DeleteDialog } from '../components/DeleteDialog'
import { FixMatchDialog } from '../components/FixMatchDialog'
import { Bookmark as BookmarkIcon, Check, Play } from '../components/icons'
import type { MenuAction } from '../components/ItemMenu'
import { Overview } from '../components/Overview'
import { Row } from '../components/Row'
import { useAudioPlayer, useAudioTime } from '../hooks/audioPlayerContext'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { announceWatched } from '../hooks/watched'
import { chapterIndexAt, chapterProgress, formatSpeed, seriesPlace, timeLeft } from '../lib/books'
import { formatClock, formatDuration } from '../lib/format'
import { MoreButton } from './MusicPages'

/**
 * A book's page: cover, title, its series and people, how long it is and how long is left at the speed it plays at,
 * its chapters (with where you are), bookmarks, and more from its series, author and narrator.
 */
export function BookPage({ detail, onChange }: { detail: ItemDetail; onChange: () => void }) {
  const { item } = detail
  const book = detail.book
  const player = useAudioPlayer()
  const time = useAudioTime()
  const navigate = useNavigate()
  const { me } = useSession()
  const listening = useApi(getBookListening, [])
  const [showBookmarks, setShowBookmarks] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [editing, setEditing] = useState<'chapters' | 'match' | null>(null)

  if (!book) return <p className="page-message muted">This book can’t be shown by this version of the app.</p>

  const playing = player.current?.id === item.id
  const duration = book.durationSeconds || item.durationSeconds || 0
  const finished = item.userData.watched
  const position = playing ? time : finished ? 0 : item.userData.positionSeconds
  const started = position > 0
  const speed = book.speed ?? listening.data?.speed ?? 1
  const current = chapterIndexAt(book.chapters, position)
  const authors = detail.credits.filter((c) => c.role === 'Author')
  const narrators = detail.credits.filter((c) => c.role === 'Narrator')
  const translators = detail.credits.filter((c) => c.role === 'Translator')
  const series = book.series[0]
  const mainProfile = me?.isAdmin === true || me?.canUnlock === true

  /** Plays from `at` (a chapter, a bookmark), or seeks there when the book is already playing. */
  const playAt = (at?: number) => {
    if (playing && at !== undefined) {
      player.seek(at)
      if (!player.playing) player.toggle()
    } else if (playing) player.toggle()
    else player.play([{ ...item, playFrom: at }])
  }

  async function mark(to: boolean) {
    await setBookFinished(item.id, to)
    announceWatched(item)
    onChange()
  }

  async function again() {
    await listenAgain(item.id)
    announceWatched(item)
    player.play([{ ...item, playFrom: 0, userData: { ...item.userData, watched: false, positionSeconds: 0 } }])
    onChange()
  }

  const more: MenuAction[] = [{ label: 'Add to collection…', run: () => openAddToCollection(item) }]
  if (started && !finished) more.push({ label: 'Play from the start', run: () => playAt(0) })
  if (mainProfile) {
    more.push({ label: 'Edit chapters…', run: () => setEditing('chapters') })
    // Its search is a read the server keeps to the unlocked main profile, so a locked one doesn't get to ask for the PIN.
    if (me?.isAdmin === true) more.push({ label: 'Fix match…', run: () => setEditing('match') })
    more.push('divider', { label: 'Delete…', run: () => setDeleting(true), danger: true })
  }

  const facts = [
    item.year?.toString(),
    formatDuration(duration),
    started && !finished ? `${formatDuration(timeLeft(duration, position, speed))} left${speed !== 1 ? ` at ${formatSpeed(speed)}` : ''}` : null,
    book.abridged ? 'Abridged' : null,
  ].filter(Boolean)
  // "und" is a tag's way of saying the language isn't known.
  const edition = [book.publisher, book.language === 'und' ? null : book.language].filter(Boolean)

  return (
    <article className="music-page book-page" style={item.accentColor ? ({ '--ambient': item.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {item.artwork.poster && <img src={imageUrl(item.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={item.artwork.poster} title={item.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          {series && (
            <p className="book-series-link">
              <Link to={`/book-series/${series.id}`}>{series.name}</Link>
              <span className="muted">{seriesPlace(series)}</span>
            </p>
          )}
          <h1>{item.title}</h1>
          {book.subtitle && <p className="book-subtitle">{book.subtitle}</p>}
          <People label="By" people={authors} />
          <People label="Read by" people={narrators} />
          <People label="Translated by" people={translators} />
          <p className="facts">
            {facts.map((f) => (
              <span key={f}>{f}</span>
            ))}
          </p>
          <div className="detail-actions">
            {finished ? (
              <AsyncButton className="button" onClick={again}>
                <Play /> Listen again
              </AsyncButton>
            ) : (
              <button className="button" onClick={() => playAt()}>
                <Play /> {playing && player.playing ? 'Pause' : started ? `Resume from ${formatClock(position)}` : 'Play'}
              </button>
            )}
            <AsyncButton className="button quiet" aria-pressed={finished} onClick={() => mark(!finished)}>
              <Check /> {finished ? 'Finished' : 'Mark finished'}
            </AsyncButton>
            {book.bookmarks.length > 0 && (
              <button className="button quiet" aria-expanded={showBookmarks} aria-controls="book-bookmarks" onClick={() => setShowBookmarks(!showBookmarks)}>
                <BookmarkIcon /> {book.bookmarks.length}
                <span className="visually-hidden">{book.bookmarks.length === 1 ? 'bookmark' : 'bookmarks'}</span>
              </button>
            )}
            <MoreButton label={`More for ${item.title}`} actions={more} />
          </div>
          {started && !finished && duration > 0 && (
            <span className="detail-progress" aria-label={`${Math.round((position / duration) * 100)}% listened`}>
              <span style={{ width: `${Math.min(100, (position / duration) * 100)}%` }} />
            </span>
          )}
        </div>
      </header>

      {showBookmarks && book.bookmarks.length > 0 && <BookmarkList id="book-bookmarks" bookmarks={book.bookmarks} chapters={book.chapters} onJump={playAt} onRemove={(id) => deleteBookmark(id).then(onChange)} />}

      <div className="book-body">
        {detail.overview && (
          <section className="book-about">
            <Overview text={detail.overview} />
            {edition.length > 0 && <p className="muted book-edition">{edition.join(' · ')}</p>}
          </section>
        )}
        {book.chapters.length > 0 && (
          <section className="book-chapters">
            <h2>{book.chapters.length === 1 ? '1 chapter' : `${book.chapters.length} chapters`}</h2>
            <ol className="tracklist">
              {book.chapters.map((chapter, i) => {
                const done = position >= chapter.endSeconds && chapter.endSeconds > 0
                const here = i === current && started
                return (
                  <li key={chapter.index}>
                    <div className="track-row">
                      <button className={here ? 'track current' : done ? 'track done' : 'track'} onClick={() => playAt(chapter.startSeconds)} aria-current={here || undefined}>
                        <span className="track-number">{here && playing && player.playing ? <Play /> : done ? <Check aria-label="Heard" /> : i + 1}</span>
                        <span className="track-title">
                          {chapter.title}
                          {here && <span className="chapter-progress" style={{ width: `${chapterProgress(book.chapters, i, position, duration) * 100}%` }} />}
                        </span>
                        <span className="track-time">{formatClock(chapter.endSeconds - chapter.startSeconds)}</span>
                      </button>
                    </div>
                  </li>
                )
              })}
            </ol>
          </section>
        )}
      </div>

      <div className="book-rows">
        <BookRow title={series ? `More in ${series.name}` : 'More in the series'} items={book.moreInSeries} link={series ? `/book-series/${series.id}` : undefined} />
        <BookRow title={authors[0] ? `By ${authors[0].name}` : 'By the author'} items={book.byAuthor} link={authors[0] ? `/person/${authors[0].personId}` : undefined} />
        <BookRow title={narrators[0] ? `Read by ${narrators[0].name}` : 'Read by the narrator'} items={book.readByNarrator} link={narrators[0] ? `/person/${narrators[0].personId}` : undefined} />
      </div>

      {editing === 'chapters' && <ChapterEditor bookId={item.id} chapters={book.chapters} hasAsin={!!book.asin} onSaved={onChange} onClose={() => setEditing(null)} />}
      {editing === 'match' && <FixMatchDialog item={item} onFixed={onChange} onClose={() => setEditing(null)} />}
      {deleting && <DeleteDialog item={item} onClose={() => setDeleting(false)} onDeleted={() => navigate(`/library/${item.libraryId}`, { replace: true })} />}
    </article>
  )
}

function People({ label, people }: { label: string; people: Credit[] }) {
  if (people.length === 0) return null
  return (
    <p className="book-people">
      <span className="muted">{label} </span>
      {people.map((p, i) => (
        <span key={p.personId}>
          {i > 0 && (i === people.length - 1 ? ' and ' : ', ')}
          <Link to={`/person/${p.personId}`}>{p.name}</Link>
        </span>
      ))}
    </p>
  )
}

function BookRow({ title, items, link }: { title: string; items: ItemSummary[]; link?: string }) {
  if (items.length === 0) return null
  return (
    <Row title={title} link={link}>
      {items.map((b) => (
        <PosterCard key={b.id} item={b} />
      ))}
    </Row>
  )
}
